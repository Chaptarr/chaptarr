using System;
using System.Collections.Generic;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RemotePathMappings;

namespace NzbDrone.Core.Download.Clients.Direct
{
    public partial class DirectDownloadClient
    {
        private DownloadClientItem BuildItem(DirectDownloadClientState state)
        {
            var completed = state.Status == DownloadItemStatus.Completed;
            var failed = state.Status == DownloadItemStatus.Failed;
            var outputPath = completed ? state.OutputFilePath : state.PartFilePath;

            return new DownloadClientItem
            {
                DownloadClientInfo = DownloadClientItemClientInfo.FromDownloadClient(this, false),
                DownloadId = state.DownloadId,
                Title = state.Title,
                TotalSize = state.TotalSize,
                RemainingSize = Math.Max(0, state.TotalSize - state.DownloadedBytes),
                OutputPath = outputPath.IsNotNullOrWhiteSpace() ? new OsPath(outputPath) : new OsPath(null),
                Message = state.Message,
                Status = state.Status,
                CanMoveFiles = false,
                CanBeRemoved = completed
                    ? ((DownloadClientDefinition)Definition).RemoveCompletedDownloads
                    : failed && ((DownloadClientDefinition)Definition).RemoveFailedDownloads,
                FilePaths = completed && _diskProvider.FileExists(state.OutputFilePath)
                    ? new List<string> { state.OutputFilePath }
                    : null,
                FileListConfidence = completed ? DownloadClientFileListConfidence.Authoritative : DownloadClientFileListConfidence.Pending
            };
        }

        private static string BuildFileName(ReleaseInfo release)
        {
            var extension = release.Container?.Trim().TrimStart('.');
            if (extension.IsNullOrWhiteSpace())
            {
                throw new DownloadClientException("Direct release is missing a file extension.");
            }

            return $"{FileNameBuilder.CleanFileName(release.Title)}.{extension}";
        }

        private static void ValidateDownload(RemoteBook remoteBook)
        {
            if (remoteBook?.Release == null)
            {
                throw new DownloadClientException("Direct download requires release metadata.");
            }

            if (remoteBook.Release.DownloadProtocol != DownloadProtocol.Direct)
            {
                throw new DownloadClientException("Direct download client only accepts Direct releases.");
            }

            if (remoteBook.Release.DownloadUrl.IsNullOrWhiteSpace())
            {
                throw new DownloadClientException("Direct release is missing a download URL.");
            }
        }

        /// <summary>
        /// Validates that an identifier is safe for use as a single path segment:
        /// no path separators, no parent-directory references, no null bytes.
        /// </summary>
        internal static bool IsValidPathSegment(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return false;
            }

            foreach (var c in id)
            {
                if (c is '/' or '\\' or '\0')
                {
                    return false;
                }
            }

            if (id == "..")
            {
                return false;
            }

            var trimmed = id.TrimEnd('.');
            if (trimmed.Length == 0)
            {
                return false;
            }

            return true;
        }

        private void DeleteIfPresent(string path)
        {
            if (path.IsNullOrWhiteSpace())
            {
                return;
            }

            if (_diskProvider.FileExists(path))
            {
                _diskProvider.DeleteFile(path);
            }
        }

        private void DeleteDirectoryIfEmpty(string path)
        {
            if (path.IsNullOrWhiteSpace() || !_diskProvider.FolderExists(path) || !_diskProvider.FolderEmpty(path))
            {
                return;
            }

            _diskProvider.DeleteFolder(path, false);
        }

        private sealed class NoopRemotePathMappingService : IRemotePathMappingService
        {
            public List<RemotePathMapping> All() => new();
            public RemotePathMapping Add(RemotePathMapping mapping) => mapping;
            public void Remove(int id) { }
            public RemotePathMapping Get(int id) => null;
            public RemotePathMapping Update(RemotePathMapping mapping) => mapping;
            public OsPath RemapRemoteToLocal(string host, OsPath remotePath) => remotePath;
            public OsPath RemapLocalToRemote(string host, OsPath localPath) => localPath;
            public OsPath RemapRemoteToLocal(int downloadClientId, string host, OsPath remotePath) => remotePath;
            public OsPath RemapLocalToRemote(int downloadClientId, string host, OsPath localPath) => localPath;
            public RemotePathMappingTestResult Test(RemotePathMapping mapping) => new();
        }
    }
}
