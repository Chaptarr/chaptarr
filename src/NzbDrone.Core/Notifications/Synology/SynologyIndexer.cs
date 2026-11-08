using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentValidation.Results;
using NzbDrone.Common;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;

namespace NzbDrone.Core.Notifications.Synology
{
    public class SynologyIndexer : NotificationBase<SynologyIndexerSettings>
    {
        private readonly ISynologyIndexerProxy _indexerProxy;
        private readonly IDiskProvider _diskProvider;
        private readonly ICached<object> _queueLocks;
        private readonly IPendingNotificationRepository _pending;

        public SynologyIndexer(ISynologyIndexerProxy indexerProxy, IDiskProvider diskProvider, ICacheManager cacheManager, IPendingNotificationRepository pending)
        {
            _indexerProxy = indexerProxy;
            _diskProvider = diskProvider;
            _queueLocks = cacheManager.GetCache<object>(GetType(), "queueLocks");
            _pending = pending;
        }

        public override string Link => "https://www.synology.com";
        public override string Name => "Synology Indexer";

        public override void OnReleaseImport(BookDownloadMessage message)
        {
            if (Settings.UpdateLibrary)
            {
                foreach (var oldFile in message.OldFiles)
                {
                    var fullPath = oldFile.Path;

                    _indexerProxy.DeleteFile(fullPath);
                }

                foreach (var newFile in message.BookFiles)
                {
                    var fullPath = newFile.Path;

                    _indexerProxy.AddFile(fullPath);
                }
            }
        }

        public override void OnRename(Author author, List<RenamedBookFile> renamedFiles)
        {
            if (Settings.UpdateLibrary)
            {
                _indexerProxy.UpdateFolder(author.Path);
            }
        }

        public override void OnAuthorDelete(AuthorDeleteMessage deleteMessage)
        {
            if (Settings.UpdateLibrary && deleteMessage.DeletedFiles)
            {
                QueueDelete(deleteMessage.Author.Path, true);
            }
        }

        public override void OnBookDelete(BookDeleteMessage deleteMessage)
        {
            if (Settings.UpdateLibrary && deleteMessage.DeletedFiles)
            {
                lock (_queueLocks.Get(Definition.Id.ToString(), () => new object()))
                {
                    _pending.Enqueue(Definition.Id, deleteMessage.Book.BookFiles.Select(file => Path.GetFullPath(file.Path)));
                }
            }
        }

        public override void OnBookFileDelete(BookFileDeleteMessage deleteMessage)
        {
            if (Settings.UpdateLibrary)
            {
                QueueDelete(deleteMessage.BookFile.Path, false);
            }
        }

        public override void OnBookRetag(BookRetagMessage message)
        {
            if (Settings.UpdateLibrary)
            {
                _indexerProxy.UpdateFolder(message.Author.Path);
            }
        }

        public override bool HasPendingQueue => _pending.HasPending(Definition.Id);

        public override void ProcessQueue()
        {
            lock (_queueLocks.Get(Definition.Id.ToString(), () => new object()))
            {
                // Folder deletions subsume their files even when those rows cross a page boundary.
                var folders = Settings.UpdateLibrary
                    ? _pending.GetPending(Definition.Id, foldersOnly: true).Where(item => !_diskProvider.FolderExists(item.Path)).ToList()
                    : new List<PendingNotificationItem>();
                var deletedFolders = folders.Select(item => item.Path).ToHashSet(PathEqualityComparer.Instance);
                var sentFolders = new HashSet<string>(PathEqualityComparer.Instance);
                foreach (var batch in _pending.GetPending(Definition.Id).Chunk(PendingNotificationRepository.BatchSize))
                {
                    if (!Settings.UpdateLibrary)
                    {
                        _pending.Complete(batch);
                        continue;
                    }
                    foreach (var group in batch.GroupBy(item => GetDeletionTarget(item.Path, deletedFolders), PathEqualityComparer.Instance))
                    {
                        if (deletedFolders.Contains(group.Key))
                        {
                            if (sentFolders.Add(group.Key)) _indexerProxy.DeleteFolder(group.Key);
                        }
                        else if (group.Any(item => !item.IsFolder) && !_diskProvider.FileExists(group.Key)) _indexerProxy.DeleteFile(group.Key);
                        // Keep the folder marker until every child page succeeds, so a retry still coalesces them.
                        _pending.Complete(group.Where(item => !item.IsFolder || !deletedFolders.Contains(item.Path)));
                    }
                }
                _pending.Complete(folders);
            }
        }

        private void QueueDelete(string path, bool isFolder)
        {
            lock (_queueLocks.Get(Definition.Id.ToString(), () => new object()))
            {
                _pending.Enqueue(Definition.Id, Path.GetFullPath(path), isFolder);
            }
        }

        private static string GetDeletionTarget(string path, HashSet<string> deletedFolders)
        {
            var target = path;
            for (var parent = path; parent != null; parent = parent.GetParentPath())
            {
                if (deletedFolders.Contains(parent))
                {
                    target = parent;
                }
            }

            return target;
        }

        public override ValidationResult Test()
        {
            var failures = new List<ValidationFailure>();

            failures.AddIfNotNull(TestConnection());

            return new ValidationResult(failures);
        }

        protected virtual ValidationFailure TestConnection()
        {
            if (!OsInfo.IsLinux)
            {
                return new ValidationFailure(null, "Must be a Synology");
            }

            if (!_indexerProxy.Test())
            {
                return new ValidationFailure(null, "Not a Synology or synoindex not available");
            }

            return null;
        }
    }
}
