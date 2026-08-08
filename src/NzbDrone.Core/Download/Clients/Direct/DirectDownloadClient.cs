using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Common.TPL;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Validation;

using NzbDrone.Core.Indexers.DirectDownload;

namespace NzbDrone.Core.Download.Clients.Direct
{
    public partial class DirectDownloadClient : DownloadClientBase<DirectDownloadClientSettings>
    {
        private const int MaxAttempts = 3;
        private static readonly TimeSpan BaseRetryDelay = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(8);

        private readonly IHttpClient _httpClient;
        private readonly DirectDownloadClientStateStore _stateStore;
        private readonly DirectDownloadGrabUrlResolver _grabUrlResolver;
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeDownloads = new(StringComparer.OrdinalIgnoreCase);

        public DirectDownloadClient(IHttpClient httpClient,
                                    IDiskProvider diskProvider,
                                    IConfigService configService,
                                    Logger logger,
                                    DirectDownloadGrabUrlResolver grabUrlResolver = null)
            : base(configService, diskProvider, new NoopRemotePathMappingService(), logger)
        {
            _httpClient = httpClient;
            _stateStore = new DirectDownloadClientStateStore(diskProvider, logger);
            _grabUrlResolver = grabUrlResolver;
        }

        public override string Name => "Direct Download";

        public override DownloadProtocol Protocol => DownloadProtocol.Direct;

        public override async Task<string> Download(RemoteBook remoteBook, IIndexer indexer)
        {
            ValidateDownload(remoteBook);

            if (!_diskProvider.FolderExists(Settings.StagingFolder))
            {
                throw new ReleaseDownloadException(remoteBook.Release, $"Direct staging folder '{Settings.StagingFolder}' does not exist.");
            }

            var downloadId = remoteBook.Release.Guid?.Trim();
            if (downloadId.IsNullOrWhiteSpace())
            {
                throw new ReleaseDownloadException(remoteBook.Release, "Direct release is missing a stable identifier.");
            }

            var existing = _stateStore.Find(Settings.StagingFolder, Definition.Id, downloadId);
            if (existing != null)
            {
                throw new DownloadClientRejectedReleaseException(remoteBook.Release, $"Direct download '{remoteBook.Release.Title}' is already staged.");
            }

            var downloadUrl = remoteBook.Release.DownloadUrl;
            downloadUrl = await TryResolveGrabUrlAsync(downloadUrl, remoteBook.Release.Source, indexer);

            var fileName = BuildFileName(remoteBook.Release);
            var downloadDirectory = _stateStore.GetDownloadDirectory(Settings.StagingFolder, Definition.Id, downloadId);
            var outputPath = Path.Combine(downloadDirectory, fileName);
            var partPath = outputPath + ".part";

            var state = new DirectDownloadClientState
            {
                DownloadId = downloadId,
                Title = remoteBook.Release.Title,
                DownloadUrl = downloadUrl,
                Status = DownloadItemStatus.Queued,
                OutputFilePath = outputPath,
                PartFilePath = partPath,
                TotalSize = Math.Max(0, remoteBook.Release.Size),
                DownloadedBytes = 0,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };

            _stateStore.Save(Settings.StagingFolder, Definition.Id, state);
            StartDownload(state.DownloadId);
            await Task.CompletedTask;
            return state.DownloadId;
        }

        public override IEnumerable<DownloadClientItem> GetItems()
        {
            var states = _stateStore.LoadAll(Settings.StagingFolder, Definition.Id).ToList();
            foreach (var state in states)
            {
                ReconcileState(state);
                if (state.Status is DownloadItemStatus.Queued or DownloadItemStatus.Downloading)
                {
                    StartDownload(state.DownloadId);
                }
            }

            return states.Select(BuildItem).ToList();
        }

        public override DownloadClientItem GetImportItem(DownloadClientItem item, DownloadClientItem previousImportAttempt)
        {
            var result = item.Clone();
            var state = _stateStore.Find(Settings.StagingFolder, Definition.Id, item.DownloadId);
            if (state == null || state.Status != DownloadItemStatus.Completed || !_diskProvider.FileExists(state.OutputFilePath))
            {
                return result;
            }

            result.OutputPath = new OsPath(state.OutputFilePath);
            result.FilePaths = new List<string> { state.OutputFilePath };
            result.FileListConfidence = DownloadClientFileListConfidence.Authoritative;
            return result;
        }

        public override void RemoveItem(DownloadClientItem item, bool deleteData)
        {
            if (item == null)
            {
                return;
            }

            if (_activeDownloads.TryRemove(item.DownloadId, out var cancellationTokenSource))
            {
                cancellationTokenSource.Cancel();
                cancellationTokenSource.Dispose();
            }

            var state = _stateStore.Find(Settings.StagingFolder, Definition.Id, item.DownloadId);
            if (state == null)
            {
                return;
            }

            _stateStore.Delete(Settings.StagingFolder, Definition.Id, item.DownloadId);
            if (deleteData)
            {
                DeleteIfPresent(state.PartFilePath);
                DeleteIfPresent(state.OutputFilePath);
            }

            DeleteDirectoryIfEmpty(state.OutputDirectory);
        }

        public override DownloadClientInfo GetStatus()
        {
            return new DownloadClientInfo
            {
                IsLocalhost = true,
                OutputRootFolders = Settings.StagingFolder.IsNotNullOrWhiteSpace()
                    ? new List<OsPath> { new(Settings.StagingFolder) }
                    : new List<OsPath>()
            };
        }

        public override void MarkItemAsImported(DownloadClientItem downloadClientItem)
        {
            var state = _stateStore.Find(Settings.StagingFolder, Definition.Id, downloadClientItem?.DownloadId);
            if (state == null)
            {
                return;
            }

            state.ImportedAtUtc = DateTime.UtcNow;
            _stateStore.Save(Settings.StagingFolder, Definition.Id, state);
        }

        protected override void Test(List<ValidationFailure> failures)
        {
            failures.AddIfNotNull(TestFolder(Settings.StagingFolder, nameof(Settings.StagingFolder)));
        }

        private void StartDownload(string downloadId)
        {
            if (_activeDownloads.ContainsKey(downloadId))
            {
                return;
            }

            var state = _stateStore.Find(Settings.StagingFolder, Definition.Id, downloadId);
            if (state == null || state.Status is DownloadItemStatus.Completed or DownloadItemStatus.Failed)
            {
                return;
            }

            var cancellationTokenSource = new CancellationTokenSource();
            if (!_activeDownloads.TryAdd(downloadId, cancellationTokenSource))
            {
                cancellationTokenSource.Dispose();
                return;
            }

            Task.Run(() => DownloadInternalAsync(downloadId, cancellationTokenSource.Token)).LogExceptions();
        }

        private async Task<string> TryResolveGrabUrlAsync(string downloadUrl, string source, IIndexer indexer)
        {
            if (_grabUrlResolver == null)
            {
                return downloadUrl;
            }

            var indexerSettings = indexer?.Definition?.Settings as DirectDownloadSettings;
            var apiKey = indexerSettings?.ApiKey;
            var slowFallbackEnabled = indexerSettings?.EnableSlowFallback ?? false;
            return await _grabUrlResolver.TryResolveAsync(downloadUrl, apiKey, source, slowFallbackEnabled);
        }

    }
}
