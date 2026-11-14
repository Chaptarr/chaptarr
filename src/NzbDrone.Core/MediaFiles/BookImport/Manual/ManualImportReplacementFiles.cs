using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Extras;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.Extras.Others;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles.BookImport.Manual
{
    public sealed class ManualImportReplacementFile
    {
        public int Id { get; set; }
        public int BookId { get; set; }
        public string Path { get; set; }
        public int EditionId { get; set; }
        public string EditionTitle { get; set; }
        public string Narrator { get; set; }
        public string Quality { get; set; }
        public string MediaType { get; set; }
        public string RetentionReason { get; set; }
        public bool IsGraphicAudio { get; set; }
        public string AudioProductionType { get; set; }
        public bool IsPermanentDelete { get; set; }
        public List<string> AssociatedPaths { get; set; } = new();
    }

    public interface IManualImportReplacementFiles
    {
        List<ManualImportReplacementFile> Describe(IReadOnlyCollection<BookFile> files);
        List<ManualImportReplacementFile> DescribeForDeletion(IReadOnlyCollection<BookFile> files);
    }

    public class ManualImportReplacementFiles : IManualImportReplacementFiles
    {
        public const string ConfirmationRequiredMessage = "Replace requires confirmedReplacementFiles. POST the selected files to /api/v1/manualimport/replacementfiles, review the response, and include that list in the ManualImport command. Use an empty list when no files will be replaced.";

        private readonly IDiskProvider _diskProvider;
        private readonly IExtraFileRepository<MetadataFile> _metadata;
        private readonly IExtraFileRepository<OtherExtraFile> _extras;
        private readonly IAuthorRepository _authors;
        private readonly IBookRepository _books;
        private readonly IConfigService _config;

        public ManualImportReplacementFiles(IDiskProvider diskProvider,
            IExtraFileRepository<MetadataFile> metadata, IExtraFileRepository<OtherExtraFile> extras, IAuthorRepository authors, IBookRepository books, IConfigService config)
        {
            _diskProvider = diskProvider;
            _metadata = metadata;
            _extras = extras;
            _authors = authors;
            _books = books;
            _config = config;
        }

        public List<ManualImportReplacementFile> Describe(IReadOnlyCollection<BookFile> files) => Describe(files, false);

        public List<ManualImportReplacementFile> DescribeForDeletion(IReadOnlyCollection<BookFile> files) => Describe(files, true);

        private List<ManualImportReplacementFile> Describe(IReadOnlyCollection<BookFile> files, bool includeBookExtras)
        {
            var ids = files.Select(file => file.Id).ToList();
            var extraRows = includeBookExtras
                ? _metadata.GetFilesForDeletion(files).Cast<ExtraFile>().Concat(_extras.GetFilesForDeletion(files)).ToList()
                : _metadata.GetFilesByBookFiles(ids).Cast<ExtraFile>().Concat(_extras.GetFilesByBookFiles(ids)).ToList();
            var extras = extraRows.Where(extra => extra.BookFileId.HasValue).ToLookup(extra => extra.BookFileId);
            var bookExtras = extraRows.Where(extra => !extra.BookFileId.HasValue).ToLookup(extra => extra.BookId);
            var books = _books.Get(files.Where(file => file.Edition != null && (extras.Contains(file.Id) || bookExtras.Contains(file.Edition.BookId))).Select(file => file.Edition.BookId).Distinct())
                .ToDictionary(book => book.Id);
            var authors = _authors.Get(books.Values.Select(book => book.AuthorId).Distinct())
                .ToDictionary(author => author.Id);
            var primaryPaths = files.Select(file => file.Path).ToHashSet(PathEqualityComparer.Instance);
            var ownedPaths = new Dictionary<ExtraFile, string>();
            var isPermanentDelete = string.IsNullOrWhiteSpace(_config.RecycleBin);
            return files.Select(file =>
            {
                var retentionReason = includeBookExtras ? TrackedFileDeletionCheck.GetRetentionReason(_diskProvider, file.Path) : null;
                var associated = (file.ReplicaPaths ?? new List<string>())
                    .Where(path => path.IsNotNullOrWhiteSpace() && !primaryPaths.Contains(path) && _diskProvider.FileExistsCanonical(path)).ToList();
                foreach (var extra in extras[file.Id].Concat(bookExtras[file.Edition?.BookId]))
                {
                    if (file.Edition == null || !books.TryGetValue(file.Edition.BookId, out var book) ||
                        !authors.TryGetValue(book.AuthorId, out var author))
                    {
                        continue;
                    }

                    if (!ownedPaths.TryGetValue(extra, out var path))
                    {
                        path = ExtraFilePathHelper.ResolveOwnedSidecarPath(author, extra, includeBookExtras ? files : new[] { file }, _diskProvider);
                        ownedPaths.Add(extra, path);
                    }
                    if (path == null)
                    {
                        continue;
                    }

                    associated.Add(path);
                }

                return new ManualImportReplacementFile
                {
                    Id = file.Id,
                    BookId = file.Edition?.BookId ?? 0,
                    Path = file.Path,
                    EditionId = file.EditionId,
                    EditionTitle = file.Edition?.Title,
                    Narrator = file.Narrator ?? file.Edition?.Narrator,
                    Quality = file.Quality?.ToString(),
                    MediaType = file.MediaType,
                    RetentionReason = retentionReason,
                    IsGraphicAudio = AudioProductionDetector.IsDramatized(file),
                    AudioProductionType = AudioProductionDetector.GetProductionLabel(file, file.Edition),
                    IsPermanentDelete = isPermanentDelete,
                    AssociatedPaths = associated.Distinct(PathEqualityComparer.Instance).ToList()
                };
            }).ToList();
        }

        public static IReadOnlyCollection<string> ConfirmedAssociatedPaths(BookFile file, IReadOnlyCollection<ManualImportReplacementFile> confirmedFiles) =>
            confirmedFiles == null ? null : confirmedFiles.Where(confirmed => confirmed.Id == file.Id &&
                confirmed.BookId == file.Edition?.BookId && confirmed.Path.PathEquals(file.Path))
                .SelectMany(confirmed => confirmed.AssociatedPaths ?? new List<string>()).ToHashSet(PathEqualityComparer.Instance);

        public static bool IsConfirmed(BookFile file, IReadOnlyCollection<ManualImportReplacementFile> confirmedFiles) =>
            confirmedFiles == null || confirmedFiles.Any(confirmed => confirmed.Id == file.Id &&
                confirmed.BookId == file.Edition?.BookId && confirmed.Path.PathEquals(file.Path));

        public static List<BookFile> Get(IEnumerable<BookFile> existingFiles, IReadOnlyCollection<LocalBook> selectedFiles, IEnumerable<BookFile> storedSources)
        {
            var sourcePaths = selectedFiles.Select(file => file.Path).ToHashSet(PathEqualityComparer.Instance);
            var storedPaths = storedSources.Select(file => file.Path).ToHashSet(PathEqualityComparer.Instance);
            var replacementBooks = selectedFiles
                .Where(file => !storedPaths.Contains(file.Path) && (!file.ExistingFile || ConversionWorkPath.HasSuccessfulArtifact(file.Path)))
                .Select(file => file.Book?.Id ?? 0).ToHashSet();

            // Relinking library files does not replace a recording, and selected inputs must never be deleted.
            return existingFiles.Where(file => replacementBooks.Contains(file.Edition?.BookId ?? 0) &&
                !sourcePaths.Contains(file.Path)).ToList();
        }
    }
}
