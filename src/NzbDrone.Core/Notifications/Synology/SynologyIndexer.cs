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
        private readonly ICached<Dictionary<string, bool>> _pendingDeletes;

        public SynologyIndexer(ISynologyIndexerProxy indexerProxy, IDiskProvider diskProvider, ICacheManager cacheManager)
        {
            _indexerProxy = indexerProxy;
            _diskProvider = diskProvider;
            _pendingDeletes = cacheManager.GetCache<Dictionary<string, bool>>(GetType(), "pendingDeletes");
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
                foreach (var bookFile in deleteMessage.Book.BookFiles)
                {
                    QueueDelete(bookFile.Path, false);
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

        public override bool HasPendingQueue
        {
            get
            {
                lock (_pendingDeletes)
                {
                    return _pendingDeletes.Find(Definition.Id.ToString())?.Count > 0;
                }
            }
        }

        public override void ProcessQueue()
        {
            lock (_pendingDeletes)
            {
                var key = Definition.Id.ToString();
                var pending = _pendingDeletes.Find(key);
                if (Settings.UpdateLibrary && pending != null)
                {
                    var deletedFolders = pending.Where(item => item.Value && !_diskProvider.FolderExists(item.Key))
                        .Select(item => item.Key).ToHashSet(PathEqualityComparer.Instance);
                    var groups = pending.GroupBy(item => GetDeletionTarget(item.Key, deletedFolders), PathEqualityComparer.Instance).ToList();
                    foreach (var group in groups)
                    {
                        if (deletedFolders.Contains(group.Key))
                        {
                            _indexerProxy.DeleteFolder(group.Key);
                        }
                        else if (!pending[group.Key] && !_diskProvider.FileExists(group.Key))
                        {
                            _indexerProxy.DeleteFile(group.Key);
                        }

                        foreach (var item in group)
                        {
                            pending.Remove(item.Key);
                        }
                    }
                }

                _pendingDeletes.Remove(key);
            }
        }

        private void QueueDelete(string path, bool isFolder)
        {
            lock (_pendingDeletes)
            {
                _pendingDeletes.Get(Definition.Id.ToString(), () => new Dictionary<string, bool>(PathEqualityComparer.Instance))[Path.GetFullPath(path)] = isFolder;
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
