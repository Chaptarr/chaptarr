using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles.Azw;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.MediaFiles.TagExtraction;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
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
        void RetagFiles(RetagFilesCommand message);
        void RetagAuthor(RetagAuthorCommand message);
    }

    public class EBookTagService : IEBookTagService
    {
        private readonly IAuthorService _authorService;
        private readonly IMediaFileService _mediaFileService;
        private readonly IRootFolderService _rootFolderService;
        private readonly IConfigService _configService;
        private readonly ICalibreProxy _calibre;
        private readonly Logger _logger;
        private readonly IFileMutationSafetyService _fileMutationSafetyService;

        public EBookTagService(IAuthorService authorService,
            IMediaFileService mediaFileService,
            IRootFolderService rootFolderService,
            IConfigService configService,
            ICalibreProxy calibre,
            IFileMutationSafetyService fileMutationSafetyService,
            Logger logger)
        {
            _authorService = authorService;
            _mediaFileService = mediaFileService;
            _rootFolderService = rootFolderService;
            _configService = configService;
            _calibre = calibre;
            _logger = logger;
            _fileMutationSafetyService = fileMutationSafetyService;
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

            WriteTagsInternal(bookFile, _configService.UpdateCovers, _configService.EmbedMetadata);
        }

        public void SyncTags(List<Edition> editions)
        {
            if (_configService.WriteBookTags != WriteBookTagsType.Sync)
            {
                return;
            }

            var hydratedEditions = HydrateEditionBookFiles(editions);

            foreach (var edition in hydratedEditions)
            {
                var bookFiles = edition.BookFiles ?? new List<BookFile>();

                _logger.Debug($"Syncing ebook tags for {edition}");

                foreach (var file in bookFiles.Where(x => x.CalibreId != 0))
                {
                    // populate tracks (which should also have release/book/author set) because
                    // not all of the updates will have been committed to the database yet
                    file.Edition = edition;

                    WriteTagsInternal(file, _configService.UpdateCovers, _configService.EmbedMetadata);
                }
            }
        }

        private List<Edition> HydrateEditionBookFiles(List<Edition> editions)
        {
            var safeEditions = editions?.Where(e => e != null).ToList() ?? new List<Edition>();
            var missingBookFiles = safeEditions.Where(e => e.BookFiles == null && e.BookId > 0).ToList();

            if (!missingBookFiles.Any())
            {
                return safeEditions;
            }

            var bookIds = missingBookFiles.Select(e => e.BookId).Distinct().ToList();
            var filesByEditionId = (_mediaFileService.GetFilesByBooks(bookIds) ?? new List<BookFile>())
                .Where(file => file != null && file.EditionId > 0)
                .GroupBy(file => file.EditionId)
                .ToDictionary(group => group.Key, group => group.ToList());

            foreach (var edition in missingBookFiles)
            {
                edition.BookFiles = filesByEditionId.TryGetValue(edition.Id, out var editionFiles)
                    ? editionFiles
                    : new List<BookFile>();
            }

            return safeEditions;
        }

        public List<RetagBookFilePreview> GetRetagPreviewsByAuthor(int authorId)
        {
            var files = _mediaFileService.GetFilesByAuthor(authorId);

            return GetPreviews(files).ToList();
        }

        public List<RetagBookFilePreview> GetRetagPreviewsByBook(int bookId)
        {
            var files = _mediaFileService.GetFilesByBook(bookId);

            return GetPreviews(files).ToList();
        }

        public void RetagFiles(RetagFilesCommand message)
        {
            var author = _authorService.GetAuthor(message.AuthorId);
            var files = _mediaFileService.Get(message.Files);

            _logger.ProgressInfo("Re-tagging {0} ebook files for {1}", files.Count, author.Name);

            foreach (var file in files.Where(x => x.CalibreId != 0))
            {
                WriteTagsInternal(file, message.UpdateCovers, message.EmbedMetadata);
            }

            _logger.ProgressInfo("Selected ebook files re-tagged for {0}", author.Name);
        }

        public void RetagAuthor(RetagAuthorCommand message)
        {
            _logger.Debug("Re-tagging all ebook files for selected authors");
            var authorsToRename = _authorService.GetAuthors(message.AuthorIds);

            foreach (var author in authorsToRename)
            {
                var files = _mediaFileService.GetFilesByAuthor(author.Id);

                _logger.ProgressInfo("Re-tagging all ebook files for author: {0}", author.Name);

                foreach (var file in files.Where(x => x.CalibreId != 0))
                {
                    WriteTagsInternal(file, message.UpdateCovers, message.EmbedMetadata);
                }

                _logger.ProgressInfo("All ebook files re-tagged for {0}", author.Name);
            }
        }

        private void WriteTagsInternal(BookFile file, bool updateCover, bool embedMetadata)
        {
            if (file.CalibreId == 0)
            {
                _logger.Trace($"No calibre id for {file.Path}, skipping writing tags");
            }

            _fileMutationSafetyService.EnsureMutableFile(file.Path);

            var rootFolder = _rootFolderService.GetBestRootFolder(file.Path);

            if (rootFolder == null)
            {
                throw new Exception($"File '{file.Path}' is not in a root folder.");
            }

            _calibre.SetFields(file, rootFolder.CalibreSettings, updateCover, embedMetadata);
        }

        private IEnumerable<RetagBookFilePreview> GetPreviews(List<BookFile> files)
        {
            var calibreFiles = files.Where(x => x.CalibreId > 0).OrderBy(x => x.Edition.Title).ToList();

            var rootFolderPairs = calibreFiles.Select(x => Tuple.Create(x, _rootFolderService.GetBestRootFolder(x.Path)));

            var rootFolderGroups = rootFolderPairs.GroupBy(x => x.Item2.Path);

            var calibreBooks = new List<CalibreBook>();
            foreach (var group in rootFolderGroups)
            {
                var rootFolder = group.First().Item2;
                var books = _calibre.GetBooks(group.Select(x => x.Item1.CalibreId).ToList(), rootFolder.CalibreSettings);
                calibreBooks.AddRange(books);
            }

            var dict = calibreBooks.ToDictionary(x => x.Id);

            foreach (var file in calibreFiles)
            {
                var edition = file.Edition;
                var book = edition.Book;
                var serieslink = book.SeriesLinks.OrderBy(x => x.SeriesPosition).FirstOrDefault(x => x.Series.Value.Title.IsNotNullOrWhiteSpace());

                var series = serieslink?.Series.Value;
                double? seriesIndex = null;
                if (double.TryParse(serieslink?.Position, out var index))
                {
                    _logger.Trace($"Parsed {serieslink?.Position} as {index}");
                    seriesIndex = index;
                }

                var oldTags = dict[file.CalibreId];

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

                var diff = oldTags.Diff(newTags);

                if (diff.Any())
                {
                    yield return new RetagBookFilePreview
                    {
                        AuthorId = file.Author.Id,
                        BookId = file.Edition.Id,
                        BookFileId = file.Id,
                        Path = file.Path,
                        Changes = diff
                    };
                }
            }
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
