using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO.Abstractions;
using System.Linq;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Http;
using NzbDrone.Core.MediaFiles.Azw;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.MediaFiles.TagExtraction;
using NzbDrone.Core.RootFolders;
using PdfSharpCore.Pdf.IO;
using VersOne.Epub;
using VersOne.Epub.Schema;

namespace NzbDrone.Core.MediaFiles
{
    public interface IEBookTagService
    {
        Dictionary<string, List<string>> ReadAllTags(IFileInfo file); // Field-agnostic tag reading (multi-value)
        void WriteTags(BookFile trackfile, bool newDownload, bool force = false);
        void SyncTags(List<Edition> books);
        List<RetagBookFilePreview> GetRetagPreviewsByAuthor(int authorId);
        List<RetagBookFilePreview> GetRetagPreviewsByBook(int bookId);
        TagBatch RetagFiles(RetagFilesCommand message, List<BookFile> files, TagBatch batch);
        TagBatch RetagAuthor(RetagAuthorCommand message, IReadOnlyList<Author> authors, TagBatch batch);
    }

    public class EBookTagService : IEBookTagService
    {
        private readonly IMediaFileService _mediaFileService;
        private readonly IRootFolderService _rootFolderService;
        private readonly IConfigService _configService;
        private readonly ICalibreProxy _calibre;
        private readonly ICalibreLibraryGate _libraryGate;
        private readonly Logger _logger;

        public EBookTagService(IMediaFileService mediaFileService,
            IRootFolderService rootFolderService,
            IConfigService configService,
            ICalibreProxy calibre,
            ICalibreLibraryGate libraryGate,
            Logger logger)
        {
            _mediaFileService = mediaFileService;
            _rootFolderService = rootFolderService;
            _configService = configService;
            _calibre = calibre;
            _libraryGate = libraryGate;
            _logger = logger;
        }

        // Removed legacy ReadTags (ParsedTrackInfo) path; use ReadAllTags for field-agnostic tags

        public Dictionary<string, List<string>> ReadAllTags(IFileInfo file)
        {
            var extension = file.Extension.ToLower();
            _logger.Debug("[METADATA-EXTRACTION] Reading ALL tags from {0} file: {1}", extension, file.FullName);

            switch (extension)
            {
                case ".pdf":
                    return ToMulti(ReadAllPdfTags(file.FullName));
                case ".epub":
                case ".kepub":
                    return ToMulti(ReadAllEpubTags(file.FullName));
                case ".azw3":
                case ".azw":
                case ".mobi":
                    return ToMulti(ReadAllAzw3Tags(file.FullName));
                default:
                    _logger.Debug("[METADATA-EXTRACTION] Unsupported format for field extraction: {0}", extension);
                    return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static Dictionary<string, List<string>> ToMulti(Dictionary<string, string> single)
        {
            var multi = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in single ?? new Dictionary<string, string>())
            {
                multi[kvp.Key] = new List<string> { kvp.Value };
            }
            return multi;
        }

        public void WriteTags(BookFile bookFile, bool newDownload, bool force = false)
        {
            if (!force)
            {
                if (_configService.WriteBookTags == WriteBookTagsType.NewFiles && !newDownload)
                {
                    return;
                }
            }

            _logger.Debug($"Writing tags for {bookFile}");

            WriteAutomaticTags(new List<BookFile> { bookFile });
        }

        public void SyncTags(List<Edition> editions)
        {
            if (_configService.WriteBookTags != WriteBookTagsType.Sync)
            {
                return;
            }

            var hydratedEditions = _mediaFileService.HydrateEditionBookFiles(editions);

            var files = new List<BookFile>();
            foreach (var edition in hydratedEditions)
            {
                foreach (var file in edition.BookFiles ?? new List<BookFile>())
                {
                    file.Edition = edition;
                    files.Add(file);
                }
            }

            WriteAutomaticTags(files);
        }

        private void WriteAutomaticTags(List<BookFile> files)
        {
            files = files.Where(file => file.CalibreId != 0).ToList();
            if (files.Count == 0)
            {
                return;
            }

            var batch = new TagBatch();
            List<RootFolder> roots = null;
            try
            {
                RetagBatch(files, _configService.UpdateCovers, _configService.EmbedMetadata, batch, ref roots);
            }
            finally
            {
                batch.Timer.Stop();
                LogBatch(batch);
            }
        }

        private void LogBatch(TagBatch batch)
        {
            var missingBooks = batch.MissingCalibreBooks.Values.Sum();
            var invalidSettings = batch.InvalidCalibreSettings.Values.Sum();
            if (!batch.HasProblems) return;
            _logger.Warn("Calibre tags: {0} written, {1} failed, {2} incomplete, {3} not attempted (connection or server error, access refusal, rate limit, or unusable Calibre response), " +
                "{4} symlinks skipped, {5} not registered, {6} missing Calibre books, {7} missing roots, {8} roots not linked, " +
                "{9} invalid settings, {10} pages/{11} ids read in {12} ms",
                batch.Written, batch.Failed, batch.Incomplete, batch.Unavailable, batch.SkippedSymlinks,
                batch.NotInCalibre, missingBooks, batch.MissingRootFolder, batch.RootNotLinkedToCalibre, invalidSettings,
                batch.CalibrePages, batch.CalibreIds, batch.Timer.ElapsedMilliseconds);
        }

        public List<RetagBookFilePreview> GetRetagPreviewsByAuthor(int authorId)
        {
            var files = RetagMediaScope.Select(_mediaFileService.GetFilesByAuthor(authorId), BookMediaType.Ebook).ToList();

            return GetPreviews(files).ToList();
        }

        public List<RetagBookFilePreview> GetRetagPreviewsByBook(int bookId)
        {
            var files = RetagMediaScope.Select(_mediaFileService.GetFilesByBook(bookId), BookMediaType.Ebook).ToList();

            return GetPreviews(files).ToList();
        }

        public TagBatch RetagFiles(RetagFilesCommand message, List<BookFile> files, TagBatch batch)
        {
            files = RetagMediaScope.Select(files, BookMediaType.Ebook, batch).ToList();
            List<RootFolder> roots = null;
            RetagBatch(files, message.UpdateCovers, message.EmbedMetadata, batch, ref roots);
            return batch;
        }

        public TagBatch RetagAuthor(RetagAuthorCommand message, IReadOnlyList<Author> authors, TagBatch batch)
        {
            List<RootFolder> roots = null;
            RetagMediaScope.ForEachAuthor(authors, _mediaFileService, BookMediaType.Ebook, batch, files =>
            {
                _logger.ProgressInfo("Re-tagging ebook files for {0}", batch.CurrentAuthor);
                RetagBatch(files, message.UpdateCovers, message.EmbedMetadata, batch, ref roots);
            });

            return batch;
        }

        private void RetagBatch(List<BookFile> files, bool updateCovers, bool embedMetadata, TagBatch batch, ref List<RootFolder> roots)
        {
            if (files.Count == 0)
            {
                return;
            }

            roots ??= _rootFolderService.All();
            ReadCalibrePages(files, roots, batch, (file, reason, library, rootPath, retryAfter) =>
            {
                switch (reason)
                {
                    case RetagSkipReason.NotInCalibre:
                        batch.NotInCalibre++;
                        break;
                    case RetagSkipReason.MissingRootFolder:
                        batch.MissingRootFolder++;
                        break;
                    case RetagSkipReason.RootNotLinkedToCalibre:
                        batch.RootNotLinkedToCalibre++;
                        break;
                    case RetagSkipReason.InvalidCalibreSettings:
                        batch.InvalidCalibreSettings.TryGetValue(rootPath, out var count);
                        batch.InvalidCalibreSettings[rootPath] = count + 1;
                        break;
                    case RetagSkipReason.MissingCalibreBook:
                        batch.MissingCalibreBooks.TryGetValue(library, out var missing);
                        batch.MissingCalibreBooks[library] = missing + 1;
                        break;
                    case RetagSkipReason.LibraryUnavailable:
                        batch.SkipUnavailable(library, retryAfter, _libraryGate.GetFailureContext(library));
                        break;
                    default:
                        batch.Failed++;
                        break;
                }
            }, (page, root, key, _) =>
            {
                foreach (var file in page)
                {
                    if (!_libraryGate.TryBegin(key, out var retryAfter))
                    {
                        batch.SkipUnavailable(key, retryAfter, _libraryGate.GetFailureContext(key));
                        continue;
                    }

                    try
                    {
                        var result = _calibre.SetFields(file, root.CalibreSettings, updateCovers, embedMetadata, path => batch.SkippedSymlinkPaths.Add(path));
                        _libraryGate.ReportSuccess(key);
                        if (result == CalibreTagUpdateResult.Completed)
                        {
                            batch.Written++;
                        }
                        // Skipped is a protected symlink, already counted by SkippedSymlinkPaths.
                        else if (result == CalibreTagUpdateResult.Incomplete)
                        {
                            CountIncomplete(batch, embedMetadata);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        _libraryGate.ReportInconclusive(key);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        var accepted = ex is CalibreException { WriteAccepted: true };
                        _logger.Debug(ex, "Calibre write failed for {0}", file.Path);
                        var outcome = _libraryGate.ReportException(key, ex, file.Path);
                        if (accepted && batch.SkippedSymlinkPaths.Contains(file.Path))
                        {
                            // A sibling's failure does not change this tracked file's protected outcome.
                        }
                        else if (accepted)
                        {
                            CountIncomplete(batch, embedMetadata);
                        }
                        else if (outcome is HttpServiceResult.RateLimited or HttpServiceResult.ConnectionFailure)
                        {
                            _libraryGate.CanBegin(key, out retryAfter);
                            batch.SkipUnavailable(key, retryAfter, file.Path);
                        }
                        else if (outcome == HttpServiceResult.Outage)
                        {
                            batch.Unconfirmed++;
                        }
                        else
                        {
                            batch.Failed++;
                        }

                        if (!HttpServiceHealth.OpensBackoff(outcome))
                        {
                            if (accepted && batch.SkippedSymlinkPaths.Contains(file.Path))
                            {
                                _logger.Warn("Calibre metadata follow-up failed for protected symlink {0}: {1}", file.Path,
                                    CalibreLibraryGate.GetFailureReason(ex));
                            }
                            else
                            {
                                _logger.Error(ex, "Unable to finish writing ebook tags for {0}; continuing batch", file.Path);
                            }
                        }
                    }
                }
            }, writeOnPageFailure: true);
        }

        private static void CountIncomplete(TagBatch batch, bool embedMetadata)
        {
            if (embedMetadata)
            {
                batch.CalibreIncomplete++;
            }
            else
            {
                batch.CalibreDetailsIncomplete++;
            }
        }

        // Preview and Retag share root resolution, library isolation and bounded existence reads.
        // Consume a good page before fetching the next, so a later outage cannot discard its work.
        private void ReadCalibrePages(List<BookFile> files, List<RootFolder> roots, TagBatch batch,
            Action<BookFile, RetagSkipReason, string, string, TimeSpan> skip,
            Action<List<BookFile>, RootFolder, string, Dictionary<int, CalibreBook>> consume, bool writeOnPageFailure = false)
        {
            var groups = new Dictionary<string, (RootFolder Root, List<BookFile> Files)>();
            foreach (var file in files)
            {
                if (file.CalibreId == 0)
                {
                    skip(file, RetagSkipReason.NotInCalibre, null, null, TimeSpan.Zero);
                    continue;
                }

                var root = _rootFolderService.GetBestRootFolder(file.Path, roots);
                if (root == null)
                {
                    skip(file, RetagSkipReason.MissingRootFolder, null, null, TimeSpan.Zero);
                    continue;
                }

                var key = _libraryGate.GetKey(root.CalibreSettings);
                if (key == null)
                {
                    skip(file, root.CalibreSettings == null ? RetagSkipReason.RootNotLinkedToCalibre : RetagSkipReason.InvalidCalibreSettings,
                        null, root.Path, TimeSpan.Zero);
                    continue;
                }

                // Root-folder validation rejects duplicate library keys, so one library
                // has one set of credentials/output settings even though those are not in the key.
                if (!groups.TryGetValue(key, out var group))
                {
                    group = (root, new List<BookFile>());
                    groups.Add(key, group);
                }

                group.Files.Add(file);
            }

            foreach (var (key, group) in groups)
            {
                var pages = group.Files.GroupBy(file => file.CalibreId).Chunk(CalibreProxy.PageSize).ToList();
                for (var pageIndex = 0; pageIndex < pages.Count; pageIndex++)
                {
                    var page = pages[pageIndex];
                    var pageFiles = page.SelectMany(filesForId => filesForId).ToList();
                    if (!_libraryGate.TryBegin(key, out var retryAfter))
                    {
                        foreach (var file in pageFiles)
                        {
                            skip(file, RetagSkipReason.LibraryUnavailable, key, null, retryAfter);
                        }

                        continue;
                    }

                    Dictionary<int, CalibreBook> books;
                    var pageTimer = Stopwatch.StartNew();
                    var pageRead = false;
                    try
                    {
                        books = _calibre.GetBooks(page.Select(filesForId => filesForId.Key).ToList(), group.Root.CalibreSettings)
                            .ToDictionary(book => book.Id);
                        _libraryGate.ReportSuccess(key);
                        pageRead = true;
                        batch.CalibrePages++;
                        batch.CalibreIds += page.Length;
                    }
                    catch (OperationCanceledException)
                    {
                        _libraryGate.ReportInconclusive(key);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        pageTimer.Stop();
                        _logger.Debug(ex, "Calibre page read failed for {0}", key);
                        var outcome = _libraryGate.ReportException(key, ex, libraryRequest: true);
                        var unavailable = HttpServiceHealth.OpensBackoff(outcome);
                        if (unavailable)
                        {
                            _libraryGate.CanBegin(key, out retryAfter);
                        }
                        else
                        {
                            var cause = CalibreLibraryGate.Unwrap(ex);
                            _logger.Warn("Unable to read a page from Calibre {0}: {1}; continuing other work", key,
                                CalibreLibraryGate.GetFailureReason(ex, libraryRequest: true));
                            if (writeOnPageFailure && HttpTransportErrors.GetStatusCode(exception: cause) == System.Net.HttpStatusCode.InternalServerError)
                            {
                                consume(pageFiles, group.Root, key, null);
                                continue;
                            }
                        }

                        foreach (var file in pageFiles)
                        {
                            skip(file, unavailable ? RetagSkipReason.LibraryUnavailable : RetagSkipReason.LibraryPreviewFailed, key, null, retryAfter);
                        }

                        continue;
                    }
                    finally
                    {
                        _logger.Debug("Calibre {0}: {1} {2} ids in {3} ms (page {4}/{5})", key, pageRead ? "read" : "failed",
                            page.Length, pageTimer.ElapsedMilliseconds, pageIndex + 1, pages.Count);
                    }

                    foreach (var file in pageFiles.Where(file => !books.ContainsKey(file.CalibreId)))
                    {
                        skip(file, RetagSkipReason.MissingCalibreBook, key, null, TimeSpan.Zero);
                    }

                    consume(pageFiles.Where(file => books.ContainsKey(file.CalibreId)).ToList(), group.Root, key, books);
                }
            }
        }

        private List<RetagBookFilePreview> GetPreviews(List<BookFile> files)
        {
            var previews = new List<RetagBookFilePreview>();
            if (files.Count == 0)
            {
                return previews;
            }

            var batch = new TagBatch();
            ReadCalibrePages(files, _rootFolderService.All(), batch,
                (file, reason, library, rootPath, retryAfter) => previews.Add(SkippedPreview(file, reason, library,
                    reason == RetagSkipReason.LibraryUnavailable ? DateTime.UtcNow.Add(retryAfter) : null, rootPath)),
                (page, root, key, books) =>
                {
                    foreach (var file in page)
                    {
                        try
                        {
                            var preview = BuildPreview(file, books[file.CalibreId]);
                            if (preview.Changes.Count > 0)
                            {
                                previews.Add(preview);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.Warn(ex, "Unable to build Calibre tag preview for {0}", file.Path);
                            previews.Add(SkippedPreview(file, RetagSkipReason.LibraryPreviewFailed, key));
                        }
                    }
                });
            return previews;
        }

        private static RetagBookFilePreview SkippedPreview(BookFile file, RetagSkipReason reason, string library = null, DateTime? retryAfter = null, string rootPath = null)
        {
            return new RetagBookFilePreview
            {
                AuthorId = file.Author?.Id ?? 0,
                BookId = file.Edition?.BookId ?? 0,
                BookFileId = file.Id,
                Title = file.Edition?.Title,
                Path = file.Path,
                IsCalibreFormat = true,
                SkipReason = reason,
                CalibreLibrary = library,
                RetryAfter = retryAfter,
                RetryNow = retryAfter <= DateTime.UtcNow,
                RootFolderPath = rootPath
            };
        }

        private RetagBookFilePreview BuildPreview(BookFile file, CalibreBook oldTags)
        {
            var edition = file.Edition;
            var book = edition.Book;
            var serieslink = BookSeries.Select(book);

            var series = serieslink?.Series.Value;
            double? seriesIndex = null;
            if (double.TryParse(serieslink?.Position, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var index))
            {
                _logger.Trace($"Parsed {serieslink?.Position} as {index}");
                seriesIndex = index;
            }

            var textInfo = CultureInfo.InvariantCulture.TextInfo;
            var genres = book.Genres.Select(x => textInfo.ToTitleCase(x.Replace('-', ' '))).ToList();

            var newTags = new CalibreBook
            {
                Title = edition.Title,
                Authors = new List<string> { file.Author.Name },
                PubDate = book.ReleaseDate,
                Publisher = edition.Publisher,
                Languages = new List<string> { edition.Language.CanonicalizeLanguage() },
                Tags = genres,
                Comments = edition.Overview,
                Rating = (int)(edition.Ratings.Value * 2) / 2.0,
                Identifiers = new Dictionary<string, string>
                {
                    { "isbn", edition.Isbn13 },
                    { "asin", edition.Asin },
                    { "goodreads", edition.ForeignEditionId }
                },
                Series = series?.Title,
                Position = seriesIndex
            };

            return new RetagBookFilePreview
            {
                AuthorId = file.Author.Id,
                BookId = file.Edition.BookId,
                BookFileId = file.Id,
                Title = edition.Title,
                Path = file.Path,
                IsFileSymlink = FileMutationSafetyService.IsFileSymlink(file.Path),
                IsCalibreFormat = true,
                Changes = oldTags.Diff(newTags)
            };
        }

        // Removed ParsedTrackInfo read helpers (ReadEpub/ReadAzw3/ReadPdf)

        public string GetIsbn(IEnumerable<EpubMetadataIdentifier> ids)
        {
            var candidates = ids.Select(x => StripIsbn(x?.Identifier))
                .Where(x => x != null)
                .OrderByDescending(x => x.Length);

            return candidates.FirstOrDefault(x => x.StartsWith("978"))
                ?? candidates.FirstOrDefault(x => x.StartsWith("979"))
                ?? candidates.FirstOrDefault();
        }

        private string GetIsbnChars(string input)
        {
            if (input == null)
            {
                return null;
            }

            return new string(input.Where(c => char.IsDigit(c) || c == 'X' || c == 'x').ToArray());
        }

        private string StripIsbn(string input)
        {
            var isbn = GetIsbnChars(input);

            if (isbn == null)
            {
                return null;
            }
            else if ((isbn.Length == 10 && ValidateIsbn10(isbn)) ||
                (isbn.Length == 13 && ValidateIsbn13(isbn)))
            {
                return isbn;
            }

            return null;
        }

        private static char Isbn10Checksum(string isbn)
        {
            var sum = 0;
            for (var i = 0; i < 9; i++)
            {
                sum += int.Parse(isbn[i].ToString()) * (10 - i);
            }

            var result = sum % 11;

            if (result == 0)
            {
                return '0';
            }
            else if (result == 1)
            {
                return 'X';
            }

            return (11 - result).ToString()[0];
        }

        private static char Isbn13Checksum(string isbn)
        {
            var result = 0;
            for (var i = 0; i < 12; i++)
            {
                result += int.Parse(isbn[i].ToString()) * ((i % 2 == 0) ? 1 : 3);
            }

            result %= 10;

            return result == 0 ? '0' : (10 - result).ToString()[0];
        }

        private static bool ValidateIsbn10(string isbn)
        {
            return ulong.TryParse(isbn.Substring(0, 9), out _) && isbn[9] == Isbn10Checksum(isbn);
        }

        private static bool ValidateIsbn13(string isbn)
        {
            return ulong.TryParse(isbn, out _) && isbn[12] == Isbn13Checksum(isbn);
        }

        private static bool IsAsinLike(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            // ASINs are typically 10 characters, starting with B (for digital books)
            // or standard 10-character product codes
            return value.Length == 10 && (value.StartsWith("B") || char.IsLetterOrDigit(value[0]));
        }

        // Field-agnostic tag extraction methods
        private Dictionary<string, string> ReadAllEpubTags(string file)
        {
            var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _logger.Debug("[METADATA-EXTRACTION] Extracting ALL fields from EPUB: {0}", file);

            try
            {
                using (var bookRef = EpubReader.OpenBook(file))
                {
                    // Basic fields
                    if (!string.IsNullOrWhiteSpace(bookRef.Title))
                    {
                        tags["title"] = bookRef.Title;
                    }

                    if (bookRef.AuthorList?.Any() == true)
                    {
                        var authors = string.Join("; ", bookRef.AuthorList);
                        tags["author"] = authors;
                        tags["authors"] = authors;
                    }

                    var meta = bookRef.Schema.Package.Metadata;
                    if (meta != null)
                    {
                        // Publishers
                        if (meta.Publishers?.Any() == true)
                        {
                            tags["publisher"] = string.Join("; ", meta.Publishers);
                        }

                        // Languages
                        if (meta.Languages?.Any() == true)
                        {
                            tags["language"] = string.Join("; ", meta.Languages);
                        }

                        // Description
                        if (!string.IsNullOrWhiteSpace(meta.Description))
                        {
                            tags["description"] = meta.Description;
                            tags["comment"] = meta.Description;
                        }

                        // Dates
                        if (meta.Dates?.Any() == true)
                        {
                            foreach (var date in meta.Dates)
                            {
                                if (!string.IsNullOrWhiteSpace(date.Date))
                                {
                                    var key = string.IsNullOrWhiteSpace(date.Event) ? "date" : $"date_{date.Event}";
                                    tags[key] = date.Date;
                                }
                            }
                        }

                        // Identifiers
                        if (meta.Identifiers?.Any() == true)
                        {
                            foreach (var id in meta.Identifiers)
                            {
                                if (!string.IsNullOrWhiteSpace(id.Identifier))
                                {
                                    var scheme = id.Scheme?.ToLower() ?? "unknown";
                                    tags[$"identifier_{scheme}"] = id.Identifier;

                                    // Also add standard fields
                                    if (scheme.Contains("isbn"))
                                    {
                                        tags["isbn"] = id.Identifier;
                                    }
                                    else if (scheme.Contains("asin"))
                                    {
                                        tags["asin"] = id.Identifier;
                                    }
                                }
                            }
                        }

                        // Contributors (may contain additional authors, editors, etc.)
                        if (meta.Contributors?.Any() == true)
                        {
                            foreach (var contributor in meta.Contributors)
                            {
                                if (!string.IsNullOrWhiteSpace(contributor.Contributor))
                                {
                                    var role = contributor.Role?.ToLower() ?? "contributor";
                                    var key = $"contributor_{role}";
                                    if (tags.ContainsKey(key))
                                    {
                                        tags[key] += "; " + contributor.Contributor;
                                    }
                                    else
                                    {
                                        tags[key] = contributor.Contributor;
                                    }
                                }
                            }
                        }

                        // Subjects (genres/categories)
                        if (meta.Subjects?.Any() == true)
                        {
                            tags["subjects"] = string.Join("; ", meta.Subjects);
                            tags["genres"] = tags["subjects"];
                        }

                        // Rights/Copyright
                        if (meta.Rights?.Any() == true)
                        {
                            tags["rights"] = string.Join("; ", meta.Rights);
                            tags["copyright"] = tags["rights"];
                        }

                        // Coverage
                        if (meta.Coverages?.Any() == true)
                        {
                            tags["coverage"] = string.Join("; ", meta.Coverages);
                        }

                        // Source
                        if (meta.Sources?.Any() == true)
                        {
                            tags["source"] = string.Join("; ", meta.Sources);
                        }

                        // Meta items (Calibre and other custom metadata)
                        if (meta.MetaItems?.Any() == true)
                        {
                            foreach (var metaItem in meta.MetaItems)
                            {
                                if (!string.IsNullOrWhiteSpace(metaItem.Name) && !string.IsNullOrWhiteSpace(metaItem.Content))
                                {
                                    tags[metaItem.Name] = metaItem.Content;

                                    // Extract series info
                                    if (metaItem.Name == "calibre:series")
                                    {
                                        tags["series"] = metaItem.Content;
                                    }
                                    else if (metaItem.Name == "calibre:series_index")
                                    {
                                        tags["series_index"] = metaItem.Content;
                                    }
                                }
                            }
                        }

                        // Additional OPF metadata that might contain author info
                        if (meta.Creators?.Any() == true)
                        {
                            foreach (var creator in meta.Creators)
                            {
                                if (!string.IsNullOrWhiteSpace(creator.Creator))
                                {
                                    var role = creator.Role?.ToLower() ?? "creator";
                                    tags[$"creator_{role}"] = creator.Creator;

                                    // Add to general author field if it's an author role
                                    if (role.Contains("aut") || role == "creator")
                                    {
                                        if (tags.ContainsKey("all_authors"))
                                        {
                                            tags["all_authors"] += "; " + creator.Creator;
                                        }
                                        else
                                        {
                                            tags["all_authors"] = creator.Creator;
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // Log all extracted fields
                    _logger.Debug("[METADATA-EXTRACTION] Extracted {0} fields from EPUB", tags.Count);
                    foreach (var tag in tags.OrderBy(t => t.Key))
                    {
                        _logger.Debug("  {0}: {1}", tag.Key, tag.Value.Truncate(100));
                    }

                }
            }
            catch (Exception e)
            {
                _logger.Error(e, "[METADATA-EXTRACTION] Error reading EPUB tags");
                throw new TagExtractionException(file, e);
            }

            return tags;
        }

        private Dictionary<string, string> ReadAllAzw3Tags(string file)
        {
            var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _logger.Debug("[METADATA-EXTRACTION] Extracting ALL fields from AZW3/MOBI: {0}", file);

            try
            {
                var book = new Azw3File(file);

                // Basic fields
                if (!string.IsNullOrWhiteSpace(book.Title))
                {
                    tags["title"] = book.Title;
                }

                if (book.Authors?.Any() == true)
                {
                    var authors = string.Join("; ", book.Authors);
                    tags["author"] = authors;
                    tags["authors"] = authors;
                }

                if (!string.IsNullOrWhiteSpace(book.Isbn))
                {
                    tags["isbn"] = StripIsbn(book.Isbn);
                }

                if (!string.IsNullOrWhiteSpace(book.Asin))
                {
                    tags["asin"] = book.Asin;
                }

                if (!string.IsNullOrWhiteSpace(book.Language))
                {
                    tags["language"] = book.Language;
                }

                if (!string.IsNullOrWhiteSpace(book.Description))
                {
                    tags["description"] = book.Description;
                    tags["comment"] = book.Description;
                }

                if (!string.IsNullOrWhiteSpace(book.Publisher))
                {
                    tags["publisher"] = book.Publisher;
                }

                if (!string.IsNullOrWhiteSpace(book.Imprint))
                {
                    tags["imprint"] = book.Imprint;
                }

                if (!string.IsNullOrWhiteSpace(book.Source))
                {
                    tags["source"] = book.Source;
                }

                // PublishDate is a string property
                if (!string.IsNullOrWhiteSpace(book.PublishDate))
                {
                    tags["publish_date"] = book.PublishDate;
                }

                // Log all extracted fields
                _logger.Debug("[METADATA-EXTRACTION] Extracted {0} fields from AZW3/MOBI", tags.Count);
                foreach (var tag in tags.OrderBy(t => t.Key))
                {
                    _logger.Debug("  {0}: {1}", tag.Key, tag.Value.Truncate(100));
                }

            }
            catch (Exception e)
            {
                _logger.Error(e, "[METADATA-EXTRACTION] Error reading AZW3/MOBI tags");
                throw new TagExtractionException(file, e);
            }

            return tags;
        }

        private Dictionary<string, string> ReadAllPdfTags(string file)
        {
            var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _logger.Debug("[METADATA-EXTRACTION] Extracting ALL fields from PDF: {0}", file);

            try
            {
                using var book = PdfReader.Open(file, PdfDocumentOpenMode.InformationOnly);
                if (book.Info != null)
                {
                    // Basic fields
                    if (!string.IsNullOrWhiteSpace(book.Info.Title))
                    {
                        tags["title"] = book.Info.Title;
                    }

                    if (!string.IsNullOrWhiteSpace(book.Info.Author))
                    {
                        tags["author"] = book.Info.Author;
                        tags["authors"] = book.Info.Author;
                    }

                    if (!string.IsNullOrWhiteSpace(book.Info.Subject))
                    {
                        tags["subject"] = book.Info.Subject;
                        tags["description"] = book.Info.Subject;
                    }

                    if (!string.IsNullOrWhiteSpace(book.Info.Keywords))
                    {
                        tags["keywords"] = book.Info.Keywords;
                        tags["tags"] = book.Info.Keywords;
                    }

                    if (!string.IsNullOrWhiteSpace(book.Info.Creator))
                    {
                        tags["creator"] = book.Info.Creator;
                    }

                    if (!string.IsNullOrWhiteSpace(book.Info.Producer))
                    {
                        tags["producer"] = book.Info.Producer;
                    }

                    if (book.Info.CreationDate != DateTime.MinValue)
                    {
                        tags["creation_date"] = book.Info.CreationDate.ToString("yyyy-MM-dd");
                    }

                    if (book.Info.ModificationDate != DateTime.MinValue)
                    {
                        tags["modification_date"] = book.Info.ModificationDate.ToString("yyyy-MM-dd");
                    }

                    // Log all extracted fields
                    _logger.Debug("[METADATA-EXTRACTION] Extracted {0} fields from PDF", tags.Count);
                    foreach (var tag in tags.OrderBy(t => t.Key))
                    {
                        _logger.Debug("  {0}: {1}", tag.Key, tag.Value.Truncate(100));
                    }

                }
                else
                {
                    _logger.Debug("[METADATA-EXTRACTION] No metadata found in PDF");
                }
            }
            catch (Exception e)
            {
                _logger.Error(e, "[METADATA-EXTRACTION] Error reading PDF tags");
                throw new TagExtractionException(file, e);
            }

            return tags;
        }
    }
}
