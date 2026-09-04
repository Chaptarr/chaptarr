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
        private readonly IBrowserDownloadResolver _browserResolver;
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeDownloads = new(StringComparer.OrdinalIgnoreCase);

        public DirectDownloadClient(IHttpClient httpClient,
                                    IDiskProvider diskProvider,
                                    IConfigService configService,
                                    Logger logger,
                                    DirectDownloadGrabUrlResolver grabUrlResolver = null,
                                    IBrowserDownloadResolver browserResolver = null)
            : base(configService, diskProvider, new NoopRemotePathMappingService(), logger)
        {
            _httpClient = httpClient;
            _stateStore = new DirectDownloadClientStateStore(diskProvider, logger);
            _grabUrlResolver = grabUrlResolver;
            _browserResolver = browserResolver ?? new NullBrowserDownloadResolver();
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

            if (!IsValidPathSegment(downloadId))
            {
                throw new ReleaseDownloadException(remoteBook.Release, "Direct release identifier contains invalid characters.");
            }

            var existing = _stateStore.Find(Settings.StagingFolder, Definition.Id, downloadId);
            if (existing != null)
            {
                throw new DownloadClientRejectedReleaseException(remoteBook.Release, $"Direct download '{remoteBook.Release.Title}' is already staged.");
            }

            var originalUrl = remoteBook.Release.DownloadUrl;
            var indexerSettings = indexer?.Definition?.Settings as DirectDownloadSettings;
            var slowFallbackEnabled = indexerSettings?.EnableSlowFallback ?? false;

            // API-only resolution — never invokes Playwright at grab time.
            var grabResolution = await TryResolveGrabAsync(originalUrl, remoteBook.Release.Source, indexer);

            string resolvedUrl;
            DirectDownloadFallbackMode fallbackMode;

            switch (grabResolution.Outcome)
            {
                case GrabResolutionOutcome.Success:
                    resolvedUrl = grabResolution.ResolvedUrl;
                    fallbackMode = DirectDownloadFallbackMode.None;
                    break;

                case GrabResolutionOutcome.NotApplicable:
                    // Non-catalog source or URL that doesn't need resolution — pass through.
                    resolvedUrl = originalUrl;
                    fallbackMode = DirectDownloadFallbackMode.None;
                    break;

                case GrabResolutionOutcome.Unavailable:
                    if (slowFallbackEnabled)
                    {
                        // Defer Playwright to background transfer — do not block grab.
                        resolvedUrl = originalUrl;
                        fallbackMode = DirectDownloadFallbackMode.DeferredPlaywright;
                    }
                    else
                    {
                        throw new ReleaseDownloadException(
                            remoteBook.Release,
                            $"Direct source could not resolve a download URL via API and browser fallback is disabled. {grabResolution.Reason}");
                    }

                    break;

                default:
                    throw new ReleaseDownloadException(
                        remoteBook.Release,
                        $"Unexpected grab resolution outcome: {grabResolution.Outcome}");
            }

            var fileName = BuildFileName(remoteBook.Release);
            var downloadDirectory = _stateStore.GetDownloadDirectory(Settings.StagingFolder, Definition.Id, downloadId);
            var outputPath = Path.Combine(downloadDirectory, fileName);
            var partPath = outputPath + ".part";

            var state = new DirectDownloadClientState
            {
                DownloadId = downloadId,
                Title = remoteBook.Release.Title,
                DownloadUrl = resolvedUrl,
                ResolvedUrl = grabResolution.Outcome == GrabResolutionOutcome.Success ? resolvedUrl : null,
                FallbackMode = fallbackMode,
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

        private async Task<GrabResolution> TryResolveGrabAsync(string downloadUrl, string source, IIndexer indexer)
        {
            if (_grabUrlResolver == null)
            {
                return GrabResolution.NotApplicable(downloadUrl);
            }

            var indexerSettings = indexer?.Definition?.Settings as DirectDownloadSettings;
            var apiKey = indexerSettings?.ApiKey;
            return await _grabUrlResolver.TryResolveGrabAsync(downloadUrl, apiKey, source);
        }

    }
}
