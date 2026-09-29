using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using FluentValidation;
using FluentValidation.Results;
using Newtonsoft.Json;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.RemotePathMappings;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Books.Calibre
{
    public interface ICalibreProxy
    {
        BookFile AddAndConvert(BookFile file, CalibreSettings settings);
        void DeleteBook(BookFile book, CalibreSettings settings);
        void DeleteBooks(List<BookFile> books, CalibreSettings settings);
        void RemoveFormats(int calibreId, IEnumerable<string> formats, CalibreSettings settings);
        void SetFields(BookFile file, CalibreSettings settings, bool updateCover = true, bool embed = false);
        void SetIdentity(int calibreId, string title, string author, string series, double? seriesIndex, CalibreSettings settings);
        ICollection<string> SetSelectedFields(BookFile file, ICollection<string> fields, CalibreSettings settings);
        List<string> GetAllBookFilePaths(CalibreSettings settings);
        Dictionary<int, string> GetBookTitlesUnderPath(string localPathPrefix, CalibreSettings settings);
        void DeleteBookIds(List<int> calibreIds, CalibreSettings settings);
        int GetCalibreIdForPath(string path, CalibreSettings settings);
        string GetFormatLocalPath(int calibreId, string extension, CalibreSettings settings);
        CalibreBook GetBook(int calibreId, CalibreSettings settings);
        List<CalibreBook> GetBooks(List<int> calibreId, CalibreSettings settings);
        List<CalibreBook> GetAllBooks(CalibreSettings settings);
        void Test(CalibreSettings settings);
    }

    public class CalibreProxy : ICalibreProxy
    {
        private const int PAGE_SIZE = 750;

        private readonly IHttpClient _httpClient;
        private readonly IMapCoversToLocal _mediaCoverService;
        private readonly IRemotePathMappingService _pathMapper;
        private readonly IRootFolderWatchingService _rootFolderWatchingService;
        private readonly IMediaFileService _mediaFileService;
        private readonly IConfigService _configService;
        private readonly Logger _logger;
        private readonly ICached<CalibreBook> _bookCache;

        public CalibreProxy(IHttpClient httpClient,
                            IMapCoversToLocal mediaCoverService,
                            IRemotePathMappingService pathMapper,
                            IRootFolderWatchingService rootFolderWatchingService,
                            IMediaFileService mediaFileService,
                            IConfigService configService,
                            ICacheManager cacheManager,
                            Logger logger)
        {
            _httpClient = httpClient;
            _mediaCoverService = mediaCoverService;
            _pathMapper = pathMapper;
            _rootFolderWatchingService = rootFolderWatchingService;
            _mediaFileService = mediaFileService;
            _configService = configService;
            _bookCache = cacheManager.GetCache<CalibreBook>(GetType());
            _logger = logger;
        }

        public static string GetOriginalFormat(Dictionary<string, CalibreBookFormat> formats)
        {
            return formats
                .Where(x => MediaFileExtensions.TextExtensions.Contains("." + x.Key))
                .OrderBy(f => f.Value.LastModified)
                .FirstOrDefault().Value?.Path;
        }

        public BookFile AddAndConvert(BookFile file, CalibreSettings settings)
        {
            _logger.Trace($"Importing to calibre: {file.Path} calibre id: {file.CalibreId}");

            if (file.CalibreId == 0)
            {
                // A file already inside the library belongs to an existing record; adding again duplicates it.
                var existingId = GetCalibreIdForPath(file.Path, settings);

                if (existingId > 0)
                {
                    _logger.Debug("{0} already belongs to calibre record {1}; reusing it instead of adding", file.Path, existingId);
                    file.CalibreId = existingId;
                }
                else
                {
                    var import = AddBook(file, settings);
                    file.CalibreId = import.Id;
                }
            }
            else
            {
                AddFormat(file, settings);
            }

            SetFields(file, settings, true, _configService.EmbedMetadata);

            if (settings.OutputFormat.IsNotNullOrWhiteSpace())
            {
                _logger.Trace($"Getting book data for {file.CalibreId}");
                var options = GetBookData(file.CalibreId, settings);
                var inputFormat = file.Quality.Quality.Name.ToUpper();

                options.Conversion_options.Input_fmt = inputFormat;

                var formats = settings.OutputFormat.Split(',').Select(x => x.Trim());
                foreach (var format in formats)
                {
                    if (format.ToLower() == inputFormat ||
                        options.Input_formats.Contains(format, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    options.Conversion_options.Output_fmt = format;

                    if (settings.OutputProfile != (int)CalibreProfile.@default)
                    {
                        options.Conversion_options.Options.Output_profile = ((CalibreProfile)settings.OutputProfile).ToString();
                    }

                    _logger.Trace($"Starting conversion to {format}");

                    _rootFolderWatchingService.ReportFileSystemChangeBeginning(Path.ChangeExtension(file.Path, format));
                    ConvertBook(file.CalibreId, options.Conversion_options, settings);
                }
            }

            return file;
        }

        private CalibreImportJob AddBook(BookFile book, CalibreSettings settings)
        {
            var jobid = (int)(DateTime.UtcNow.Ticks % 1000000000);
            var addDuplicates = 1;
            var path = book.Path;
            var filename = $"$dummy{Path.GetExtension(path)}";
            var body = File.ReadAllBytes(path);

            _logger.Trace($"Read {body.Length} bytes from {path}");

            try
            {
                var builder = GetBuilder($"cdb/add-book/{jobid}/{addDuplicates}/{filename}/{settings.Library}", settings);

                var request = builder.Build();
                request.SetContent(body);

                var response = _httpClient.Post<CalibreImportJob>(request).Resource;

                if (response.Id == 0)
                {
                    throw new CalibreException("Calibre rejected duplicate book");
                }

                return response;
            }
            catch (HttpException ex)
            {
                throw new CalibreException("Unable to add file to Calibre library: {0}", ex, ex.Message);
            }
        }

        public void DeleteBook(BookFile book, CalibreSettings settings)
        {
            var request = GetBuilder($"cdb/delete-books/{book.CalibreId}/{settings.Library}", settings).Build();
            _httpClient.Post(request);
        }

        public void DeleteBooks(List<BookFile> books, CalibreSettings settings)
        {
            var idString = books.Where(x => x.CalibreId != 0).Select(x => x.CalibreId).ConcatToString(",");
            var request = GetBuilder($"cdb/delete-books/{idString}/{settings.Library}", settings).Build();
            _httpClient.Post(request);
        }

        private void AddFormat(BookFile file, CalibreSettings settings)
        {
            var format = Path.GetExtension(file.Path);
            var bookData = Convert.ToBase64String(File.ReadAllBytes(file.Path));

            var payload = new CalibreChangesPayload
            {
                LoadedBookIds = new List<int> { file.CalibreId },
                Changes = new CalibreChanges
                {
                    AddedFormats = new List<CalibreAddFormat>
                    {
                        new CalibreAddFormat
                        {
                            Ext = format,
                            Data = bookData
                        }
                    }
                }
            };

            ExecuteSetFields(file.CalibreId, payload, settings);
        }

        public void RemoveFormats(int calibreId, IEnumerable<string> formats, CalibreSettings settings)
        {
            var payload = new CalibreChangesPayload
            {
                LoadedBookIds = new List<int> { calibreId },
                Changes = new CalibreChanges
                {
                    RemovedFormats = formats.ToList()
                }
            };

            ExecuteSetFields(calibreId, payload, settings);
        }

        // Writing values a record already holds still bumps last_modified, which calibre-web answers by re-embedding metadata into the files.
        private void RemoveUnchanged(int calibreId, Dictionary<string, object> changes, CalibreSettings settings)
        {
            CalibreBook current;

            try
            {
                current = GetBook(calibreId, settings);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to read calibre record {0} back before writing fields", calibreId);
                return;
            }

            if (current == null)
            {
                return;
            }

            bool SameText(object a, string b)
            {
                return string.Equals((a as string)?.Trim() ?? string.Empty, b?.Trim() ?? string.Empty, StringComparison.Ordinal);
            }

            bool SameList(object a, List<string> b)
            {
                var left = ((a as IEnumerable<string>) ?? Enumerable.Empty<string>()).Select(v => v?.Trim()).Where(v => v.IsNotNullOrWhiteSpace()).OrderBy(v => v, StringComparer.Ordinal).ToList();
                var right = (b ?? new List<string>()).Select(v => v?.Trim()).Where(v => v.IsNotNullOrWhiteSpace()).OrderBy(v => v, StringComparer.Ordinal).ToList();

                return left.SequenceEqual(right, StringComparer.Ordinal);
            }

            if (changes.TryGetValue("title", out var title) && SameText(title, current.Title))
            {
                changes.Remove("title");
            }

            if (changes.TryGetValue("authors", out var authors) && SameList(authors, current.Authors))
            {
                changes.Remove("authors");
            }

            if (changes.TryGetValue("comments", out var comments) && SameText(comments, current.Comments))
            {
                changes.Remove("comments");
            }

            if (changes.TryGetValue("publisher", out var publisher) && SameText(publisher, current.Publisher))
            {
                changes.Remove("publisher");
            }

            if (changes.TryGetValue("languages", out var languages) && SameList(languages, current.Languages))
            {
                changes.Remove("languages");
            }

            if (changes.TryGetValue("tags", out var tags) && SameList(tags, current.Tags))
            {
                changes.Remove("tags");
            }

            if (changes.TryGetValue("series", out var series) && SameText(series, current.Series))
            {
                changes.Remove("series");

                if (changes.TryGetValue("series_index", out var index) &&
                    current.Position.HasValue &&
                    index is double position &&
                    Math.Abs(position - current.Position.Value) < 0.001)
                {
                    changes.Remove("series_index");
                }
            }

            if (changes.TryGetValue("pubdate", out var pubdate) &&
                pubdate is DateTime date &&
                current.PubDate.HasValue &&
                date.Date == current.PubDate.Value.Date)
            {
                changes.Remove("pubdate");
            }

            if (changes.TryGetValue("rating", out var rating) &&
                rating is int stars &&
                stars == (int)Math.Round(current.Rating))
            {
                changes.Remove("rating");
            }

            if (changes.TryGetValue("identifiers", out var idsObj) &&
                idsObj is Dictionary<string, string> ids &&
                current.Identifiers != null &&
                ids.Count == current.Identifiers.Count &&
                ids.All(pair => current.Identifiers.TryGetValue(pair.Key, out var existing) && string.Equals(existing, pair.Value, StringComparison.Ordinal)))
            {
                changes.Remove("identifiers");
            }

            if (changes.TryGetValue("cover", out var coverObj) && coverObj is string encoded)
            {
                try
                {
                    var coverRequest = GetBuilder($"get/cover/{calibreId}/{settings.Library}", settings).Build();
                    var existingCover = _httpClient.Get(coverRequest)?.ResponseData;

                    if (existingCover != null && Convert.ToBase64String(existingCover) == encoded)
                    {
                        changes.Remove("cover");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Unable to compare the existing cover for calibre record {0}", calibreId);
                }
            }
        }

        public ICollection<string> SetSelectedFields(BookFile file, ICollection<string> fields, CalibreSettings settings)
        {
            if (file == null || file.CalibreId == 0 || fields == null || fields.Count == 0)
            {
                return Array.Empty<string>();
            }

            var selected = new HashSet<string>(fields, StringComparer.OrdinalIgnoreCase);
            var edition = file.Edition;
            var book = edition?.Book;
            var changes = new Dictionary<string, object>();

            var title = edition?.Title.IsNotNullOrWhiteSpace() == true ? edition.Title : book?.Title;

            if (selected.Contains("title") && title.IsNotNullOrWhiteSpace())
            {
                changes["title"] = title;
            }

            if (selected.Contains("authors") && file.Author?.Name.IsNotNullOrWhiteSpace() == true)
            {
                changes["authors"] = new List<string> { file.Author.Name };
            }

            if (selected.Contains("series"))
            {
                var serieslink = CalibreSeriesSelector.Select(book);
                var seriesTitle = serieslink?.Series?.Value?.Title;

                if (seriesTitle.IsNotNullOrWhiteSpace())
                {
                    changes["series"] = seriesTitle;

                    if (double.TryParse(serieslink.Position, out var index))
                    {
                        changes["series_index"] = index;
                    }
                }
            }

            var overview = edition?.Overview.IsNotNullOrWhiteSpace() == true ? edition.Overview : book?.Overview;

            if (selected.Contains("comments") && overview.IsNotNullOrWhiteSpace())
            {
                changes["comments"] = overview;
            }

            if (selected.Contains("publisher") && edition?.Publisher.IsNotNullOrWhiteSpace() == true)
            {
                changes["publisher"] = edition.Publisher;
            }

            if (selected.Contains("pubdate") && book?.ReleaseDate > DateTime.MinValue)
            {
                changes["pubdate"] = book.ReleaseDate;
            }

            if (selected.Contains("languages") &&
                edition?.Language.CanonicalizeLanguage() is string canonicalLanguage &&
                canonicalLanguage.IsNotNullOrWhiteSpace())
            {
                changes["languages"] = new List<string> { canonicalLanguage };
            }

            if (selected.Contains("tags") && book?.Genres?.Any() == true)
            {
                var textInfo = CultureInfo.InvariantCulture.TextInfo;
                changes["tags"] = book.Genres.Select(x => textInfo.ToTitleCase(x.Replace('-', ' '))).ToList();
            }

            if (selected.Contains("rating") && edition?.Ratings?.Value > 0)
            {
                changes["rating"] = (int)(edition.Ratings.Value * 2);
            }

            if (selected.Contains("identifiers") && edition != null)
            {
                var identifiers = new Dictionary<string, string>();

                if (edition.Isbn13.IsNotNullOrWhiteSpace())
                {
                    identifiers["isbn"] = edition.Isbn13;
                }

                if (edition.Asin.IsNotNullOrWhiteSpace())
                {
                    identifiers["asin"] = edition.Asin;
                }

                if (edition.ForeignEditionId.IsNotNullOrWhiteSpace())
                {
                    identifiers["goodreads"] = edition.ForeignEditionId;
                }

                if (identifiers.Any())
                {
                    changes["identifiers"] = identifiers;
                }
            }

            if (selected.Contains("cover") && edition != null)
            {
                var cover = edition.Images?.FirstOrDefault(x => x.CoverType == MediaCoverTypes.Cover);

                if (cover != null)
                {
                    var imageFile = _mediaCoverService.GetCoverPath(edition.BookId, MediaCoverEntity.Book, cover.CoverType, cover.Extension, null);

                    if (File.Exists(imageFile))
                    {
                        var imageData = File.ReadAllBytes(imageFile);

                        if (CalibreImageValidator.IsValidImage(imageData))
                        {
                            changes["cover"] = Convert.ToBase64String(imageData);
                        }
                    }
                }
            }

            RemoveUnchanged(file.CalibreId, changes, settings);
            DropRejectedFieldWrites(file.CalibreId, changes, settings);

            if (!changes.Any())
            {
                return Array.Empty<string>();
            }

            var payload = new Dictionary<string, object>
            {
                { "changes", changes },
                { "loaded_book_ids", new List<int> { file.CalibreId } }
            };

            var builder = GetBuilder($"cdb/set-fields/{file.CalibreId}/{settings.Library}", settings)
                .Post()
                .SetHeader("Content-Type", "application/json");

            var request = builder.Build();
            request.SetContent(payload.ToJson());
            _httpClient.Execute(request);

            RememberRejectedFieldWrites(file.CalibreId, changes, settings);

            return changes.Keys.ToList();
        }

        private static string RejectedFieldKey(CalibreSettings settings, int calibreId, string field)
        {
            return $"{settings.Host}:{settings.Port}:{settings.Library}:{calibreId}:{field}";
        }

        private static void DropRejectedFieldWrites(int calibreId, Dictionary<string, object> changes, CalibreSettings settings)
        {
            var now = DateTime.UtcNow;

            foreach (var stale in RejectedFieldWrites.Where(p => now - p.Value.Added > RejectedFieldWriteMemory).Select(p => p.Key).ToList())
            {
                RejectedFieldWrites.TryRemove(stale, out _);
            }

            foreach (var field in changes.Keys.ToList())
            {
                if (RejectedFieldWrites.TryGetValue(RejectedFieldKey(settings, calibreId, field), out var rejected) &&
                    rejected.Value == changes[field].ToJson())
                {
                    changes.Remove(field);
                }
            }
        }

        // Fields the server refuses to persist would otherwise be rewritten on every event.
        private void RememberRejectedFieldWrites(int calibreId, Dictionary<string, object> written, CalibreSettings settings)
        {
            var check = written
                .Where(pair => !pair.Key.Equals("cover", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(pair => pair.Key, pair => pair.Value);

            if (!check.Any())
            {
                return;
            }

            RemoveUnchanged(calibreId, check, settings);

            if (!check.Any())
            {
                return;
            }

            foreach (var field in check.Keys)
            {
                RejectedFieldWrites[RejectedFieldKey(settings, calibreId, field)] = (check[field].ToJson(), DateTime.UtcNow);
            }

            _logger.Debug("Calibre did not persist {0} for record {1}; suppressing further writes of the same values", string.Join(", ", check.Keys), calibreId);
        }

        public void SetIdentity(int calibreId, string title, string author, string series, double? seriesIndex, CalibreSettings settings)
        {
            if (calibreId == 0 || (title.IsNullOrWhiteSpace() && author.IsNullOrWhiteSpace() && series.IsNullOrWhiteSpace()))
            {
                return;
            }

            var identity = new Dictionary<string, object>();

            if (title.IsNotNullOrWhiteSpace())
            {
                identity["title"] = title;
            }

            if (author.IsNotNullOrWhiteSpace())
            {
                identity["authors"] = new List<string> { author };
            }

            if (series.IsNotNullOrWhiteSpace())
            {
                identity["series"] = series;

                if (seriesIndex.HasValue)
                {
                    identity["series_index"] = seriesIndex.Value;
                }
            }

            RemoveUnchanged(calibreId, identity, settings);

            if (!identity.Any())
            {
                return;
            }

            var payload = new CalibreChangesPayload
            {
                LoadedBookIds = new List<int> { calibreId },
                Changes = new CalibreChanges
                {
                    Title = identity.ContainsKey("title") ? title : null,
                    Authors = identity.ContainsKey("authors") ? new List<string> { author } : null,
                    Series = identity.ContainsKey("series") ? series : null,
                    SeriesIndex = identity.ContainsKey("series_index") ? seriesIndex : null
                }
            };

            ExecuteSetFields(calibreId, payload, settings);
        }

        public void SetFields(BookFile file, CalibreSettings settings, bool updateCover = true, bool embed = false)
        {
            var edition = file.Edition;
            var book = edition.Book;
            var serieslink = CalibreSeriesSelector.Select(book);

            var series = serieslink?.Series.Value;
            double? seriesIndex = null;
            if (double.TryParse(serieslink?.Position, out var index))
            {
                _logger.Trace("Parsed '{0}' as '{1}'", serieslink.Position, index);
                seriesIndex = index;
            }

            _logger.Trace("Book: {0} Series: {1}, Position: {2}", book, series?.Title, seriesIndex);

            var cover = edition.Images.FirstOrDefault(x => x.CoverType == MediaCoverTypes.Cover);
            string image = null;
            if (cover != null)
            {
                var imageFile = _mediaCoverService.GetCoverPath(edition.BookId, MediaCoverEntity.Book, cover.CoverType, cover.Extension, null);

                if (File.Exists(imageFile))
                {
                    var imageData = File.ReadAllBytes(imageFile);
                    if (CalibreImageValidator.IsValidImage(imageData))
                    {
                        image = Convert.ToBase64String(imageData);
                    }
                }
            }

            var textInfo = CultureInfo.InvariantCulture.TextInfo;
            var genres = book.Genres.Select(x => textInfo.ToTitleCase(x.Replace('-', ' '))).ToList();

            var payload = new CalibreChangesPayload
            {
                LoadedBookIds = new List<int> { file.CalibreId },
                Changes = new CalibreChanges
                {
                    Title = edition.Title,
                    Authors = new List<string> { file.Author.Name },
                    Cover = updateCover ? image : null,
                    PubDate = book.ReleaseDate,
                    Publisher = edition.Publisher,
                    Languages = edition.Language.CanonicalizeLanguage() is string canonicalLanguage
                        ? new List<string> { canonicalLanguage }
                        : null,
                    Tags = genres,
                    Comments = edition.Overview,
                    Rating = (int)(edition.Ratings.Value * 2),
                    Identifiers = new Dictionary<string, string>
                    {
                        { "isbn", edition.Isbn13 },
                        { "asin", edition.Asin },
                        { "goodreads", edition.ForeignEditionId }
                    },
                    Series = series?.Title,
                    SeriesIndex = seriesIndex
                }
            };

            ExecuteSetFields(file.CalibreId, payload, settings);

            // updating the calibre metadata may have renamed the file, so track that
            var updated = GetBook(file.CalibreId, settings);

            var updatedPath = GetOriginalFormat(updated.Formats);

            _logger.Trace("File path from Calibre: '{0}'", updatedPath);

            if (updatedPath.IsNotNullOrWhiteSpace() && updatedPath != file.Path)
            {
                _rootFolderWatchingService.ReportFileSystemChangeBeginning(updatedPath);
                file.Path = updatedPath;
            }

            var fileInfo = new FileInfo(file.Path);
            file.Size = fileInfo.Length;
            file.Modified = fileInfo.LastWriteTimeUtc;

            if (file.Id > 0)
            {
                _mediaFileService.Update(file);
            }

            if (embed)
            {
                EmbedMetadata(file, settings);
            }
        }

        private void ExecuteSetFields(int id, CalibreChangesPayload payload, CalibreSettings settings)
        {
            var builder = GetBuilder($"cdb/set-fields/{id}/{settings.Library}", settings)
                .Post()
                .SetHeader("Content-Type", "application/json");

            var request = builder.Build();
            request.SetContent(payload.ToJson());
            request.ContentSummary = payload.ToJson(Formatting.None);

            _httpClient.Execute(request);
        }

        private void EmbedMetadata(BookFile file, CalibreSettings settings)
        {
            _rootFolderWatchingService.ReportFileSystemChangeBeginning(file.Path);

            var request = GetBuilder($"cdb/cmd/embed_metadata", settings)
                .AddQueryParam("library_id", settings.Library)
                .Post()
                .SetHeader("Content-Type", "application/json")
                .Build();

            request.SetContent($"[{file.CalibreId}, null]");
            _httpClient.Execute(request);

            PollEmbedStatus(file, settings);
        }

        private void PollEmbedStatus(BookFile file, CalibreSettings settings)
        {
            var previous = new FileInfo(file.Path);
            Thread.Sleep(100);

            FileInfo current = null;

            var i = 0;
            while (i++ < 20)
            {
                current = new FileInfo(file.Path);

                if (current.LastWriteTimeUtc == previous.LastWriteTimeUtc &&
                    current.LastWriteTimeUtc != file.Modified)
                {
                    break;
                }

                previous = current;
                Thread.Sleep(1000);
            }

            file.Size = current.Length;
            file.Modified = current.LastWriteTimeUtc;

            if (file.Id > 0)
            {
                _mediaFileService.Update(file);
            }
        }

        private CalibreBookData GetBookData(int calibreId, CalibreSettings settings)
        {
            try
            {
                var request = GetBuilder($"conversion/book-data/{calibreId}", settings)
                    .AddQueryParam("library_id", settings.Library)
                    .Build();

                return _httpClient.Get<CalibreBookData>(request).Resource;
            }
            catch (HttpException ex)
            {
                throw new CalibreException("Unable to add file to Calibre library: {0}", ex, ex.Message);
            }
        }

        private long ConvertBook(int calibreId, CalibreConversionOptions options, CalibreSettings settings)
        {
            try
            {
                var request = GetBuilder($"conversion/start/{calibreId}", settings)
                    .AddQueryParam("library_id", settings.Library)
                    .Build();
                request.SetContent(options.ToJson());

                var jobId = _httpClient.Post<long>(request).Resource;

                // Run async task to check if conversion complete
                _ = PollConvertStatus(jobId, settings);

                return jobId;
            }
            catch (HttpException ex)
            {
                throw new CalibreException("Unable to start Calibre conversion: {0}", ex, ex.Message);
            }
        }

        public CalibreBook GetBook(int calibreId, CalibreSettings settings)
        {
            try
            {
                var builder = GetBuilder($"ajax/book/{calibreId}/{settings.Library}", settings);

                var request = builder.Build();
                var book = _httpClient.Get<CalibreBook>(request).Resource;

                foreach (var format in book.Formats.Values)
                {
                    format.Path = _pathMapper.RemapRemoteToLocal(settings.Host, new OsPath(format.Path)).FullPath;
                }

                return book;
            }
            catch (HttpException ex)
            {
                throw new CalibreException("Unable to connect to Calibre library: {0}", ex, ex.Message);
            }
        }

        public List<CalibreBook> GetAllBooks(CalibreSettings settings)
        {
            var ids = GetAllBookIds(settings);
            var result = new List<CalibreBook>();
            var offset = 0;

            while (offset < ids.Count)
            {
                var chunk = ids.Skip(offset).Take(PAGE_SIZE).ToList();

                if (chunk.Count == 0)
                {
                    break;
                }

                result.AddRange(GetBooks(chunk, settings));
                offset += PAGE_SIZE;
            }

            return result;
        }

        public List<CalibreBook> GetBooks(List<int> calibreIds, CalibreSettings settings)
        {
            var builder = GetBuilder($"ajax/books/{settings.Library}", settings);
            builder.LogResponseContent = false;
            builder.AddQueryParam("ids", calibreIds.ConcatToString(","));

            var request = builder.Build();

            try
            {
                var response = _httpClient.Get<Dictionary<int, CalibreBook>>(request);
                var result = response.Resource.Values.ToList();

                foreach (var book in result)
                {
                    foreach (var format in book.Formats.Values)
                    {
                        format.Path = _pathMapper.RemapRemoteToLocal(settings.Host, new OsPath(format.Path)).FullPath;
                    }
                }

                return result;
            }
            catch (HttpException ex)
            {
                throw new CalibreException("Unable to connect to Calibre library: {0}", ex, ex.Message);
            }
        }

        private static readonly ConcurrentDictionary<string, DateTime> PathEnumerations = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan PathEnumerationCooldown = TimeSpan.FromMinutes(1);
        private static readonly ConcurrentDictionary<string, (string Value, DateTime Added)> RejectedFieldWrites = new ConcurrentDictionary<string, (string Value, DateTime Added)>(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan RejectedFieldWriteMemory = TimeSpan.FromHours(24);

        public int GetCalibreIdForPath(string path, CalibreSettings settings)
        {
            var book = _bookCache.Find(path);

            if (book == null && ShouldEnumeratePaths(settings))
            {
                GetAllBookFilePaths(settings);
                book = _bookCache.Find(path);
            }

            return book?.Id ?? 0;
        }

        // Every download import misses this lookup; one full-library walk per cooldown.
        private static bool ShouldEnumeratePaths(CalibreSettings settings)
        {
            var enumerationKey = $"{settings.Host}:{settings.Port}:{settings.Library}";
            var now = DateTime.UtcNow;
            var last = PathEnumerations.GetOrAdd(enumerationKey, DateTime.MinValue);

            if (now - last < PathEnumerationCooldown)
            {
                return false;
            }

            PathEnumerations[enumerationKey] = now;
            return true;
        }

        public string GetFormatLocalPath(int calibreId, string extension, CalibreSettings settings)
        {
            if (calibreId == 0 || extension.IsNullOrWhiteSpace())
            {
                return null;
            }

            var format = GetBook(calibreId, settings)?.Formats?
                .FirstOrDefault(f => f.Key.Equals(extension, StringComparison.OrdinalIgnoreCase))
                .Value;

            if (format?.Path == null)
            {
                return null;
            }

            return _pathMapper.RemapRemoteToLocal(settings.Host, new OsPath(format.Path)).FullPath;
        }

        public Dictionary<int, string> GetBookTitlesUnderPath(string localPathPrefix, CalibreSettings settings)
        {
            var result = new Dictionary<int, string>();

            if (localPathPrefix.IsNullOrWhiteSpace())
            {
                return result;
            }

            foreach (var book in GetAllBooks(settings))
            {
                if (book?.Formats == null)
                {
                    continue;
                }

                foreach (var format in book.Formats.Values)
                {
                    if (format?.Path == null)
                    {
                        continue;
                    }

                    var localPath = _pathMapper.RemapRemoteToLocal(settings.Host, new OsPath(format.Path)).FullPath;

                    if (localPath.StartsWith(localPathPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        result[book.Id] = book.Title;
                        break;
                    }
                }
            }

            return result;
        }

        public void DeleteBookIds(List<int> calibreIds, CalibreSettings settings)
        {
            if (calibreIds == null || calibreIds.Count == 0)
            {
                return;
            }

            DeleteBooks(calibreIds.Distinct().Select(id => new BookFile { CalibreId = id }).ToList(), settings);
        }

        public List<string> GetAllBookFilePaths(CalibreSettings settings)
        {
            var ids = GetAllBookIds(settings);
            var result = new List<string>();

            var offset = 0;

            while (offset < ids.Count)
            {
                var builder = GetBuilder($"ajax/books/{settings.Library}", settings);
                builder.LogResponseContent = false;
                builder.AddQueryParam("ids", ids.Skip(offset).Take(PAGE_SIZE).ConcatToString(","));

                var request = builder.Build();
                try
                {
                    var response = _httpClient.Get<Dictionary<int, CalibreBook>>(request);
                    foreach (var book in response.Resource.Values)
                    {
                        var remotePath = GetOriginalFormat(book?.Formats);

                        if (remotePath == null)
                        {
                            continue;
                        }

                        var localPath = _pathMapper.RemapRemoteToLocal(settings.Host, new OsPath(remotePath)).FullPath;
                        result.Add(localPath);

                        foreach (var format in book.Formats.Values)
                        {
                            if (format?.Path == null)
                            {
                                continue;
                            }

                            var formatPath = _pathMapper.RemapRemoteToLocal(settings.Host, new OsPath(format.Path)).FullPath;
                            _bookCache.Set(formatPath, book);
                        }
                    }
                }
                catch (HttpException ex)
                {
                    throw new CalibreException("Unable to connect to Calibre library: {0}", ex, ex.Message);
                }

                offset += PAGE_SIZE;
            }

            return result;
        }

        public List<int> GetAllBookIds(CalibreSettings settings)
        {
            // the magic string is 'allbooks' converted to hex
            var builder = GetBuilder($"/ajax/category/616c6c626f6f6b73/{settings.Library}", settings);
            var offset = 0;

            var ids = new List<int>();

            while (true)
            {
                var result = GetPaged<CalibreCategory>(builder, PAGE_SIZE, offset);
                if (!result.Resource.BookIds.Any())
                {
                    break;
                }

                offset += PAGE_SIZE;
                ids.AddRange(result.Resource.BookIds);
            }

            return ids;
        }

        private HttpResponse<T> GetPaged<T>(HttpRequestBuilder builder, int count, int offset)
            where T : new()
        {
            builder.AddQueryParam("num", count, replace: true);
            builder.AddQueryParam("offset", offset, replace: true);

            var request = builder.Build();

            try
            {
                return _httpClient.Get<T>(request);
            }
            catch (HttpException ex)
            {
                throw new CalibreException("Unable to connect to Calibre library: {0}", ex, ex.Message);
            }
        }

        private CalibreLibraryInfo GetLibraryInfo(CalibreSettings settings)
        {
            var builder = GetBuilder($"ajax/library-info", settings);
            var request = builder.Build();
            var response = _httpClient.Get<CalibreLibraryInfo>(request);

            return response.Resource;
        }

        private bool HasWriteAccess(CalibreSettings settings)
        {
            var request = GetBuilder($"cdb/cmd/saved_searches", settings)
                .Post()
                .SetHeader("Content-Type", "application/json")
                .Build();

            request.SuppressHttpError = true;
            request.SetContent("[\"list\"]");

            var response = _httpClient.Get(request);

            return response.StatusCode != HttpStatusCode.Forbidden;
        }

        private HttpRequestBuilder GetBuilder(string relativePath, CalibreSettings settings)
        {
            var baseUrl = HttpRequestBuilder.BuildBaseUrl(settings.UseSsl, settings.Host, settings.Port, settings.UrlBase);
            baseUrl = HttpUri.CombinePath(baseUrl, relativePath);

            var builder = new HttpRequestBuilder(baseUrl)
                .Accept(HttpAccept.Json);

            builder.LogResponseContent = true;

            if (settings.Username.IsNotNullOrWhiteSpace())
            {
                builder.NetworkCredential = new NetworkCredential(settings.Username, settings.Password);
            }

            return builder;
        }

        private async Task PollConvertStatus(long jobId, CalibreSettings settings)
        {
            var request = GetBuilder($"/conversion/status/{jobId}", settings)
                .AddQueryParam("library_id", settings.Library)
                .Build();

            while (true)
            {
                var status = _httpClient.Get<CalibreConversionStatus>(request).Resource;

                if (!status.Running)
                {
                    if (!status.Ok)
                    {
                        _logger.Warn("Calibre conversion failed.\n{0}\n{1}", status.Traceback, status.Log);
                    }

                    return;
                }

                await Task.Delay(2000);
            }
        }

        public void Test(CalibreSettings settings)
        {
            var failures = new List<ValidationFailure> { TestCalibre(settings) };
            var validationResult = new ValidationResult(failures);
            var result = new NzbDroneValidationResult(validationResult.Errors);

            if (!result.IsValid || result.HasWarnings)
            {
                throw new ValidationException(result.Failures);
            }
        }

        private ValidationFailure TestCalibre(CalibreSettings settings)
        {
            var builder = GetBuilder("", settings);
            builder.Accept(HttpAccept.Html);
            builder.SuppressHttpError = true;
            builder.AllowAutoRedirect = true;

            var request = builder.Build();
            request.LogResponseContent = false;
            HttpResponse response;

            try
            {
                response = _httpClient.Execute(request);
            }
            catch (WebException ex)
            {
                _logger.Error(ex, "Unable to connect to Calibre");
                if (ex.Status == WebExceptionStatus.ConnectFailure)
                {
                    return new NzbDroneValidationFailure("Host", "Unable to connect")
                    {
                        DetailedDescription = "Please verify the hostname and port."
                    };
                }

                return new NzbDroneValidationFailure(string.Empty, "Unknown exception: " + ex.Message);
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new ValidationFailure("Host", "Could not connect");
            }

            if (response.Content.Contains(@"guac-login"))
            {
                return new ValidationFailure("Port", "Bad port. This is the container's remote Calibre GUI, not the Calibre content server.  Try mapping port 8081.");
            }

            if (response.Content.Contains("Calibre-Web"))
            {
                return new ValidationFailure("Port", "This is a Calibre-Web server, not the required Calibre content server.  See https://manual.calibre-ebook.com/server.html");
            }

            if (!response.Content.Contains(@"<title>calibre</title>"))
            {
                return new ValidationFailure("Port", "Not a valid Calibre content server.  See https://manual.calibre-ebook.com/server.html");
            }

            if (!HasWriteAccess(settings))
            {
                return new ValidationFailure("Username", "Chaptarr needs write access. Configure a user or trusted IP in calibre. See https://manual.calibre-ebook.com/server.html");
            }

            var libraryInfo = GetLibraryInfo(settings);

            if (settings.Library.IsNullOrWhiteSpace())
            {
                settings.Library = libraryInfo.DefaultLibrary;
            }

            if (!libraryInfo.LibraryMap.ContainsKey(settings.Library))
            {
                return new ValidationFailure("Library", "Not a valid library in calibre");
            }

            return null;
        }
    }
}
