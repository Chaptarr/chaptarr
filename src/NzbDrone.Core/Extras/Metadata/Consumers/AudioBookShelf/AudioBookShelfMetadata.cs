using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.RootFolders;
using MediaCoverModel = NzbDrone.Core.MediaCover.MediaCover;

namespace NzbDrone.Core.Extras.Metadata.Consumers.AudioBookShelf
{
    public class AudioBookShelfMetadata : MetadataBase<AudioBookShelfMetadataSettings>
    {
        private static readonly HashSet<string> SupportedCoverExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg",
            ".jpeg",
            ".png",
            ".webp"
        };

        private readonly IRootFolderService _rootFolderService;
        private readonly IBookService _bookService;
        private readonly IEditionService _editionService;

        public AudioBookShelfMetadata(IRootFolderService rootFolderService,
                                      IBookService bookService,
                                      IEditionService editionService)
        {
            _rootFolderService = rootFolderService;
            _bookService = bookService;
            _editionService = editionService;
        }

        public override string Name => "AudioBookShelf Sidecars";

        public override MetadataFile FindMetadataFile(Author author, string path)
        {
            var fileName = Path.GetFileName(path);

            if (fileName.Equals("metadata.json", StringComparison.OrdinalIgnoreCase))
            {
                return new MetadataFile
                {
                    AuthorId = author.Id,
                    Consumer = GetType().Name,
                    Type = MetadataType.BookMetadata
                };
            }

            if (Path.GetFileNameWithoutExtension(path).Equals("cover", StringComparison.OrdinalIgnoreCase) &&
                SupportedCoverExtensions.Contains(Path.GetExtension(path)))
            {
                return new MetadataFile
                {
                    AuthorId = author.Id,
                    Consumer = GetType().Name,
                    Type = MetadataType.BookImage
                };
            }

            return null;
        }

        public override MetadataFileResult AuthorMetadata(Author author)
        {
            return null;
        }

        public override MetadataFileResult BookMetadata(Author author, BookFile bookFile)
        {
            if (!ShouldWriteMetadataForBookFile(author, bookFile))
            {
                return null;
            }

            var (edition, book) = ResolveBook(author, bookFile);
            if (edition == null || book == null)
            {
                return null;
            }

            var relativePath = GetSidecarRelativePath(author, bookFile, "metadata.json");
            if (relativePath.IsNullOrWhiteSpace())
            {
                return null;
            }

            var metadata = BuildMetadata(author, book, edition, bookFile);
            var contents = JsonConvert.SerializeObject(metadata, Formatting.Indented, new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore
            });

            return new MetadataFileResult(relativePath, contents, overwriteExisting: true);
        }

        public override List<ImageFileResult> AuthorImages(Author author)
        {
            return new List<ImageFileResult>();
        }

        public override List<ImageFileResult> BookImages(Author author, BookFile bookFile)
        {
            if (!ShouldWriteCoverForBookFile(author, bookFile))
            {
                return new List<ImageFileResult>();
            }

            var (edition, book) = ResolveBook(author, bookFile);
            if (edition == null || book == null)
            {
                return new List<ImageFileResult>();
            }

            var cover = SelectCover(edition);
            if (cover?.Url.IsNullOrWhiteSpace() != false)
            {
                return new List<ImageFileResult>();
            }

            var extension = NormalizeCoverExtension(cover.Extension, cover.Url);
            var relativePath = GetSidecarRelativePath(author, bookFile, "cover" + extension);
            if (relativePath.IsNullOrWhiteSpace())
            {
                return new List<ImageFileResult>();
            }

            var source = GetCoverSource(cover);
            if (source.IsNullOrWhiteSpace())
            {
                return new List<ImageFileResult>();
            }

            return new List<ImageFileResult>
            {
                new ImageFileResult(relativePath, source, overwriteExisting: true)
            };
        }

        public override string GetFilenameAfterMove(Author author, BookFile bookFile, MetadataFile metadataFile)
        {
            if (metadataFile.Type == MetadataType.BookMetadata)
            {
                return Path.Combine(Path.GetDirectoryName(bookFile.Path), "metadata.json");
            }

            if (metadataFile.Type == MetadataType.BookImage)
            {
                var extension = NormalizeCoverExtension(metadataFile.Extension, metadataFile.RelativePath);
                return Path.Combine(Path.GetDirectoryName(bookFile.Path), "cover" + extension);
            }

            return base.GetFilenameAfterMove(author, bookFile, metadataFile);
        }

        private bool ShouldWriteMetadataForBookFile(Author author, BookFile bookFile)
        {
            return ShouldWriteForBookFile(author, bookFile, settings => settings.WriteAudioBookShelfMetadataJson);
        }

        private bool ShouldWriteCoverForBookFile(Author author, BookFile bookFile)
        {
            return ShouldWriteForBookFile(author, bookFile, settings => settings.WriteAudioBookShelfCover);
        }

        private bool ShouldWriteForBookFile(Author author, BookFile bookFile, Func<MediaTypeSettings, bool> isEnabled)
        {
            if (author == null || bookFile == null || bookFile.Path.IsNullOrWhiteSpace())
            {
                return false;
            }

            if (bookFile.Part > 1 || (bookFile.PartCount > 1 && bookFile.Part != 1))
            {
                return false;
            }

            var rootFolder = _rootFolderService.GetBestRootFolder(bookFile.Path);
            if (rootFolder == null)
            {
                return false;
            }

            var settings = IsEbook(bookFile)
                ? rootFolder.GetEbookSettings()
                : rootFolder.GetAudiobookSettings();

            return settings != null && isEnabled(settings);
        }

        private (Edition Edition, Book Book) ResolveBook(Author author, BookFile bookFile)
        {
            var edition = bookFile.Edition;
            if (edition == null && bookFile.EditionId > 0)
            {
                edition = _editionService.GetEdition(bookFile.EditionId);
                bookFile.Edition = edition;
            }

            var book = edition?.Book;
            if (book == null && edition?.BookId > 0)
            {
                book = _bookService.GetBook(edition.BookId);
                edition.Book = book;
            }

            if (book != null && book.Author == null && author != null)
            {
                book.Author = author;
            }

            return (edition, book);
        }

        private Dictionary<string, object> BuildMetadata(Author author, Book book, Edition edition, BookFile bookFile)
        {
            var metadata = new Dictionary<string, object>();
            var releaseDate = edition.ReleaseDate ?? book.ReleaseDate;

            AddString(metadata, "title", FirstNotEmpty(edition.Title, book.Title));
            AddString(metadata, "subtitle", FirstNotEmpty(edition.Subtitle, book.Subtitle));
            AddStringArray(metadata, "authors", new[] { author?.Name, book.Author?.Name });
            AddStringArray(metadata, "narrators", GetNarrators(book, edition, bookFile));
            AddStringArray(metadata, "series", GetSeries(book));
            AddStringArray(metadata, "genres", book.Genres);
            AddString(metadata, "publishedYear", releaseDate?.Year.ToString() ?? book.PublicationYear?.ToString());
            AddString(metadata, "publishedDate", releaseDate?.ToString("yyyy-MM-dd"));
            AddString(metadata, "publisher", FirstNotEmpty(edition.Publisher, book.Publisher));
            AddString(metadata, "description", FirstNotEmpty(edition.Overview, book.Overview));
            AddString(metadata, "isbn", FirstNotEmpty(edition.Isbn13, edition.Isbn10, book.ISBN13, book.ISBN10));
            AddString(metadata, "asin", FirstNotEmpty(edition.AudibleASIN, edition.Asin, book.AudibleASIN, book.ASIN));
            AddString(metadata, "language", FirstNotEmpty(edition.Language, book.LanguageCode, book.LanguageName));

            var chapters = GetChapters(edition);
            if (chapters.Count > 0 && !IsEbook(bookFile))
            {
                metadata["chapters"] = chapters;
            }

            return metadata;
        }

        private static List<object> GetChapters(Edition edition)
        {
            var chapters = new List<object>();
            if (edition?.Chapters == null)
            {
                return chapters;
            }

            foreach (var chapter in edition.Chapters)
            {
                var start = chapter.StartOffsetSec > 0 ? chapter.StartOffsetSec : chapter.StartOffsetMs / 1000.0;
                var length = chapter.LengthMs / 1000.0;
                if (length <= 0)
                {
                    continue;
                }

                chapters.Add(new
                {
                    start,
                    end = start + length,
                    title = chapter.Title.IsNotNullOrWhiteSpace() ? chapter.Title : $"Chapter {chapters.Count + 1}"
                });
            }

            return chapters;
        }

        private static List<string> GetNarrators(Book book, Edition edition, BookFile bookFile)
        {
            return SplitNames(bookFile.Narrator)
                .Concat(SplitNames(edition.Narrator))
                .Concat(edition.NarratorNames ?? new List<string>())
                .Concat(SplitNames(book.Narrator))
                .Distinct(StringComparer.InvariantCultureIgnoreCase)
                .ToList();
        }

        private static List<string> GetSeries(Book book)
        {
            if (book.SeriesName.IsNullOrWhiteSpace())
            {
                return new List<string>();
            }

            var position = book.SeriesPosition.IsNotNullOrWhiteSpace() ? $" #{book.SeriesPosition}" : string.Empty;
            return new List<string> { book.SeriesName + position };
        }

        private static MediaCoverModel SelectCover(Edition edition)
        {
            return edition.Images?.FirstOrDefault(c => c.CoverType == MediaCoverTypes.Cover);
        }

        private static string GetCoverSource(MediaCoverModel cover)
        {
            return cover.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? cover.Url : null;
        }

        private string GetSidecarRelativePath(Author author, BookFile bookFile, string fileName)
        {
            var bookFolder = Path.GetDirectoryName(bookFile.Path);
            if (bookFolder.IsNullOrWhiteSpace())
            {
                return null;
            }

            var fullPath = Path.Combine(bookFolder, fileName);
            var preferredBase = ExtraFilePathHelper.GetPreferredBasePath(author, bookFile);

            return ExtraFilePathHelper.TryGetRelativePath(author, fullPath, out var relativePath, out _, preferredBase)
                ? relativePath
                : null;
        }

        private static bool IsEbook(BookFile bookFile)
        {
            if (bookFile.MediaType.IsNotNullOrWhiteSpace())
            {
                return bookFile.MediaType.Equals("ebook", StringComparison.OrdinalIgnoreCase);
            }

            return bookFile.Quality != null && BookFile.DetermineMediaType(bookFile.Quality).Equals("ebook", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeCoverExtension(string extension, string source)
        {
            var normalized = extension;

            if (normalized.IsNullOrWhiteSpace())
            {
                normalized = Path.GetExtension(source ?? string.Empty);
            }

            if (normalized.IsNullOrWhiteSpace())
            {
                return ".jpg";
            }

            if (!normalized.StartsWith("."))
            {
                normalized = "." + normalized;
            }

            return SupportedCoverExtensions.Contains(normalized) ? normalized.ToLowerInvariant() : ".jpg";
        }

        private static void AddString(Dictionary<string, object> metadata, string key, string value)
        {
            if (value.IsNotNullOrWhiteSpace())
            {
                metadata[key] = value;
            }
        }

        private static void AddStringArray(Dictionary<string, object> metadata, string key, IEnumerable<string> values)
        {
            var cleaned = values?
                .Where(v => v.IsNotNullOrWhiteSpace())
                .Select(v => v.Trim())
                .Distinct(StringComparer.InvariantCultureIgnoreCase)
                .ToList();

            if (cleaned?.Count > 0)
            {
                metadata[key] = cleaned;
            }
        }

        private static List<string> SplitNames(string names)
        {
            if (names.IsNullOrWhiteSpace())
            {
                return new List<string>();
            }

            return names
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(n => n.Trim())
                .Where(n => n.IsNotNullOrWhiteSpace())
                .ToList();
        }

        private static string FirstNotEmpty(params string[] values)
        {
            return values.FirstOrDefault(v => v.IsNotNullOrWhiteSpace());
        }
    }
}
