using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace BadNews.Repositories.News
{
    public class NewsRepository : INewsRepository
    {
        private static readonly string recordSeparator = "<--e891b395-4498-4f93-84a5-19b867d826ae-->";
        private readonly string dataFileName;
        private string DataFilePath => $"./.db/{dataFileName}";

        private object dataFileLocker = new object();

        private static Dictionary<Guid, long> index = new();

        public NewsRepository(string dataFileName = "news.txt")
        {
            this.dataFileName = dataFileName;
            RecalculateIndex();
        }

        public void InitializeDataBase(IEnumerable<NewsArticle> articles)
        {
            var dataFileInfo = new FileInfo(DataFilePath);
            if (dataFileInfo.Exists)
                dataFileInfo.Delete();
            if (!dataFileInfo.Directory.Exists)
                dataFileInfo.Directory.Create();

            lock (dataFileLocker)
            {
                var file = dataFileInfo.Open(FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using (var fileWriter = new StreamWriter(file))
                {
                    foreach (var article in articles)
                    {
                        var storedArticle = new NewsArticle(article);
                        if (storedArticle.Id == Guid.Empty)
                            storedArticle.Id = Guid.NewGuid();
                        AppendArticle(fileWriter, storedArticle);
                    }
                }
            }
        }

        private void RecalculateIndex()
        {
            if (!File.Exists(DataFilePath))
                return;

            long curByte = 0;
            bool nextIsId = false;

            lock (dataFileLocker)
            {
                var file = new FileStream(DataFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using (var fileReader = new SeekableStreamTextReader(file, Encoding.UTF8))
                {
                    string line = fileReader.ReadLine();
                    while (line != null)
                    {
                        if (nextIsId)
                        {
                            var guid = Guid.Parse(line);
                            index[guid] = curByte;
                        }
                        nextIsId = line == recordSeparator;
                        curByte = fileReader.UsedBytes;
                        line = fileReader.ReadLine();
                    }
                }
            }
        }

        public NewsArticle GetArticleById(Guid id)
        {
            if (!index.ContainsKey(id))
                return null;
            NewsArticle article = null;

            var idString = id.ToString();
            ReadFromFile((meta, data) =>
            {
                if (idString == meta.ToString())
                {
                    var obj = JsonConvert.DeserializeObject<NewsArticle>(data.ToString());
                    if (id != obj.Id)
                        throw new InvalidDataException();
                    article = obj;
                    return true;
                }
                return false;
            }, index[id]);

            return article != null && !article.IsDeleted ? article : null;
        }

        public IList<NewsArticle> GetArticles(Func<NewsArticle, bool> predicate = null)
        {
            var articles = new Dictionary<Guid, NewsArticle>();

            ReadFromFile((meta, data) =>
            {
                var id = Guid.Parse(meta.ToString());
                var obj = JsonConvert.DeserializeObject<NewsArticle>(data.ToString());
                if (id != obj.Id)
                    throw new InvalidDataException();
                if (obj.IsDeleted || predicate == null || predicate(obj))
                articles[id] = obj;
                return false;
            });

            return articles
                .Where(it => !it.Value.IsDeleted)
                .Select(it => it.Value)
                .OrderByDescending(it => it.Date)
                .ToList();
        }

        public IList<int> GetYearsWithArticles()
        {
            var years = new Dictionary<Guid, DateTime>();

            ReadFromFile((meta, data) =>
            {
                var obj = JsonConvert.DeserializeObject<NewsArticle>(data.ToString());
                if (!obj.IsDeleted)
                    years[obj.Id] = obj.Date;
                else
                    years.Remove(obj.Id);
                return false;
            });

            return years.Select(it => it.Value.Year)
                .Distinct()
                .OrderByDescending(it => it)
                .ToArray();
        }

        public Guid CreateArticle(NewsArticle article)
        {
            if (article.Id != Guid.Empty)
                throw new InvalidOperationException("Creating article should not have id");

            lock (dataFileLocker)
            {
                var file = new FileStream(DataFilePath, FileMode.Append, FileAccess.Write, FileShare.None);
                using (var fileWriter = new StreamWriter(file))
                {
                    var storedArticle = new NewsArticle(article)
                    {
                        Id = Guid.NewGuid()
                    };
                    index[storedArticle.Id] = AppendArticle(fileWriter, storedArticle);

                    return storedArticle.Id;
                }
            }
        }

        public void DeleteArticleById(Guid id)
        {
            lock (dataFileLocker)
            {
                var file = new FileStream(DataFilePath, FileMode.Append, FileAccess.Write, FileShare.None);
                using (var fileWriter = new StreamWriter(file))
                {
                    var storedArticle = new NewsArticle()
                    {
                        Id = id,
                        IsDeleted = true
                    };
                    index.Remove(id);
                    AppendArticle(fileWriter, storedArticle);
                }
            }
        }

        private static long AppendArticle(StreamWriter file, NewsArticle article)
        {
            long pos = 0;
            var meta = article.Id.ToString();
            var data = JsonConvert.SerializeObject(article, Formatting.Indented);
            file.WriteLine(recordSeparator);
            pos = file.BaseStream.Position;
            file.WriteLine(meta);
            file.WriteLine(data);
            return pos;
        }

        private void ReadFromFile(
            Func<StringBuilder, StringBuilder, bool> onObjectRead, long startPos = 0)
        {
            if (!File.Exists(DataFilePath))
                return;

            lock (dataFileLocker)
            {
                var file = new FileStream(DataFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using (var fileReader = new SeekableStreamTextReader(file, Encoding.UTF8))
                {
                    fileReader.Seek(startPos, SeekOrigin.Begin);
                    int objectLine = 0;
                    var metaBuilder = new StringBuilder();
                    var dataBuilder = new StringBuilder();

                    string line = fileReader.ReadLine();
                    while (line != null)
                    {
                        if (line != recordSeparator)
                        {
                            if (objectLine++ > 0)
                            {
                                dataBuilder.Append(line);
                            }
                            else
                            {
                                metaBuilder.Append(line);
                            }
                        }
                        else
                        {
                            if (metaBuilder.Length > 0 || dataBuilder.Length > 0)
                            {
                                if (onObjectRead(metaBuilder, dataBuilder))
                                    return;
                            }

                            objectLine = 0;
                            metaBuilder = new StringBuilder();
                            dataBuilder = new StringBuilder();
                        }

                        line = fileReader.ReadLine();
                    }

                    if (dataBuilder.Length > 0)
                    {
                        if (onObjectRead(metaBuilder, dataBuilder))
                            return;
                    }
                }
            }
        }
    }
}
