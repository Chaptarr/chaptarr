using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentValidation;
using FluentValidation.Results;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
    public enum CalibreTagUpdateResult
    {
        Completed,
        Skipped,
        Incomplete
    }

    public interface ICalibreProxy
    {
        BookFile AddAndConvert(BookFile file, CalibreSettings settings);
        void DeleteBook(BookFile book, CalibreSettings settings);
        void DeleteBooks(List<BookFile> books, CalibreSettings settings);
        void RemoveFormats(int calibreId, IEnumerable<string> formats, CalibreSettings settings);
        CalibreTagUpdateResult SetFields(BookFile file, CalibreSettings settings, bool updateCover = true, bool embed = false, Action<string> onSymlinkSkipped = null);
        List<string> GetAllBookFilePaths(CalibreSettings settings);
        CalibreBook GetBook(int calibreId, CalibreSettings settings);
        List<CalibreBook> GetBooks(List<int> calibreId, CalibreSettings settings);
        void Test(CalibreSettings settings);
    }

    public class CalibreProxy : ICalibreProxy
    {
        public const int PageSize = 750;

        private readonly IHttpClient _httpClient;
        private readonly IMapCoversToLocal _mediaCoverService;
        private readonly IRemotePathMappingService _pathMapper;
        private readonly IRootFolderWatchingService _rootFolderWatchingService;
        private readonly IMediaFileService _mediaFileService;
        private readonly IConfigService _configService;
        private readonly IFileMutationSafetyService _fileMutationSafetyService;
        private readonly ICalibreLibraryGate _libraryGate;
        private readonly Logger _logger;
        private readonly ICached<CalibreBook> _bookCache;

        public CalibreProxy(IHttpClient httpClient,
                            IMapCoversToLocal mediaCoverService,
                            IRemotePathMappingService pathMapper,
                            IRootFolderWatchingService rootFolderWatchingService,
                            IMediaFileService mediaFileService,
                            IConfigService configService,
                            ICacheManager cacheManager,
                            IFileMutationSafetyService fileMutationSafetyService,
                            ICalibreLibraryGate libraryGate,
                            Logger logger)
        {
            _httpClient = httpClient;
            _libraryGate = libraryGate;
            _mediaCoverService = mediaCoverService;
            _pathMapper = pathMapper;
            _rootFolderWatchingService = rootFolderWatchingService;
            _mediaFileService = mediaFileService;
            _configService = configService;
            _fileMutationSafetyService = fileMutationSafetyService;
            _bookCache = cacheManager.GetCache<CalibreBook>(GetType());
            _logger = logger;
        }

        public static string GetOriginalFormat(Dictionary<string, CalibreBookFormat> formats, string trackedExtension = null)
        {
            return formats?
                .Where(x => !string.IsNullOrWhiteSpace(x.Value?.Path) && MediaFileExtensions.TextExtensions.Contains("." + x.Key))
                .Where(x => trackedExtension == null || string.Equals("." + x.Key, trackedExtension, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.Value.LastModified)
                .FirstOrDefault().Value?.Path;
        }

        public BookFile AddAndConvert(BookFile file, CalibreSettings settings)
        {
            _logger.Trace($"Importing to calibre: {file.Path} calibre id: {file.CalibreId}");

            if (file.CalibreId == 0)
            {
                var import = AddBook(file, settings);
                file.CalibreId = import.Id;
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
                throw new CalibreException("Unable to add file to Calibre library: {0}", ex, CalibreLibraryGate.GetFailureReason(ex));
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
            if (string.IsNullOrEmpty(idString)) return;
            var request = GetBuilder($"cdb/delete-books/{idString}/{settings.Library}", settings).Build();
            request.RedactUrlInLogs = true;
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

            ExecuteSetFields(calibreId, payload, settings, allowMissingBook: true);
        }

        public CalibreTagUpdateResult SetFields(BookFile file, CalibreSettings settings, bool updateCover = true, bool embed = false, Action<string> onSymlinkSkipped = null)
        {
            var edition = file.Edition;
            var book = edition.Book;
            var serieslink = BookSeries.Select(book);

            var series = serieslink?.Series.Value;
            double? seriesIndex = null;
            if (double.TryParse(serieslink?.Position, System.Globalization.NumberStyles.AllowDecimalPoint | System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var index))
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
                    Languages = edition.Language.CanonicalizeLanguage(),
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

            try
            {
                // updating the calibre metadata may have renamed the file, so track that
                var updated = GetBook(file.CalibreId, settings);

                // A format can still exist on the server while its mapped local path is unavailable.
                // Embedding may use other visible formats, but a tracked file keeps its format identity.
                var visibleFormats = (updated.Formats ?? new Dictionary<string, CalibreBookFormat>()).Where(format =>
                {
                    if (File.Exists(format.Value.Path))
                    {
                        return true;
                    }
                    _logger.Debug("Calibre format {0} is not visible at {1}; skipping format", format.Key, format.Value.Path);
                    return false;
                }).ToDictionary(format => format.Key, format => format.Value);
                var updatedPath = GetOriginalFormat(visibleFormats, Path.GetExtension(file.Path));
                if (updatedPath.IsNullOrWhiteSpace())
                {
                    _logger.Warn("Tracked Calibre format is not visible for {0}; skipping size and modified refresh", file.Path);
                }
                else
                {
                    _logger.Trace("File path from Calibre: '{0}'", updatedPath);
                    if (!updatedPath.PathEquals(file.Path))
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
                }

                if (embed)
                {
                    var result = EmbedMetadata(file, settings, visibleFormats, onSymlinkSkipped);
                    return updatedPath.IsNullOrWhiteSpace() ? CalibreTagUpdateResult.Incomplete : result;
                }

                return updatedPath.IsNullOrWhiteSpace() ? CalibreTagUpdateResult.Incomplete : CalibreTagUpdateResult.Completed;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new CalibreException("Calibre metadata was saved, but the remaining tag update failed: {0}", ex, CalibreLibraryGate.GetFailureReason(ex)) { WriteAccepted = true };
            }
        }

        private static T SendRequest<T>(Func<T> request)
            where T : HttpResponse
        {
            T response;
            try
            {
                try
                {
                    response = request();
                }
                catch (Exception ex) when (HttpTransportErrors.IsConnectionNotEstablished(ex))
                {
                    // Only this unsent request is repeated; earlier steps for the book are not.
                    response = request();
                }
            }
            catch (JsonException ex)
            {
                throw new CalibreInvalidResponseException("Calibre returned an invalid response", ex);
            }
            if (response == null) throw new CalibreInvalidResponseException("Calibre returned no response");
            if ((int)response.StatusCode is < 200 or >= 300) throw new HttpException(response);
            return response;
        }

        private void ExecuteSetFields(int id, CalibreChangesPayload payload, CalibreSettings settings, bool allowMissingBook = false)
        {
            var builder = GetBuilder($"cdb/set-fields/{id}/{settings.Library}", settings)
                .Post()
                .SetHeader("Content-Type", "application/json");

            var request = builder.Build();
            request.SetContent(payload.ToJson());
            request.ContentSummary = payload.ToJson(Formatting.None);

            var response = SendRequest(() => _httpClient.Execute(request));
            try
            {
                if (string.IsNullOrWhiteSpace(response?.Content))
                {
                    throw new JsonException("The response is empty");
                }
                // Confirm the requested key contains a book object; field completeness is not an acceptance receipt.
                var confirmation = JObject.Parse(response.Content);
                var book = confirmation[id.ToString(CultureInfo.InvariantCulture)];
                if (book?.Type == JTokenType.Null)
                {
                    // Deleting an already-missing book's formats is idempotent. Other
                    // writes still failed for this book, not for the whole library.
                    if (!allowMissingBook) throw new CalibreException("Calibre reports this book no longer exists");
                }
                else if (book is not JObject)
                {
                    throw new JsonException("The response does not confirm the requested book");
                }
            }
            catch (JsonException ex)
            {
                // Calibre writes before serializing its reply. An invalid reply proves
                // neither acceptance nor rollback; do not embed or claim metadata was saved.
                throw new CalibreInvalidResponseException("Calibre did not confirm the change", ex);
            }
        }

        private CalibreTagUpdateResult EmbedMetadata(BookFile file, CalibreSettings settings, Dictionary<string, CalibreBookFormat> formats, Action<string> onSymlinkSkipped)
        {
            var readyFormats = new List<string>();
            var trackedReady = false;
            var trackedSkipped = false;
            foreach (var format in formats)
            {
                try
                {
                    _rootFolderWatchingService.ReportFileSystemChangeBeginning(format.Value.Path);
                    _fileMutationSafetyService.EnsureMutableFile(format.Value.Path);
                    readyFormats.Add(format.Key);
                    trackedReady |= format.Value.Path.PathEquals(file.Path);
                }
                catch (FileSymlinkMutationException ex)
                {
                    _logger.Debug("{0}", ex.Message);
                    onSymlinkSkipped?.Invoke(format.Value.Path);
                    trackedSkipped |= format.Value.Path.PathEquals(file.Path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.Warn(ex, "Cannot prepare Calibre format {0} at {1}; skipping format", format.Key, format.Value.Path);
                }
            }

            if (readyFormats.Count == 0)
            {
                return trackedSkipped ? CalibreTagUpdateResult.Skipped : CalibreTagUpdateResult.Incomplete;
            }

            var request = GetBuilder($"cdb/cmd/embed_metadata", settings)
                .AddQueryParam("library_id", settings.Library)
                .Post()
                .SetHeader("Content-Type", "application/json")
                .Build();

            request.SetContent(new object[] { file.CalibreId, readyFormats }.ToJson());
            JObject response;
            try
            {
                response = JObject.Parse(SendRequest(() => _httpClient.Execute(request)).Content);
            }
            catch (JsonException ex)
            {
                throw new CalibreInvalidResponseException("Calibre did not confirm embedding", ex);
            }
            if (response.TryGetValue("err", out var error))
            {
                _logger.Warn("Calibre could not embed metadata for {0}: {1}", file.Path, error);
                return trackedSkipped ? CalibreTagUpdateResult.Skipped : CalibreTagUpdateResult.Incomplete;
            }

            if (!response.ContainsKey("result"))
            {
                throw new CalibreInvalidResponseException("Calibre did not confirm embedding");
            }

            if (trackedReady)
            {
                if (!PollEmbedStatus(file, settings))
                {
                    _logger.Warn("Calibre returned from embedding {0}, but no file modification was observed", file.Path);
                    return CalibreTagUpdateResult.Incomplete;
                }
            }

            return trackedReady ? CalibreTagUpdateResult.Completed
                : trackedSkipped ? CalibreTagUpdateResult.Skipped : CalibreTagUpdateResult.Incomplete;
        }

        private bool PollEmbedStatus(BookFile file, CalibreSettings settings)
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

            // This is available evidence of a write, not proof that every tag landed.
            // An unchanged timestamp leaves the outcome unconfirmed.
            var changed = current.LastWriteTimeUtc != file.Modified;
            file.Size = current.Length;
            file.Modified = current.LastWriteTimeUtc;

            if (file.Id > 0)
            {
                _mediaFileService.Update(file);
            }

            return changed;
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
                throw new CalibreException("Unable to read Calibre book data: {0}", ex, CalibreLibraryGate.GetFailureReason(ex));
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
                throw new CalibreException("Unable to start Calibre conversion: {0}", ex, CalibreLibraryGate.GetFailureReason(ex));
            }
        }

        public CalibreBook GetBook(int calibreId, CalibreSettings settings)
        {
            try
            {
                var builder = GetBuilder($"ajax/book/{calibreId}/{settings.Library}", settings);

                var request = builder.Build();
                var book = SendRequest(() => _httpClient.Get<CalibreBook>(request)).Resource;

                foreach (var format in book.Formats?.Values ?? Enumerable.Empty<CalibreBookFormat>())
                {
                    if (!string.IsNullOrWhiteSpace(format?.Path))
                    {
                        format.Path = _pathMapper.RemapRemoteToLocal(settings.Host, new OsPath(format.Path)).FullPath;
                    }
                }

                return book;
            }
            catch (HttpException ex)
            {
                throw new CalibreException("Unable to connect to Calibre library: {0}", ex, CalibreLibraryGate.GetFailureReason(ex));
            }
        }

        public List<CalibreBook> GetBooks(List<int> calibreIds, CalibreSettings settings)
        {
            var ids = calibreIds.Distinct().ToList();
            if (ids.Count > PageSize)
            {
                throw new ArgumentException($"Calibre reads accept at most {PageSize} IDs per page", nameof(calibreIds));
            }
            var builder = GetBuilder($"ajax/books/{settings.Library}", settings);
            builder.LogResponseContent = false;
            builder.LogHttpError = false;
            builder.PostProcess = request => request.RedactUrlInLogs = true;
            builder.AddQueryParam("category_urls", false);
            builder.AddQueryParam("ids", ids.ConcatToString(","));
            try
            {
                var response = SendRequest(() => _httpClient.Get<Dictionary<int, CalibreBook>>(builder.Build()));
                if (response.Resource == null || response.Resource.Count == 0)
                {
                    throw new CalibreInvalidResponseException("Calibre returned an empty book response");
                }

                if (ids.Count > 1 && response.Resource.Values.All(book => book == null))
                {
                    _logger.Warn("Calibre {0}: all {1} requested books are deleted or restricted", CalibreLibraryIdentity.GetKey(settings), ids.Count);
                }

                var result = response.Resource.Values.Where(book => book != null).ToList();
                foreach (var book in result)
                {
                    foreach (var format in book.Formats?.Values ?? Enumerable.Empty<CalibreBookFormat>())
                    {
                        if (!string.IsNullOrWhiteSpace(format?.Path))
                        {
                            format.Path = _pathMapper.RemapRemoteToLocal(settings.Host, new OsPath(format.Path)).FullPath;
                        }
                    }
                }

                return result;
            }
            catch (HttpException ex)
            {
                throw new CalibreException("Unable to connect to Calibre library: {0}", ex, CalibreLibraryGate.GetFailureReason(ex, libraryRequest: true));
            }
        }

        public List<string> GetAllBookFilePaths(CalibreSettings settings)
        {
            var timer = Stopwatch.StartNew();
            var pageIndex = 0;
            string Stage() => pageIndex == 0 ? "the ID snapshot" : $"book page {pageIndex}";
            var bookCount = 0;
            var completed = false;
            var result = new List<string>();
            try
            {
                var ids = GetAllBookIds(settings);
                foreach (var page in ids.Chunk(PageSize))
                {
                    pageIndex++;
                    foreach (var book in GetBooks(page.ToList(), settings))
                    {
                        bookCount++;
                        // GetBooks maps each format once; do not map an already-local path again.
                        var localPath = GetOriginalFormat(book.Formats);
                        if (localPath == null) continue;
                        result.Add(localPath);
                        // The scan importer consumes this identity cache until scans resolve Calibre IDs themselves.
                        _bookCache.Set(localPath, book);
                    }
                }
                completed = true;
                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new CalibreException("Unable to read Calibre catalog while reading {0}: {1}", ex,
                    Stage(), CalibreLibraryGate.GetFailureReason(ex, libraryRequest: true));
            }
            finally
            {
                _logger.Debug("Calibre catalog: {0}; {1} book pages, {2} books, {3} paths in {4} ms",
                    completed ? "completed" : $"failed while reading {Stage()}", pageIndex, bookCount, result.Count, timer.ElapsedMilliseconds);
            }
        }

        public List<int> GetAllBookIds(CalibreSettings settings)
        {
            // Read IDs and their total from one Calibre read-lock snapshot. Details remain paged.
            var builder = GetBuilder($"/ajax/category/616c6c626f6f6b73/{settings.Library}", settings);
            builder.AddQueryParam("num", int.MaxValue);
            builder.AddQueryParam("offset", 0);
            builder.LogHttpError = false;
            builder.LogResponseContent = false;
            try
            {
                var page = SendRequest(() => _httpClient.Get<CalibreCategory>(builder.Build())).Resource;
                if (page?.BookIds == null || page.TotalNum < 0)
                {
                    throw new CalibreInvalidResponseException("Calibre returned an invalid book-ID snapshot");
                }
                var seen = new HashSet<int>();
                var ids = page.BookIds.Where(seen.Add).ToList();
                if (ids.Count != page.TotalNum)
                {
                    throw new CalibreInvalidResponseException($"Calibre returned {ids.Count} distinct book IDs but reported {page.TotalNum}");
                }
                return ids;
            }
            catch (HttpException ex)
            {
                throw new CalibreException("Unable to connect to Calibre library: {0}", ex, CalibreLibraryGate.GetFailureReason(ex, libraryRequest: true));
            }
        }

        private CalibreLibraryInfo GetLibraryInfo(CalibreSettings settings)
        {
            var builder = GetBuilder("ajax/library-info", settings);
            builder.LogHttpError = false;
            try
            {
                return _httpClient.Get<CalibreLibraryInfo>(builder.Build()).Resource;
            }
            catch (JsonException ex)
            {
                throw new CalibreInvalidResponseException("Calibre returned an invalid library list", ex);
            }
        }

        private const string CalibreServerManualUrl = "https://manual.calibre-ebook.com/server.html";

        private ValidationFailure TestWriteAccess(CalibreSettings settings)
        {
            var request = GetBuilder("cdb/cmd/saved_searches", settings)
                .AddQueryParam("library_id", settings.Library)
                .Post()
                .SetHeader("Content-Type", "application/json")
                .Build();

            request.SuppressHttpError = true;
            request.SetContent("[\"list\"]");
            var response = _httpClient.Post(request);
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                const string remedy = "Configure a user or trusted IP in calibre. See " + CalibreServerManualUrl;
                var detail = string.Empty;
                // Calibre sends write-access failures as plain text; never display an HTML error page.
                if (response.Headers.ContentType?.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase) == true)
                {
                    using var reader = new StringReader(response.Content ?? string.Empty);
                    var line = reader.ReadLine()?.Trim() ?? string.Empty;
                    detail = line.Truncate(300);
                    if (detail.Length < line.Length) detail += "…";
                }

                return new ValidationFailure("Username", HttpTransportErrors.JoinMessages(
                    string.IsNullOrWhiteSpace(detail) ? "Chaptarr needs write access." : detail, remedy));
            }

            if (response.StatusCode == HttpStatusCode.OK) return null;
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.ProxyAuthenticationRequired ||
                HttpTransportErrors.IsUnavailableStatus(response.StatusCode)) throw new HttpException(response);

            var advice = response.StatusCode == HttpStatusCode.NotFound
                ? " Check the Calibre version and whether the reverse proxy permits /cdb requests."
                : string.Empty;
            throw new CalibreInvalidResponseException("Calibre's write API answered " + HttpTransportErrors.GetStatusMessage(response) + "." + advice);
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
            ValidationFailure failure;
            try
            {
                failure = TestCalibre(settings);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var cause = CalibreLibraryGate.Unwrap(ex);
                var reason = CalibreLibraryGate.GetFailureReason(cause);
                _logger.Debug(ex, "Calibre connection Test failed");
                var status = HttpTransportErrors.GetStatusCode(exception: cause);
                var message = reason;
                if (cause is HttpException http)
                {
                    if (status.HasValue && HttpTransportErrors.IsUnavailableStatus(status.Value))
                    {
                        message = "Calibre is unavailable: " + HttpTransportErrors.GetStatusMessage(http.Response);
                    }
                    else if (!CalibreLibraryGate.HasKnownDiagnosis(cause))
                    {
                        message = HttpTransportErrors.GetStatusMessage(http.Response);
                    }
                }
                else if (!CalibreLibraryGate.HasKnownDiagnosis(cause))
                {
                    message = "Unknown exception: " + reason;
                }

                failure = status == HttpStatusCode.ProxyAuthenticationRequired
                    ? new ValidationFailure("Host", HttpTransportErrors.JoinMessages(reason, "Check the proxy settings."))
                    : CalibreLibraryGate.IsAccessDenied(cause) && status == HttpStatusCode.Unauthorized
                        ? new ValidationFailure("Username", "Authentication failed: check the username and password")
                        : HttpTransportErrors.IsTransientTransportException(cause)
                            ? new NzbDroneValidationFailure("Host", "Unable to connect") { DetailedDescription = HttpTransportErrors.JoinMessages(reason, cause is HttpRequestException { HttpRequestError: HttpRequestError.ProxyTunnelError } ? "Check the proxy settings." : "Verify the hostname and port.") }
                            : new ValidationFailure(string.Empty, message);
                _logger.Warn("Unable to test Calibre: {0}", failure is NzbDroneValidationFailure detailed
                    ? HttpTransportErrors.JoinMessages(detailed.ErrorMessage, detailed.DetailedDescription)
                    : failure.ErrorMessage);
            }
            var failures = new List<ValidationFailure> { failure };
            var validationResult = new ValidationResult(failures);
            var result = new NzbDroneValidationResult(validationResult.Errors);

            if (!result.IsValid || result.HasWarnings)
            {
                throw new ValidationException(result.Failures);
            }
            var key = _libraryGate.GetKey(settings);
            if (key != null)
            {
                _libraryGate.ReportSuccess(key);
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

            response = _httpClient.Execute(request);

            if (response.StatusCode == HttpStatusCode.Unauthorized) throw new HttpException(response);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new ValidationFailure("Host", "Could not connect");
            }

            if (response.StatusCode != HttpStatusCode.OK) throw new HttpException(response);

            if (response.Content.Contains(@"guac-login"))
            {
                return new ValidationFailure("Port", "Bad port. This is the container's remote Calibre GUI, not the Calibre content server.  Try mapping port 8081.");
            }

            if (response.Content.Contains("Calibre-Web"))
            {
                return new ValidationFailure("Port", "This is a Calibre-Web server, not the required Calibre content server.  See " + CalibreServerManualUrl);
            }

            if (!response.Content.Contains(@"<title>calibre</title>"))
            {
                return new ValidationFailure("Port", "Not a valid Calibre content server.  See " + CalibreServerManualUrl);
            }

            var libraryInfo = GetLibraryInfo(settings);

            if (libraryInfo?.LibraryMap == null)
            {
                throw new CalibreInvalidResponseException("Calibre returned an invalid library list");
            }

            if (settings.Library.IsNullOrWhiteSpace())
            {
                settings.Library = libraryInfo.DefaultLibrary;
            }

            if (string.IsNullOrWhiteSpace(settings.Library) || !libraryInfo.LibraryMap.ContainsKey(settings.Library))
            {
                return new ValidationFailure("Library", "Not a valid library in calibre");
            }

            return TestWriteAccess(settings);
        }
    }
}
