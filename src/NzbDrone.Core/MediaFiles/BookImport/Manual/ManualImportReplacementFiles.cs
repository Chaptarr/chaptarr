using System.Collections.Generic;
using System.IO;
using System.Linq;
using NzbDrone.Common;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
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
        public List<string> AssociatedPaths { get; set; } = new();
    }

    public interface IManualImportReplacementFiles
    {
        List<ManualImportReplacementFile> Describe(IReadOnlyCollection<BookFile> files);
    }

    public class ManualImportReplacementFiles : IManualImportReplacementFiles
    {
        private readonly IDiskProvider _diskProvider;
        private readonly IExtraFileRepository<MetadataFile> _metadata;
        private readonly IExtraFileRepository<OtherExtraFile> _extras;
        private readonly IAuthorRepository _authors;
        private readonly IBookRepository _books;

        public ManualImportReplacementFiles(IDiskProvider diskProvider,
            IExtraFileRepository<MetadataFile> metadata, IExtraFileRepository<OtherExtraFile> extras, IAuthorRepository authors, IBookRepository books)
        {
            _diskProvider = diskProvider;
            _metadata = metadata;
            _extras = extras;
            _authors = authors;
            _books = books;
        }

        public List<ManualImportReplacementFile> Describe(IReadOnlyCollection<BookFile> files)
        {
            var ids = files.Select(file => file.Id).ToList();
            var extras = _metadata.GetFilesByBookFiles(ids).Cast<ExtraFile>()
                .Concat(_extras.GetFilesByBookFiles(ids)).ToLookup(extra => extra.BookFileId);
            var books = _books.Get(files.Where(file => extras.Contains(file.Id)).Select(file => file.Edition.BookId).Distinct())
                .ToDictionary(book => book.Id);
            var authors = _authors.Get(books.Values.Select(book => book.AuthorId).Distinct())
                .ToDictionary(author => author.Id);
            var selectedPaths = files.Select(file => file.Path).ToHashSet(PathEqualityComparer.Instance);
            var folders = new Dictionary<string, string[]>(PathEqualityComparer.Instance);
            return files.Select(file =>
            {
                var associated = (file.ReplicaPaths ?? new List<string>())
                    .Where(path => path.IsNotNullOrWhiteSpace() && !selectedPaths.Contains(path) && _diskProvider.FileExistsCanonical(path)).ToList();
                foreach (var extra in extras[file.Id])
                {
                    var path = ExtraFilePathHelper.ResolveOwnedSidecarPath(authors[books[file.Edition.BookId].AuthorId], extra, new[] { file }, _diskProvider);
                    if (path == null)
                    {
                        continue;
                    }

                    var folder = Path.GetDirectoryName(path);
                    if (!folders.TryGetValue(folder, out var contents))
                    {
                        contents = _diskProvider.GetFiles(folder, false).ToArray();
                        folders.Add(folder, contents);
                    }

                    if (!ExtraFilePathHelper.HasRemainingMedia(selectedPaths, contents))
                    {
                        associated.Add(path);
                    }
                }

                return new ManualImportReplacementFile
                {
                    Id = file.Id,
                    BookId = file.Edition.BookId,
                    Path = file.Path,
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
