using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Datastore.Events;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.RemotePathMappings;

namespace NzbDrone.Core.Download
{
    public interface IDownloadClientFileSnapshotService
    {
        void CaptureClientList(DownloadClientItem item);
        void CaptureCompletedOutput(DownloadClientItem item);
        void ApplySnapshot(DownloadClientItem item);
        void Delete(DownloadClientItem item);
    }

    public class DownloadClientFileSnapshotService : IDownloadClientFileSnapshotService,
                                                     IHandle<DownloadCompletedEvent>,
                                                     IHandle<TrackedDownloadsRemovedEvent>,
                                                     IHandle<ModelEvent<RemotePathMapping>>
    {
        private const string ClientSource = "client";
        private const string DiskSource = "disk";

        private static readonly TimeSpan SnapshotRefreshInterval = TimeSpan.FromHours(6);

        private readonly IDownloadClientFileSnapshotRepository _repository;
        private readonly IDiskProvider _diskProvider;
        private readonly Logger _logger;

        public DownloadClientFileSnapshotService(IDownloadClientFileSnapshotRepository repository,
                                                 IDiskProvider diskProvider,
                                                 Logger logger)
        {
            _repository = repository;
            _diskProvider = diskProvider;
            _logger = logger;
        }

        public void CaptureClientList(DownloadClientItem item)
        {
            if (!CanSnapshot(item) || item.FilePaths == null || item.FilePaths.Count == 0)
            {
                return;
            }

            Upsert(item, item.FilePaths, ClientSource, item.FileListConfidence ?? DownloadClientFileListConfidence.Degraded);
        }

        public void CaptureCompletedOutput(DownloadClientItem item)
        {
            if (!CanSnapshot(item) || item.OutputPath.IsEmpty)
            {
                return;
            }

            if (item.FilePaths != null && item.FilePaths.Count > 0)
            {
                return;
            }

            var outputPath = item.OutputPath.FullPath;
            var filePaths = new List<string>();

            try
            {
                if (_diskProvider.FileExists(outputPath))
                {
                    filePaths.Add(outputPath);
                }
                else if (_diskProvider.FolderExists(outputPath))
                {
                    filePaths.AddRange(_diskProvider.GetFileInfos(outputPath, true).Select(f => f.FullName));
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to snapshot completed output file list for download '{0}' at '{1}'", item.Title, outputPath);
                return;
            }

            if (filePaths.Count == 0)
            {
                return;
            }

            Upsert(item, filePaths, DiskSource, DownloadClientFileListConfidence.Disk);
            ApplySnapshot(item);
        }

        public void ApplySnapshot(DownloadClientItem item)
        {
            if (!CanSnapshot(item))
            {
                return;
            }

            if (item.FilePaths != null && item.FilePaths.Count > 0)
            {
                return;
            }

            var snapshot = _repository.Find(item.DownloadClientInfo.Id, item.DownloadId);
            if (snapshot == null || snapshot.FilePaths == null || snapshot.FilePaths.Count == 0)
            {
                return;
            }

            item.FilePaths = snapshot.FilePaths.ToList();
            item.FileListConfidence = snapshot.Confidence;

            if (item.OutputPath.IsEmpty && snapshot.OutputPath.IsNotNullOrWhiteSpace())
            {
                item.OutputPath = new OsPath(snapshot.OutputPath);
            }
        }

        public void Delete(DownloadClientItem item)
        {
            if (!CanSnapshot(item))
            {
                return;
            }

            _repository.Delete(item.DownloadClientInfo.Id, item.DownloadId);
        }

        public void Handle(DownloadCompletedEvent message)
        {
            Delete(message?.TrackedDownload?.DownloadItem);
            Delete(message?.TrackedDownload?.ImportItem);
        }

        public void Handle(TrackedDownloadsRemovedEvent message)
        {
            foreach (var trackedDownload in message?.TrackedDownloads ?? Enumerable.Empty<TrackedDownload>())
            {
                Delete(trackedDownload?.DownloadItem);
                Delete(trackedDownload?.ImportItem);
            }
        }

        public void Handle(ModelEvent<RemotePathMapping> message)
        {
            if (message?.Action == ModelAction.Deleted && message.Model?.DownloadClientId > 0)
            {
                _repository.DeleteForDownloadClient(message.Model.DownloadClientId);
                _logger.Debug("Cleared download client file snapshots for download client {0} after remote path mapping deletion.", message.Model.DownloadClientId);
                return;
            }

            _repository.Purge();
            _logger.Debug("Cleared download client file snapshots after remote path mapping {0}.", message?.Action);
        }

        private void Upsert(DownloadClientItem item, IEnumerable<string> filePaths, string source, DownloadClientFileListConfidence confidence)
        {
            var normalizedPaths = NormalizeFilePaths(filePaths, item.OutputPath.FullPath)
                .Distinct(StringComparerFromComparison(PathComparison))
                .ToList();
            if (normalizedPaths.Count == 0)
            {
                return;
            }

            var existing = _repository.Find(item.DownloadClientInfo.Id, item.DownloadId);
            var now = DateTime.UtcNow;

            if (existing != null && SamePaths(existing.FilePaths, normalizedPaths))
            {
                // Monitoring re-captures every client item each cycle. Rewriting an unchanged row
                // once a minute per item keeps long-lived rows eternally fresh (so the age-based
                // housekeeper can never reclaim a leaked one) for no informational gain, so refresh
                // the row only when something material changed or the timestamp has gone stale.
                var unchanged = string.Equals(existing.OutputPath, item.OutputPath.FullPath, StringComparison.Ordinal) &&
                                string.Equals(existing.Source, source, StringComparison.Ordinal) &&
                                existing.Confidence == confidence;

                if (unchanged && now - existing.LastUpdated < SnapshotRefreshInterval)
                {
                    return;
                }

                existing.LastUpdated = now;
                existing.OutputPath = item.OutputPath.FullPath;
                existing.Source = source;
                existing.Confidence = confidence;
                _repository.Update(existing);
                LogSnapshot(item, source, confidence, normalizedPaths.Count);
                return;
            }

            if (existing != null && existing.FilePaths?.Count > 0)
            {
                _logger.Debug("Download file snapshot changed for '{0}' ({1} -> {2} files, source={3}).",
                    item.Title,
                    existing.FilePaths.Count,
                    normalizedPaths.Count,
                    source);
            }

            var snapshot = existing ?? new DownloadClientFileSnapshot
            {
                CreatedAt = now
            };

            snapshot.DownloadClientId = item.DownloadClientInfo.Id;
            snapshot.DownloadId = item.DownloadId;
            snapshot.Protocol = item.DownloadClientInfo.Protocol;
            snapshot.Title = item.Title;
            snapshot.Category = item.Category;
            snapshot.OutputPath = item.OutputPath.FullPath;
            snapshot.Source = source;
            snapshot.Confidence = confidence;
            snapshot.FilePaths = normalizedPaths;
            snapshot.LastUpdated = now;

            _repository.Upsert(snapshot);
            item.FilePaths = normalizedPaths.ToList();
            LogSnapshot(item, source, confidence, normalizedPaths.Count);
        }

        private void LogSnapshot(DownloadClientItem item, string source, DownloadClientFileListConfidence confidence, int fileCount)
        {
            _logger.Debug("[DownloadClientFileList] client={0} downloadId={1} result={2} source={3} files={4}",
                item.DownloadClientInfo.Name,
                item.DownloadId,
                confidence,
                source,
                fileCount);
        }

        private IEnumerable<string> NormalizeFilePaths(IEnumerable<string> filePaths, string outputPath)
        {
            foreach (var filePath in filePaths ?? Enumerable.Empty<string>())
            {
                if (filePath.IsNullOrWhiteSpace())
                {
                    continue;
                }

                var normalized = filePath.Trim();
                if (!IsInsideOutputPath(normalized, outputPath))
                {
                    _logger.Debug("Ignoring download file snapshot path outside output path: {0}", normalized);
                    continue;
                }

                yield return normalized;
            }
        }

        private static bool SamePaths(IReadOnlyCollection<string> left, IReadOnlyCollection<string> right)
        {
            if ((left?.Count ?? 0) != (right?.Count ?? 0))
            {
                return false;
            }

            var comparison = PathComparison;
            return left.OrderBy(p => p, StringComparerFromComparison(comparison))
                .SequenceEqual(right.OrderBy(p => p, StringComparerFromComparison(comparison)), StringComparerFromComparison(comparison));
        }

        private static bool IsInsideOutputPath(string filePath, string outputPath)
        {
            if (outputPath.IsNullOrWhiteSpace())
            {
                return true;
            }

            var comparison = PathComparison;
            var normalizedOutput = NormalizePath(outputPath).TrimEnd('/', '\\');
            var normalizedPath = NormalizePath(filePath);

            return normalizedPath.Equals(normalizedOutput, comparison) ||
                   normalizedPath.StartsWith(normalizedOutput + "/", comparison) ||
                   normalizedPath.StartsWith(normalizedOutput + "\\", comparison);
        }

        private static string NormalizePath(string path)
        {
            return (path ?? string.Empty).Trim().Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        }

        private static StringComparison PathComparison => OsInfo.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        private static StringComparer StringComparerFromComparison(StringComparison comparison)
        {
            return comparison == StringComparison.OrdinalIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        }

        private static bool CanSnapshot(DownloadClientItem item)
        {
            return item?.DownloadClientInfo != null &&
                   item.DownloadClientInfo.Id > 0 &&
                   item.DownloadId.IsNotNullOrWhiteSpace();
        }
    }
}
