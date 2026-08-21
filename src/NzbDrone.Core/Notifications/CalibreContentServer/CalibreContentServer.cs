using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using FluentValidation.Results;
using Newtonsoft.Json;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.Notifications.CalibreContentServer
{
    public class CalibreContentServer : NotificationBase<CalibreContentServerSettings>
    {
        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;

        public CalibreContentServer(IHttpClient httpClient, Logger logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public override string Name => "Calibre Content Server";
        public override string Link => "https://manual.calibre-ebook.com/server.html";

        public override void OnReleaseImport(BookDownloadMessage message)
        {
            var ebooks = message.BookFiles.Where(x => QualityMediaTypeHelper.IsEbookFileQuality(x.Quality.Quality)).ToList();

            if (!ebooks.Any())
            {
                return;
            }

            if (Settings.SyncChanges && message.OldFiles?.Any() == true)
            {
                DeleteBook(message.Book);
            }

            foreach (var file in ebooks)
            {
                AddBook(file.Path);
            }
        }

        public override void OnBookDelete(BookDeleteMessage message)
        {
            if (Settings.SyncChanges)
            {
                DeleteBook(message.Book);
            }
        }

        public override void OnBookFileDelete(BookFileDeleteMessage message)
        {
            if (Settings.SyncChanges && QualityMediaTypeHelper.IsEbookFileQuality(message.BookFile.Quality.Quality))
            {
                DeleteBook(message.Book);
            }
        }

        public override void OnBookRetag(BookRetagMessage message)
        {
            if (Settings.SyncChanges && QualityMediaTypeHelper.IsEbookFileQuality(message.BookFile.Quality.Quality))
            {
                DeleteBook(message.Book);
                AddBook(message.BookFile.Path);
            }
        }

        public override ValidationResult Test()
        {
            var failures = new List<ValidationFailure>();

            try
            {
                var request = BuildRequest("ajax/library-info").Build();
                _httpClient.Get(request);
            }
            catch (HttpException ex) when (ex.Response?.StatusCode == HttpStatusCode.Unauthorized)
            {
                failures.Add(new ValidationFailure("Username", "Authentication failed"));
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Unable to connect to Calibre content server");
                failures.Add(new ValidationFailure("Url", "Unable to connect: " + ex.Message));
            }

            return new ValidationResult(failures);
        }

        private void AddBook(string path)
        {
            var jobId = (int)(DateTime.UtcNow.Ticks % 1000000000);
            var filename = Uri.EscapeDataString(Path.GetFileName(path));
            var request = BuildRequest($"cdb/add-book/{jobId}/0/{filename}").Build();
            request.SetContent(File.ReadAllBytes(path));

            var response = _httpClient.Post<CalibreImportJob>(request).Resource;

            if (response.Id == 0)
            {
                _logger.Info("Calibre content server reported {0} as a duplicate, skipped", path);
            }
            else
            {
                _logger.Debug("Pushed {0} to Calibre content server as book {1}", path, response.Id);
            }
        }

        private void DeleteBook(Book book)
        {
            var author = book?.Author?.Name;

            if (author.IsNullOrWhiteSpace() || book.Title.IsNullOrWhiteSpace())
            {
                return;
            }

            var query = Uri.EscapeDataString($"authors:\"={author.Replace("\"", "")}\"");
            var searchRequest = BuildRequest($"ajax/search?query={query}").Build();
            var ids = _httpClient.Get<CalibreSearchResult>(searchRequest).Resource.BookIds;

            if (ids?.Any() != true)
            {
                _logger.Debug("No matching book found on Calibre content server for {0}", book.Title);
                return;
            }

            var titles = TitleForms(book.Title);
            var booksRequest = BuildRequest($"ajax/books?ids={string.Join(",", ids)}").Build();
            var calibreBooks = _httpClient.Get<Dictionary<string, CalibreBookData>>(booksRequest).Resource;
            var matches = calibreBooks.Where(x => x.Value != null && TitleForms(x.Value.Title).Intersect(titles).Any()).Select(x => x.Key).ToList();

            if (matches.Any())
            {
                _httpClient.Post(BuildRequest($"cdb/delete-books/{string.Join(",", matches)}").Build());
                _logger.Info("Deleted book {0} from Calibre content server (calibre ids: {1})", book.Title, string.Join(",", matches));
            }
            else
            {
                _logger.Info("No matching book found on Calibre content server for {0}", book.Title);
            }
        }

        private static string[] TitleForms(string title)
        {
            return new[] { Normalize(title), Normalize(Regex.Replace(title ?? string.Empty, @"\s*\([^)]*\)\s*$", "")) };
        }

        private static string Normalize(string title)
        {
            return Regex.Replace(title?.ToLowerInvariant() ?? string.Empty, "[^a-z0-9]", "");
        }

        private HttpRequestBuilder BuildRequest(string relativePath)
        {
            var builder = new HttpRequestBuilder(HttpUri.CombinePath(Settings.Url, relativePath))
                .Accept(HttpAccept.Json);

            if (Settings.Username.IsNotNullOrWhiteSpace())
            {
                builder.NetworkCredential = new NetworkCredential(Settings.Username, Settings.Password);
            }

            return builder;
        }

        private class CalibreSearchResult
        {
            [JsonProperty("book_ids")]
            public List<int> BookIds { get; set; }
        }

        private class CalibreBookData
        {
            [JsonProperty("title")]
            public string Title { get; set; }
        }
    }
}
