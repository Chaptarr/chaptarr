using System;
using System.Collections.Concurrent;
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
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.Notifications.CalibreContentServer
{
    public class CalibreContentServer : NotificationBase<CalibreContentServerSettings>
    {
        // BookFileAddedEvent also fires for tracked downloads, which push through
        // OnReleaseImport on another handler thread; instances are transient, so the
        // claim set is static and keyed per definition.
        private static readonly ConcurrentDictionary<string, DateTime> RecentlyPushedPaths = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan RecentPushWindow = TimeSpan.FromMinutes(5);

        private readonly IHttpClient _httpClient;
        private readonly IRootFolderService _rootFolderService;
        private readonly IMapCoversToLocal _coverMapper;
        private readonly Logger _logger;

        public CalibreContentServer(IHttpClient httpClient, IRootFolderService rootFolderService, IMapCoversToLocal coverMapper, Logger logger)
        {
            _httpClient = httpClient;
            _rootFolderService = rootFolderService;
            _coverMapper = coverMapper;
            _logger = logger;
        }

        public override string Name => "Calibre Content Server";
        public override string Link => "https://manual.calibre-ebook.com/server.html";

        public override bool NotifyOnLibraryImports => Settings.PushLibraryImports;

        public override void OnReleaseImport(BookDownloadMessage message)
        {
            var ebooks = message.BookFiles.Where(x => QualityMediaTypeHelper.IsEbookFileQuality(x.Quality.Quality)).ToList();

            if (!ebooks.Any())
            {
                return;
            }

            var mirrorBookId = 0;

            foreach (var file in ebooks)
            {
                if (!TryClaimPush(file.Path))
                {
                    continue;
                }

                mirrorBookId = PushFile(mirrorBookId, message.Book, file.Path);
            }

            if (Settings.SyncChanges && mirrorBookId > 0 && message.OldFiles?.Any() == true)
            {
                RemoveReplacedFormats(mirrorBookId, message.OldFiles, ebooks);
            }
        }

        private void RemoveReplacedFormats(int calibreId, List<NzbDrone.Core.MediaFiles.BookFile> oldFiles, List<NzbDrone.Core.MediaFiles.BookFile> newFiles)
        {
            // An upgrade must not delete and re-add the whole record - that would churn
            // the calibre id and destroy whatever the server side attached to it. The
            // new files overwrite their formats in place; only drop extensions the
            // upgrade no longer provides.
            var newExtensions = newFiles
                .Select(f => (Path.GetExtension(f?.Path) ?? string.Empty).TrimStart('.'))
                .Where(e => e.IsNotNullOrWhiteSpace())
                .ToList();

            var staleExtensions = oldFiles
                .Select(f => (Path.GetExtension(f?.Path) ?? string.Empty).TrimStart('.'))
                .Where(e => e.IsNotNullOrWhiteSpace() && !newExtensions.Contains(e, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var extension in staleExtensions)
            {
                try
                {
                    RemoveFormat(calibreId.ToString(), extension);
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Unable to remove the stale {0} format from Calibre content server book {1}", extension, calibreId);
                }
            }
        }

        private bool TryClaimPush(string path)
        {
            var now = DateTime.UtcNow;

            foreach (var stale in RecentlyPushedPaths.Where(p => now - p.Value > RecentPushWindow).Select(p => p.Key).ToList())
            {
                RecentlyPushedPaths.TryRemove(stale, out _);
            }

            return RecentlyPushedPaths.TryAdd($"{Definition.Id}:{path}", now);
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
                DeleteFormat(message.Book, message.BookFile.Path);
            }
        }

        public override void OnLibraryFileAdded(NzbDrone.Core.MediaFiles.BookFile bookFile, Book book)
        {
            if (!QualityMediaTypeHelper.IsEbookFileQuality(bookFile.Quality.Quality))
            {
                return;
            }

            if (!TryClaimPush(bookFile.Path))
            {
                return;
            }

            PushFile(0, book, bookFile.Path);
        }

        public bool RePush(Book book, List<NzbDrone.Core.MediaFiles.BookFile> files, bool metadataOnly = false)
        {
            var mirrorBookId = FindMirrorBookIds(book).Select(int.Parse).FirstOrDefault();

            if (metadataOnly && mirrorBookId > 0)
            {
                // Library edits only change the record; re-uploading every format on
                // each refresh would send whole files for nothing.
                SetCanonicalMetadata(mirrorBookId, book);
                return true;
            }

            foreach (var file in files)
            {
                mirrorBookId = PushFile(mirrorBookId, book, file.Path);
            }

            if (mirrorBookId > 0)
            {
                SetCanonicalMetadata(mirrorBookId, book);
                return true;
            }

            return false;
        }

        public override void OnBookRetag(BookRetagMessage message)
        {
            if (Settings.SyncChanges && QualityMediaTypeHelper.IsEbookFileQuality(message.BookFile.Quality.Quality))
            {
                PushFile(0, message.Book, message.BookFile.Path);
            }
        }

        public override ValidationResult Test()
        {
            var failures = new List<ValidationFailure>();

            var conflictingRootFolder = FindSameServerRootFolder();

            if (conflictingRootFolder != null)
            {
                failures.Add(new ValidationFailure("Url", $"This Calibre content server already backs the root folder '{conflictingRootFolder.Path}'. Chaptarr manages that library directly, so pushing to it again would create duplicates."));
                return new ValidationResult(failures);
            }

            try
            {
                // Book id 0 never exists, so this exercises cdb write permissions
                // without ever creating or touching a record.
                var request = BuildRequest("cdb/delete-books/0").Build();
                var anonymousProbe = Settings.Username.IsNullOrWhiteSpace();

                if (anonymousProbe)
                {
                    request.Credentials = new NetworkCredential("chaptarr-connection-test", Guid.NewGuid().ToString("N"));
                }

                request.SuppressHttpError = true;
                request.SetContent(Array.Empty<byte>());
                var response = _httpClient.Post(request);

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    failures.Add(anonymousProbe
                        ? new ValidationFailure("Username", "The content server requires authentication, enter a username and password")
                        : new ValidationFailure("Username", "Authentication failed"));
                }
                else if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    failures.Add(new ValidationFailure("Username", "The content server does not accept anonymous changes, a username and password are required to push books"));
                }
                else if (response.StatusCode == HttpStatusCode.NotFound || ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400))
                {
                    failures.Add(new ValidationFailure("Url", "Not a Calibre content server, check the URL"));
                }
                else if ((int)response.StatusCode >= 500)
                {
                    failures.Add(new ValidationFailure("Url", $"The content server returned {(int)response.StatusCode} to a Calibre API request"));
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Unable to connect to Calibre content server");
                failures.Add(new ValidationFailure("Url", "Unable to connect: " + ex.Message));
            }

            return new ValidationResult(failures);
        }

        private RootFolder FindSameServerRootFolder()
        {
            if (!Uri.TryCreate(Settings.Url, UriKind.Absolute, out var url))
            {
                return null;
            }

            var port = url.IsDefaultPort ? (url.Scheme == Uri.UriSchemeHttps ? 443 : 80) : url.Port;

            return _rootFolderService.All()
                .Where(f => f.IsCalibreLibrary && f.CalibreSettings != null && f.CalibreSettings.Host.IsNotNullOrWhiteSpace())
                .FirstOrDefault(f => f.CalibreSettings.Port == port && HostsMatch(f.CalibreSettings.Host, url.Host));
        }

        private static bool HostsMatch(string a, string b)
        {
            if (a.Equals(b, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return IsLoopbackHost(a) && IsLoopbackHost(b);
        }

        private static bool IsLoopbackHost(string host)
        {
            if (host.IsNullOrWhiteSpace())
            {
                return false;
            }

            var trimmed = host.Trim().Trim('[', ']');

            if (trimmed.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return IPAddress.TryParse(trimmed, out var ip) && IPAddress.IsLoopback(ip);
        }

        private int AddBook(string path)
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

            return response.Id;
        }

        private int PushFile(int knownMirrorId, Book book, string path)
        {
            var mirrorId = knownMirrorId;

            if (mirrorId == 0)
            {
                mirrorId = FindMirrorBookIds(book).Select(int.Parse).FirstOrDefault();
            }

            if (mirrorId > 0)
            {
                PushFormat(mirrorId, path);
                return mirrorId;
            }

            var added = AddBook(path);

            if (added > 0)
            {
                SetCanonicalMetadata(added, book);
            }

            return added;
        }

        private void SetCanonicalMetadata(int calibreId, Book book)
        {
            var title = book?.Title;
            var author = book?.Author?.Name;

            if (title.IsNullOrWhiteSpace() && author.IsNullOrWhiteSpace())
            {
                return;
            }

            try
            {
                var serieslink = PickSeriesLink(book);
                double? seriesIndex = null;

                if (double.TryParse(serieslink?.Position, out var parsedIndex))
                {
                    seriesIndex = parsedIndex;
                }

                var edition = book?.Editions?.FirstOrDefault(e => e.Monitored) ?? book?.Editions?.FirstOrDefault();

                var identifiers = new Dictionary<string, string>();
                if (edition?.Isbn13.IsNotNullOrWhiteSpace() == true) { identifiers["isbn"] = edition.Isbn13; }
                if (edition?.Asin.IsNotNullOrWhiteSpace() == true) { identifiers["asin"] = edition.Asin; }
                if (edition?.ForeignEditionId.IsNotNullOrWhiteSpace() == true) { identifiers["goodreads"] = edition.ForeignEditionId; }

                var payload = new CalibreChangesPayload
                {
                    LoadedBookIds = new List<int> { calibreId },
                    Changes = new CalibreChanges
                    {
                        Title = title,
                        Authors = author.IsNullOrWhiteSpace() ? null : new List<string> { author },
                        Series = serieslink?.Series.Value.Title,
                        SeriesIndex = seriesIndex,
                        Cover = GetCanonicalCover(book),
                        Comments = edition?.Overview,
                        Publisher = edition?.Publisher,
                        PubDate = book?.ReleaseDate,
                        Tags = book?.Genres,
                        Identifiers = identifiers.Any() ? identifiers : null
                    }
                };

                var request = BuildRequest($"cdb/set-fields/{calibreId}")
                    .SetHeader("Content-Type", "application/json")
                    .Build();
                request.SetContent(payload.ToJson());
                _httpClient.Post(request);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Unable to set canonical metadata on Calibre content server book {0}", calibreId);
            }
        }

        private string GetCanonicalCover(Book book)
        {
            try
            {
                var edition = book?.Editions?.FirstOrDefault(e => e.Monitored) ?? book?.Editions?.FirstOrDefault();
                var cover = edition?.Images?.FirstOrDefault(x => x.CoverType == MediaCoverTypes.Cover);

                if (cover == null)
                {
                    return null;
                }

                var imageFile = _coverMapper.GetCoverPath(edition.BookId, MediaCoverEntity.Book, cover.CoverType, cover.Extension, null);

                if (!File.Exists(imageFile))
                {
                    return null;
                }

                var imageData = File.ReadAllBytes(imageFile);

                if (!CalibreImageValidator.IsValidImage(imageData))
                {
                    return null;
                }

                return Convert.ToBase64String(imageData);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to load canonical cover for {0}", book?.Title);
                return null;
            }
        }

        private static SeriesBookLink PickSeriesLink(Book book)
        {
            var links = book?.SeriesLinks?
                .Where(x => x?.Series?.Value?.Title.IsNotNullOrWhiteSpace() == true)
                .ToList();

            if (links == null || links.Count == 0)
            {
                return null;
            }

            return links
                .OrderBy(x => x.Series.Value.Title.Any(c => c > 127) ? 1 : 0)
                .ThenByDescending(x => x.Series.Value.WorkCount)
                .ThenBy(x => x.SeriesPosition)
                .FirstOrDefault();
        }

        private void PushFormat(int calibreId, string path)
        {
            var payload = new CalibreChangesPayload
            {
                LoadedBookIds = new List<int> { calibreId },
                Changes = new CalibreChanges
                {
                    AddedFormats = new List<CalibreAddFormat>
                    {
                        new CalibreAddFormat
                        {
                            Ext = Path.GetExtension(path),
                            Data = Convert.ToBase64String(File.ReadAllBytes(path))
                        }
                    }
                }
            };

            var request = BuildRequest($"cdb/set-fields/{calibreId}")
                .SetHeader("Content-Type", "application/json")
                .Build();
            request.SetContent(payload.ToJson());
            _httpClient.Post(request);
            _logger.Debug("Pushed format {0} to Calibre content server book {1}", Path.GetExtension(path), calibreId);
        }

        private void DeleteBook(Book book)
        {
            var matches = FindMirrorBookIds(book);

            if (matches.Any())
            {
                DeleteRecords(matches, book.Title);
            }
            else
            {
                _logger.Info("No matching book found on Calibre content server for {0}", book.Title);
            }
        }

        private void DeleteFormat(Book book, string path)
        {
            var extension = (Path.GetExtension(path) ?? string.Empty).TrimStart('.');

            if (extension.IsNullOrWhiteSpace())
            {
                return;
            }

            var records = FindMirrorBooks(book);

            if (!records.Any())
            {
                _logger.Info("No matching book found on Calibre content server for {0}", book.Title);
                return;
            }

            foreach (var record in records)
            {
                var formats = record.Value?.Formats?.Keys.ToList() ?? new List<string>();
                var match = formats.FirstOrDefault(f => f.Equals(extension, StringComparison.OrdinalIgnoreCase));

                if (match == null)
                {
                    _logger.Info("Calibre content server book {0} holds no {1} format, leaving it untouched", record.Key, extension);
                    continue;
                }

                if (formats.Count > 1)
                {
                    RemoveFormat(record.Key, match);
                }
                else
                {
                    DeleteRecords(new List<string> { record.Key }, book.Title);
                }
            }
        }

        private void RemoveFormat(string calibreId, string extension)
        {
            if (!int.TryParse(calibreId, out var id))
            {
                return;
            }

            var payload = new CalibreChangesPayload
            {
                LoadedBookIds = new List<int> { id },
                Changes = new CalibreChanges
                {
                    RemovedFormats = new List<string> { extension }
                }
            };

            var request = BuildRequest($"cdb/set-fields/{id}")
                .SetHeader("Content-Type", "application/json")
                .Build();
            request.SetContent(payload.ToJson());
            _httpClient.Post(request);
            _logger.Info("Removed the {0} format from Calibre content server book {1}", extension, id);
        }

        private void DeleteRecords(List<string> ids, string title)
        {
            _httpClient.Post(BuildRequest($"cdb/delete-books/{string.Join(",", ids)}").Build());
            _logger.Info("Deleted book {0} from Calibre content server (calibre ids: {1})", title, string.Join(",", ids));
        }

        private List<string> FindMirrorBookIds(Book book)
        {
            return FindMirrorBooks(book).Select(x => x.Key).ToList();
        }

        private List<KeyValuePair<string, CalibreBookData>> FindMirrorBooks(Book book)
        {
            var author = book?.Author?.Name;

            if (author.IsNullOrWhiteSpace() || book?.Title == null || book.Title.IsNullOrWhiteSpace())
            {
                return new List<KeyValuePair<string, CalibreBookData>>();
            }

            var query = Uri.EscapeDataString($"authors:\"={author.Replace("\"", "")}\"");
            var searchRequest = BuildRequest($"ajax/search?query={query}").Build();
            var ids = _httpClient.Get<CalibreSearchResult>(searchRequest).Resource.BookIds;

            if (ids?.Any() != true)
            {
                _logger.Debug("No matching book found on Calibre content server for {0}", book.Title);
                return new List<KeyValuePair<string, CalibreBookData>>();
            }

            var titles = TitleForms(book.Title);

            if (titles.Length == 0)
            {
                _logger.Debug("Title '{0}' has no comparable normalized form, refusing to match by title", book.Title);
                return new List<KeyValuePair<string, CalibreBookData>>();
            }

            var booksRequest = BuildRequest($"ajax/books?ids={string.Join(",", ids)}").Build();
            var calibreBooks = _httpClient.Get<Dictionary<string, CalibreBookData>>(booksRequest).Resource;
            var matches = calibreBooks.Where(x => x.Value != null && TitleForms(x.Value.Title).Intersect(titles).Any()).ToList();

            if (!matches.Any())
            {
                matches = calibreBooks.Where(x => x.Value != null && TitlesMatch(titles, RecordTitleForms(x.Value))).ToList();
            }

            return matches;
        }

        private static IEnumerable<string> RecordTitleForms(CalibreBookData data)
        {
            var forms = new List<string>();

            foreach (var title in new[] { data.Title, data.TitleSort })
            {
                if (title.IsNullOrWhiteSpace())
                {
                    continue;
                }

                forms.AddRange(TitleForms(title));

                foreach (var segment in title.Split(new[] { " - " }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var normalized = Normalize(segment);

                    if (normalized.Length > 3 && !normalized.All(char.IsDigit))
                    {
                        forms.Add(normalized);
                    }
                }
            }

            return forms;
        }

        private static string[] TitleForms(string title)
        {
            // A fully non-Latin title normalizes to an empty string; an empty form must
            // never survive here, or every such title matches every other and the
            // matches feed deletions.
            return new[] { Normalize(title), Normalize(Regex.Replace(title ?? string.Empty, @"\s*\([^)]*\)\s*$", "")) }
                .Where(form => form.IsNotNullOrWhiteSpace())
                .Distinct()
                .ToArray();
        }

        private static bool TitlesMatch(IEnumerable<string> bookForms, IEnumerable<string> recordForms)
        {
            // Exact normalized equality only: suffix heuristics match sibling titles
            // ("Foundation" vs "Second Foundation") and this result gates deletions.
            return bookForms
                .Where(form => form.IsNotNullOrWhiteSpace())
                .Intersect(recordForms.Where(form => form.IsNotNullOrWhiteSpace()), StringComparer.Ordinal)
                .Any();
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

            [JsonProperty("title_sort")]
            public string TitleSort { get; set; }

            [JsonProperty("format_metadata")]
            public Dictionary<string, object> Formats { get; set; }
        }
    }
}
