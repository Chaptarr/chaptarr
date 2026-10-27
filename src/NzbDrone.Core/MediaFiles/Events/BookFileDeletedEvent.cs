using System.Collections.Generic;
using System.Linq;
using System.IO;
using NzbDrone.Common.Extensions;
using NzbDrone.Common;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Common.Messaging;

namespace NzbDrone.Core.MediaFiles.Events
{
    // Shared only by deletion observers of one committed replacement batch.
    public sealed class BookFileDeletionContext
    {
        private readonly List<int> _fileIds;
        private readonly Dictionary<System.Type, ILookup<int?, ExtraFile>> _extras = new();
        private readonly Dictionary<string, string[]> _folders = new(PathEqualityComparer.Instance);
        private readonly Dictionary<int, ILookup<string, BookFile>> _replacements;

        public BookFileDeletionContext(IEnumerable<int> fileIds, IEnumerable<BookFile> replacements = null)
        {
            _fileIds = fileIds.Distinct().ToList();
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

        public string[] GetFolderContents(IDiskProvider diskProvider, string folder)
        {
            if (!_folders.TryGetValue(folder, out var files))
            {
                files = diskProvider.GetFiles(folder, false).ToArray();
                _folders.Add(folder, files);
            }

            return files;
        }
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
