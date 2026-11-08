using System.Collections.Generic;
using System.Linq;
using System.IO;
using NzbDrone.Common.Extensions;
using NzbDrone.Common;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Common.Messaging;

namespace NzbDrone.Core.MediaFiles.Events
{
    // Shared only by deletion observers of one committed replacement batch.
    public sealed class BookFileDeletionContext
    {
        private readonly List<int> _fileIds;
        private readonly Dictionary<System.Type, ILookup<int?, ExtraFile>> _extras = new();
        private readonly IReadOnlyCollection<BookFile> _deletedFiles;
        private readonly Dictionary<System.Type, (ILookup<int?, ExtraFile> Files, ILookup<int?, ExtraFile> Books)> _deletionExtras = new();
        private readonly HashSet<(System.Type Type, int Id)> _removedExtras = new();
        private readonly Dictionary<int, ILookup<string, BookFile>> _replacements;

        public BookFileDeletionContext(IEnumerable<int> fileIds, IEnumerable<BookFile> replacements = null, IReadOnlyCollection<BookFile> deletedFiles = null)
        {
            _fileIds = fileIds.Distinct().ToList();
            _deletedFiles = deletedFiles;
            _replacements = (replacements ?? Enumerable.Empty<BookFile>())
                .Where(file => file.Id > 0 && file.Edition?.BookId > 0 && file.Path.IsNotNullOrWhiteSpace())
                .OrderBy(file => file.Part).ThenBy(file => file.Id)
                .GroupBy(file => file.Edition.BookId)
                .ToDictionary(group => group.Key, group => group.ToLookup(file => Path.GetDirectoryName(file.Path), PathEqualityComparer.Instance));
        }

        public BookFile FindReplacement(int bookId, string sidecarPath)
        {
            return _replacements.TryGetValue(bookId, out var folders)
                ? folders[Path.GetDirectoryName(sidecarPath)].FirstOrDefault()
                : null;
        }

        public bool HasReplacement(int bookId) => _replacements.ContainsKey(bookId);

        public IEnumerable<T> GetExtras<T>(IExtraFileRepository<T> repository, int fileId)
            where T : ExtraFile, new()
        {
            if (!_extras.TryGetValue(typeof(T), out var files))
            {
                files = repository.GetFilesByBookFiles(_fileIds).Cast<ExtraFile>().ToLookup(file => file.BookFileId);
                _extras.Add(typeof(T), files);
            }

            return files[fileId].Cast<T>();
        }

        public IEnumerable<T> GetExtrasForDeletion<T>(IExtraFileRepository<T> repository, BookFile file)
            where T : ExtraFile, new()
        {
            if (!_deletionExtras.TryGetValue(typeof(T), out var extras))
            {
                var rows = repository.GetFilesForDeletion(_deletedFiles ?? new[] { file }).Cast<ExtraFile>().ToList();
                extras = (rows.Where(extra => extra.BookFileId.HasValue).ToLookup(extra => extra.BookFileId),
                    rows.Where(extra => !extra.BookFileId.HasValue).ToLookup(extra => extra.BookId));
                _deletionExtras.Add(typeof(T), extras);
            }

            return extras.Files[file.Id].Concat(extras.Books[file.Edition?.BookId])
                .Where(extra => !_removedExtras.Contains((typeof(T), extra.Id))).Cast<T>();
        }

        public void MarkExtraRemoved<T>(T extra) where T : ExtraFile => _removedExtras.Add((typeof(T), extra.Id));


    }

    public class BookFileDeletedEvent : IEvent
    {
        public BookFile BookFile { get; private set; }
        public DeleteMediaFileReason Reason { get; private set; }
        public IReadOnlyCollection<string> ConfirmedAssociatedPaths { get; }
        public BookFileDeletionContext Context { get; }

        public BookFileDeletedEvent(BookFile bookFile, DeleteMediaFileReason reason, IReadOnlyCollection<string> confirmedAssociatedPaths = null, IReadOnlyCollection<BookFile> replacementFiles = null, BookFileDeletionContext context = null)
        {
            BookFile = bookFile;
            Reason = reason;
            ConfirmedAssociatedPaths = confirmedAssociatedPaths;
            Context = context ?? (replacementFiles == null ? null : new BookFileDeletionContext(new[] { bookFile.Id }, replacementFiles));
        }
    }
}
