using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Books;
using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MediaFiles.TorrentInfo;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RemotePathMappings;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Download.Clients.Deluge
{
    public class Deluge : TorrentClientBase<DelugeSettings>, IPreserveDownloadClientItemAfterImport, IRecoverDownloadClientOperations
    {
        private readonly ICached<LabelChangeState> _labelChanges;

        private readonly IDelugeProxy _proxy;
        private readonly IDownloadHistoryRepository _downloadHistory;
        private readonly IDownloadSeedConfigProvider _downloadSeedConfigProvider;

        public Deluge(IDelugeProxy proxy,
                      ITorrentFileInfoReader torrentFileInfoReader,
                      IHttpClient httpClient,
                      IConfigService configService,
                      IDiskProvider diskProvider,
                      IRemotePathMappingService remotePathMappingService,
                      IBlocklistService blocklistService,
                      IDownloadSeedConfigProvider downloadSeedConfigProvider,
                      IDownloadHistoryRepository downloadHistory,
                      ICacheManager cacheManager,
                      Logger logger)
            : base(torrentFileInfoReader, httpClient, configService, diskProvider, remotePathMappingService, blocklistService, logger)
        {
            _proxy = proxy;
            _downloadHistory = downloadHistory;
            _downloadSeedConfigProvider = downloadSeedConfigProvider;
            _labelChanges = cacheManager.GetCache<LabelChangeState>(typeof(Deluge), "labelChanges");
        }

        private string GetPostImportCategory(DownloadClientItem downloadClientItem)
        {
            return PostImportCategoryResolver.Resolve(downloadClientItem,
                Settings.AudiobookCategory,
                Settings.EbookCategory,
                Settings.AudiobookImportedCategory,
                Settings.EbookImportedCategory);
        }

        public override void MarkItemAsImported(DownloadClientItem downloadClientItem)
        {
            // set post-import category
            var postImportCategory = GetPostImportCategory(downloadClientItem);

            if (postImportCategory.IsNotNullOrWhiteSpace() &&
                !postImportCategory.Equals(downloadClientItem.Category, StringComparison.InvariantCultureIgnoreCase))
            {
                try
                {
                    var seedConfiguration = _downloadSeedConfigProvider.GetSeedConfiguration(downloadClientItem.DownloadId);
                    if (seedConfiguration?.KeepSeeding == true)
                    {
                        ChangePreservedLabel(downloadClientItem.DownloadId, postImportCategory);
                    }
                    else
                    {
                        _proxy.SetTorrentLabel(downloadClientItem.DownloadId.ToLowerInvariant(), postImportCategory, Settings);
                    }

                    downloadClientItem.Category = postImportCategory;
                }
                catch (DownloadClientUnavailableException)
                {
                    _logger.Warn("Failed to set torrent post-import label \"{0}\" for {1} in Deluge; check the connection and label-change error",
                        postImportCategory,
                        downloadClientItem.Title);
                }
            }
        }

        private sealed class LabelChange
        {
            public string Label { get; set; }
            public bool AutoManaged { get; set; }
            public bool Resume { get; set; }
            public bool PauseConfirmed { get; set; }
            public bool ResumeAttempted { get; set; }
        }

        private sealed class LabelChangeState
        {
            public bool RecoveryRequired = true;
            public Dictionary<string, (string Name, string Reason)> Failures = new(StringComparer.OrdinalIgnoreCase);
        }

        private LabelChangeState GetLabelChangeState() => _labelChanges.Get(Definition.Id.ToString(), () => new LabelChangeState());

        public IReadOnlyList<(string Name, string Reason)> GetLabelChangeFailures()
        {
            var state = _labelChanges.Find(Definition.Id.ToString());
            if (state == null) return Array.Empty<(string Name, string Reason)>();
            lock (state) return state.Failures.Values.ToList();
        }

        private void ChangePreservedLabel(string downloadId, string label)
        {
            lock (GetLabelChangeState())
            {
                var hash = downloadId.ToLowerInvariant();
                var torrent = _proxy.GetTorrent(hash, Settings);
                var guard = (!string.IsNullOrEmpty(torrent.Label) && _proxy.GetLabelOptions(torrent.Label, Settings).ApplyQueue) ||
                            _proxy.GetLabelOptions(label, Settings).ApplyQueue;
                if (!guard)
                {
                    _proxy.SetTorrentLabel(hash, label, Settings);
                    return;
                }

                var grab = _downloadHistory.FindByDownloadId(downloadId.ToUpperInvariant())
                    .FirstOrDefault(h => h.DownloadClientId == Definition.Id && h.EventType == DownloadHistoryEventType.DownloadGrabbed);
                if (grab == null)
                {
                    throw new DownloadClientException("Cannot safely change a preserved Deluge label without its grab history");
                }

                var pending = grab.Data.TryGetValue(DownloadHistory.PendingLabelChangeKey, out var saved);
                var change = pending
                    ? Json.Deserialize<LabelChange>(saved)
                    : new LabelChange { Label = label, AutoManaged = torrent.IsAutoManaged, Resume = torrent.State is not ("Paused" or "Error") };
                SaveLabelChange(grab, change);
                FinishLabelChange(grab, change);
            }
        }

        private void SaveLabelChange(DownloadHistory grab, LabelChange change)
        {
            // A failed write may have reached the database. Recheck durable state
            // after every write attempt; an idle client needs only its startup check.
            GetLabelChangeState().RecoveryRequired = true;
            if (change == null) grab.Data.Remove(DownloadHistory.PendingLabelChangeKey);
            else grab.Data[DownloadHistory.PendingLabelChangeKey] = change.ToJson();
            _downloadHistory.SetFields(grab, h => h.Data);
        }

        private void FinishLabelChange(DownloadHistory grab, LabelChange change)
        {
            var step = "reading the torrent status";
            try
            {
                var hash = grab.DownloadId.ToLowerInvariant();
                var torrent = _proxy.GetTorrent(hash, Settings);
                if (string.IsNullOrEmpty(torrent?.Hash))
                {
                    step = "saving label-change completion";
                    SaveLabelChange(grab, null);
                    GetLabelChangeState().Failures.Remove(grab.DownloadId);
                    return;
                }

                if (!change.ResumeAttempted)
                {
                    if (!change.PauseConfirmed || torrent.Label != change.Label)
                    {
                        // A pending label write still needs the guard, even if it was
                        // manually resumed meanwhile. Never apply ratio rules while running.
                        if (change.PauseConfirmed && ResumeComplete(torrent)) change.Resume = true;
                        SaveLabelChange(grab, change);
                        step = "pausing the torrent";
                        _proxy.SetTorrentPaused(hash, true, Settings);
                        torrent = WaitForPauseState(hash, true);
                        if (!PauseConfirmed(torrent))
                        {
                            throw UnconfirmedLabelStep("the pause");
                        }

                        change.PauseConfirmed = true;
                        SaveLabelChange(grab, change);
                    }

                    step = "setting the label";
                    if (torrent.Label != change.Label) _proxy.SetTorrentLabel(hash, change.Label, Settings);
                    step = "setting unlimited seeding";
                    _proxy.SetTorrentSeedingConfiguration(hash, new TorrentSeedConfiguration { KeepSeeding = true }, Settings);
                    step = "restoring auto-management";
                    _proxy.SetTorrentConfiguration(hash, "auto_managed", change.AutoManaged, Settings);
                    step = "checking the seeding settings";
                    torrent = _proxy.GetTorrent(hash, Settings);
                    if (torrent.Label != change.Label || torrent.StopAtRatio || torrent.RemoveAtRatio || torrent.IsAutoManaged != change.AutoManaged)
                    {
                        throw UnconfirmedLabelStep(torrent.Label != change.Label ? "the label" :
                            torrent.StopAtRatio || torrent.RemoveAtRatio ? "unlimited seeding" : "auto-management");
                    }
                }

                if (change.Resume && !ResumeComplete(torrent))
                {
                    change.ResumeAttempted = true;
                    SaveLabelChange(grab, change);
                    step = "resuming the torrent";
                    _proxy.SetTorrentPaused(hash, false, Settings);
                    if (!ResumeComplete(WaitForPauseState(hash, false)))
                    {
                        throw UnconfirmedLabelStep("the resume");
                    }
                }

                step = "saving label-change completion";
                SaveLabelChange(grab, null);
                GetLabelChangeState().Failures.Remove(grab.DownloadId);
            }
            catch (Exception ex)
            {
                var reason = ex is DownloadClientUnavailableException || ex is HttpRequestException || ex is WebException || ex is TimeoutException || ex is System.Threading.Tasks.TaskCanceledException
                    ? "Deluge is not reachable"
                    : ex.Data["delugeUnconfirmedStep"] is string unconfirmed
                        ? $"Deluge did not confirm {unconfirmed}"
                        : ex is DownloadClientException
                            ? $"Deluge rejected {step}: {ex.Message}"
                            : $"Unable to finish {step}: {ex.Message}";
                GetLabelChangeState().Failures[grab.DownloadId] = (grab.SourceTitle ?? grab.DownloadId, reason);
                throw;
            }
        }

        private static DownloadClientException UnconfirmedLabelStep(string step)
        {
            var error = new DownloadClientException($"Deluge did not confirm {step}; the label change will retry on the next download check");
            error.Data["delugeUnconfirmedStep"] = step;
            return error;
        }

        private static bool ResumeComplete(DelugeTorrent torrent) => !torrent.Paused || torrent.State == "Queued";

        // Deluge's ratio-removal loop checks the display state before the paused flag.
        private static bool PauseConfirmed(DelugeTorrent torrent) => torrent.Paused && torrent.State == "Paused";

        private DelugeTorrent WaitForPauseState(string hash, bool paused)
        {
            var timer = Stopwatch.StartNew();
            DelugeTorrent torrent;
            do
            {
                torrent = _proxy.GetTorrent(hash, Settings);
                if (paused ? PauseConfirmed(torrent) : ResumeComplete(torrent)) return torrent;
                System.Threading.Thread.Sleep(100);
            }
            while (timer.Elapsed < TimeSpan.FromSeconds(5));
            return torrent;
        }

        public bool ShouldPreserveItemAfterImport(DownloadClientItem downloadClientItem)
        {
            return PostImportCategoryResolver.IsInResolvedPostImportCategory(downloadClientItem,
                Settings.AudiobookCategory,
                Settings.EbookCategory,
                Settings.AudiobookImportedCategory,
                Settings.EbookImportedCategory);
        }

        protected override string AddFromMagnetLink(RemoteBook remoteBook, string hash, string magnetLink)
        {
            var actualHash = AddTorrent(remoteBook, () => _proxy.AddTorrentFromMagnet(magnetLink, Settings));

            if (actualHash.IsNullOrWhiteSpace())
            {
                throw new DownloadClientException("Deluge failed to add magnet " + magnetLink);
            }

            return ConfigureAddedTorrent(remoteBook, actualHash);
        }

        protected override string AddFromTorrentFile(RemoteBook remoteBook, string hash, string filename, byte[] fileContent)
        {
            var actualHash = AddTorrent(remoteBook, () => _proxy.AddTorrentFromFile(filename, fileContent, Settings));

            if (actualHash.IsNullOrWhiteSpace())
            {
                throw new DownloadClientException("Deluge failed to add torrent " + filename);
            }

            return ConfigureAddedTorrent(remoteBook, actualHash);
        }

        private string ConfigureAddedTorrent(RemoteBook remoteBook, string actualHash)
        {
            if (remoteBook.SeedConfiguration?.KeepSeeding != true)
            {
                _proxy.SetTorrentSeedingConfiguration(actualHash, remoteBook.SeedConfiguration, Settings);
            }

            var category = remoteBook.GetPreferredMediaType() == BookMediaType.Ebook ? Settings.EbookCategory : Settings.AudiobookCategory;

            if (category.IsNotNullOrWhiteSpace())
            {
                _proxy.SetTorrentLabel(actualHash, category, Settings);
            }

            if (remoteBook.SeedConfiguration?.KeepSeeding == true)
            {
                _proxy.SetTorrentSeedingConfiguration(actualHash, remoteBook.SeedConfiguration, Settings);
            }

            var isRecentBook = remoteBook.IsRecentBook();

            if ((isRecentBook && Settings.RecentTvPriority == (int)DelugePriority.First) ||
                (!isRecentBook && Settings.OlderTvPriority == (int)DelugePriority.First))
            {
                _proxy.MoveTorrentToTopInQueue(actualHash, Settings);
            }

            return actualHash.ToUpper();
        }

        private static string AddTorrent(RemoteBook remoteBook, Func<string> addAction)
        {
            try
            {
                return addAction();
            }
            catch (DelugeException ex) when (IsExistingTorrentDuplicate(ex))
            {
                throw new DownloadClientRejectedReleaseException(remoteBook.Release, "Deluge rejected the torrent because it is already in the session", ex);
            }
        }

        private static bool IsExistingTorrentDuplicate(DelugeException ex)
        {
            return ex != null &&
                   ex.Code == 4 &&
                   ex.Message.Contains("already in session", StringComparison.InvariantCultureIgnoreCase);
        }

        public override string Name => "Deluge";

        public void RecoverPendingOperations()
        {
            var state = GetLabelChangeState();
            lock (state)
            {
                if (!state.RecoveryRequired) return;
                var timer = Stopwatch.StartNew();
                var pending = _downloadHistory.GetPendingLabelChanges(Definition.Id);
                _logger.Debug("[DelugeLabelRecovery] client={0} pending={1} queryElapsed={2}ms", Definition.Id, pending.Count, timer.ElapsedMilliseconds);
                var pendingIds = pending.Select(grab => grab.DownloadId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var id in state.Failures.Keys.Where(id => !pendingIds.Contains(id)).ToList()) state.Failures.Remove(id);
                var recovered = true;
                foreach (var grab in pending)
                {
                    try
                    {
                        FinishLabelChange(grab, Json.Deserialize<LabelChange>(grab.Data[DownloadHistory.PendingLabelChangeKey]));
                    }
                    catch (Exception ex)
                    {
                        recovered = false;
                        _logger.Warn(ex, "Unable to finish preserved Deluge label change for {0}; it will be retried on the next refresh", grab.DownloadId);
                    }
                }
                state.RecoveryRequired = !recovered;
            }
        }

        public override IEnumerable<DownloadClientItem> GetItems()
        {
            var categories = new[] { Settings.AudiobookCategory, Settings.EbookCategory }
                .Where(c => c.IsNotNullOrWhiteSpace())
                .Distinct(StringComparer.InvariantCultureIgnoreCase)
                .ToList();

            var torrentsByHash = new Dictionary<string, (DelugeTorrent Torrent, string Category)>(StringComparer.InvariantCultureIgnoreCase);

            if (categories.Any())
            {
                foreach (var category in categories)
                {
                    foreach (var torrent in _proxy.GetTorrentsByLabel(category, Settings))
                    {
                        if (torrent.Hash.IsNullOrWhiteSpace())
                        {
                            continue;
                        }

                        torrentsByHash[torrent.Hash] = (torrent, category);
                    }
                }
            }
            else
            {
                foreach (var torrent in _proxy.GetTorrents(Settings))
                {
                    if (torrent.Hash.IsNullOrWhiteSpace())
                    {
                        continue;
                    }

                    torrentsByHash[torrent.Hash] = (torrent, null);
                }
            }

            var items = new List<DownloadClientItem>();
            var ignoredCount = 0;

            foreach (var entry in torrentsByHash.Values)
            {
                var torrent = entry.Torrent;

                // Silently ignore torrents with no hash
                if (torrent.Hash.IsNullOrWhiteSpace())
                {
                    continue;
                }

                // Ignore torrents without a name, but track to log a single warning for all invalid torrents.
                if (torrent.Name.IsNullOrWhiteSpace())
                {
                    ignoredCount++;
                    continue;
                }

                var item = new DownloadClientItem();
                item.DownloadId = torrent.Hash.ToUpper();
                item.Title = torrent.Name;
                item.Category = entry.Category;

                item.DownloadClientInfo = DownloadClientItemClientInfo.FromDownloadClient(this,
                    Settings.AudiobookImportedCategory.IsNotNullOrWhiteSpace() || Settings.EbookImportedCategory.IsNotNullOrWhiteSpace());

                var outputPath = RemapRemoteToLocal(new OsPath(torrent.DownloadPath));
                item.OutputPath = outputPath + torrent.Name;
                item.RemainingSize = torrent.Size - torrent.BytesDownloaded;
                item.SeedRatio = torrent.Ratio;

                try
                {
                    item.RemainingTime = TimeSpan.FromSeconds(torrent.Eta);
                }
                catch (OverflowException ex)
                {
                    _logger.Debug(ex, "ETA for {0} is too long: {1}", torrent.Name, torrent.Eta);
                    item.RemainingTime = TimeSpan.MaxValue;
                }

                item.TotalSize = torrent.Size;

                if (torrent.State == DelugeTorrentStatus.Error)
                {
                    item.Status = DownloadItemStatus.Warning;
                    item.Message = "Deluge is reporting an error";
                }
                else if (torrent.IsFinished && torrent.State != DelugeTorrentStatus.Checking)
                {
                    item.Status = DownloadItemStatus.Completed;
                }
                else if (torrent.State == DelugeTorrentStatus.Queued)
                {
                    item.Status = DownloadItemStatus.Queued;
                }
                else if (torrent.State == DelugeTorrentStatus.Paused)
                {
                    item.Status = DownloadItemStatus.Paused;
                }
                else
                {
                    item.Status = DownloadItemStatus.Downloading;
                }

                // Here we detect if Deluge is managing the torrent and whether the seed criteria has been met.
                // This allows drone to delete the torrent as appropriate.
                item.CanMoveFiles = item.CanBeRemoved =
                    item.DownloadClientInfo.RemoveCompletedDownloads &&
                    torrent.IsAutoManaged &&
                    torrent.StopAtRatio &&
                    torrent.Ratio >= torrent.StopRatio &&
                    torrent.State == DelugeTorrentStatus.Paused;

                items.Add(item);
            }

            if (ignoredCount > 0)
            {
                _logger.Warn("{0} torrent(s) were ignored becuase they did not have a title, check Deluge and remove any invalid torrents");
            }

            return items;
        }

        public override void RemoveItem(DownloadClientItem item, bool deleteData)
        {
            _proxy.RemoveTorrent(item.DownloadId.ToLower(), deleteData, Settings);
        }

        public override DownloadClientItem GetImportItem(DownloadClientItem item, DownloadClientItem previousImportAttempt)
        {
            var result = item.Clone();

            if (item.DownloadId.IsNullOrWhiteSpace())
            {
                _logger.Debug("No torrent hash found for torrent {0} in Deluge; using existing output path.", item.Title);
                return result;
            }

            DelugeTorrentDetails details;
            try
            {
                details = _proxy.GetTorrentDetails(item.DownloadId.ToLower(), Settings);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to fetch Deluge file list for torrent {0}; using existing output path.", item.Title);
                return result;
            }

            if (details?.Files == null || details.Files.Count == 0)
            {
                _logger.Debug("No files found for torrent {0} in Deluge", item.Title);
                return result;
            }

            if (details.DownloadPath.IsNullOrWhiteSpace())
            {
                _logger.Debug("No save path found for torrent {0} in Deluge; using existing output path.", item.Title);
                return result;
            }

            var savePath = new OsPath(details.DownloadPath);
            result.FilePaths = details.Files
                .Select((file, index) => new { File = file, Index = index })
                .Where(x => IsWanted(details, x.Index))
                .Select(x => x.File.Path)
                .Where(p => p.IsNotNullOrWhiteSpace())
                .Select(p => RemapRemoteToLocal(TorrentClientPathHelper.CombineClientPath(savePath, p)).FullPath)
                .Where(p => p.IsNotNullOrWhiteSpace())
                .Distinct(StringComparer.InvariantCultureIgnoreCase)
                .ToList();
            result.FileListConfidence = DownloadClientFileListConfidence.Authoritative;

            return result;
        }

        private static bool IsWanted(DelugeTorrentDetails details, int index)
        {
            return details.FilePriorities == null ||
                   index >= details.FilePriorities.Count ||
                   details.FilePriorities[index] > 0;
        }

        public override DownloadClientInfo GetStatus()
        {
            var config = _proxy.GetConfig(Settings);
            var label = Settings.MusicCategory.IsNotNullOrWhiteSpace() ? _proxy.GetLabelOptions(Settings.MusicCategory, Settings) : null;

            OsPath destDir;

            if (Settings.CompletedDirectory.IsNotNullOrWhiteSpace())
            {
                destDir = new OsPath(Settings.CompletedDirectory);
            }
            else if (Settings.DownloadDirectory.IsNotNullOrWhiteSpace())
            {
                destDir = new OsPath(Settings.DownloadDirectory);
            }
            else if (label is { ApplyMoveCompleted: true, MoveCompleted: true })
            {
                // if label exists and a label completed path exists and is enabled use it instead of global
                destDir = new OsPath(label.MoveCompletedPath);
            }
            else if (config.GetValueOrDefault("move_completed", false).ToString() == "True")
            {
                destDir = new OsPath(config.GetValueOrDefault("move_completed_path") as string);
            }
            else
            {
                destDir = new OsPath(config.GetValueOrDefault("download_location") as string);
            }

            var status = new DownloadClientInfo
            {
                IsLocalhost = Settings.Host is "127.0.0.1" or "localhost"
            };

            if (!destDir.IsEmpty)
            {
                status.OutputRootFolders = new List<OsPath> { RemapRemoteToLocal(destDir) };
            }

            return status;
        }

        public override object RequestAction(string action, IDictionary<string, string> query)
        {
            if (action == "getCategories")
            {
                // Return empty if no password is configured
                if (string.IsNullOrWhiteSpace(Settings.Password))
                {
                    return new
                    {
                        options = new List<object>
                        {
                            new { value = string.Empty, name = "None" }
                        }
                    };
                }

                try
                {
                    // Check if Label plugin is enabled
                    var enabledPlugins = _proxy.GetEnabledPlugins(Settings);
                    if (!enabledPlugins.Contains("Label"))
                    {
                        // Label plugin not enabled, return just None option
                        return new
                        {
                            options = new List<object>
                            {
                                new { value = string.Empty, name = "None (Label plugin not enabled)" }
                            }
                        };
                    }

                    // Get available labels
                    var labels = _proxy.GetAvailableLabels(Settings);
                    var options = new List<object>
                    {
                        new { value = string.Empty, name = "None" }
                    };

                    options.AddRange(labels.OrderBy(l => l).Select(label => new
                    {
                        value = label,
                        name = label
                    }));

                    return new { options };
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Unable to retrieve labels from Deluge");
                    return new
                    {
                        options = new List<object>
                        {
                            new { value = string.Empty, name = "None" }
                        }
                    };
                }
            }

            return base.RequestAction(action, query);
        }

        protected override void Test(List<ValidationFailure> failures)
        {
            failures.AddIfNotNull(TestConnection());
            if (failures.HasErrors())
            {
                return;
            }

            failures.AddIfNotNull(TestCategory());
            failures.AddIfNotNull(TestGetTorrents());
        }

        private ValidationFailure TestConnection()
        {
            try
            {
                _proxy.GetVersion(Settings);
            }
            catch (DownloadClientAuthenticationException ex)
            {
                _logger.Error(ex, "Unable to authenticate");
                return new NzbDroneValidationFailure("Password", "Authentication failed");
            }
            catch (WebException ex)
            {
                _logger.Error(ex, "Unable to test connection");
                switch (ex.Status)
                {
                    case WebExceptionStatus.ConnectFailure:
                        return new NzbDroneValidationFailure("Host", "Unable to connect")
                        {
                            DetailedDescription = "Please verify the hostname and port."
                        };
                    case WebExceptionStatus.ConnectionClosed:
                        return new NzbDroneValidationFailure("UseSsl", "Verify SSL settings")
                        {
                            DetailedDescription = "Please verify your SSL configuration on both Deluge and Chaptarr."
                        };
                    case WebExceptionStatus.SecureChannelFailure:
                        return new NzbDroneValidationFailure("UseSsl", "Unable to connect through SSL")
                        {
                            DetailedDescription = "Chaptarr is unable to connect to Deluge using SSL. This problem could be computer related. Please try to configure both drone and Deluge to not use SSL."
                        };
                    default:
                        return new NzbDroneValidationFailure(string.Empty, "Unknown exception: " + ex.Message);
                }
            }
            catch (HttpRequestException ex)
            {
                _logger.Error(ex, "HTTP connection failed");

                if (ex.Message.Contains("Connection refused"))
                {
                    return new NzbDroneValidationFailure("Host", "Connection refused")
                    {
                        DetailedDescription = $"Unable to connect to Deluge at {Settings.Host}:{Settings.Port}. Please verify Deluge is running and accessible."
                    };
                }
                else if (ex.Message.Contains("No such host"))
                {
                    return new NzbDroneValidationFailure("Host", "Invalid hostname")
                    {
                        DetailedDescription = $"Unable to resolve hostname '{Settings.Host}'. Please verify the hostname is correct."
                    };
                }

                return new NzbDroneValidationFailure("Host", "Connection failed")
                {
                    DetailedDescription = ex.Message
                };
            }
            catch (DownloadClientUnavailableException ex)
            {
                _logger.Error(ex, "Unable to connect to Deluge");

                if (ex.Message.Contains("Connection refused"))
                {
                    return new NzbDroneValidationFailure("Host", "Connection refused")
                    {
                        DetailedDescription = ex.Message
                    };
                }
                else if (ex.Message.Contains("hostname"))
                {
                    return new NzbDroneValidationFailure("Host", "Invalid hostname")
                    {
                        DetailedDescription = ex.Message
                    };
                }

                return new NzbDroneValidationFailure("Host", "Unable to connect")
                {
                    DetailedDescription = ex.Message
                };
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to test connection");

                return new NzbDroneValidationFailure(string.Empty, "Unknown error")
                {
                    DetailedDescription = ex.Message
                };
            }

            return null;
        }

        private ValidationFailure TestCategory()
        {
            var initialCategories = new[] { Settings.AudiobookCategory, Settings.EbookCategory }
                .Where(c => c.IsNotNullOrWhiteSpace())
                .Distinct(StringComparer.InvariantCultureIgnoreCase)
                .ToList();

            var postImportCategories = new[] { Settings.AudiobookImportedCategory, Settings.EbookImportedCategory }
                .Where(c => c.IsNotNullOrWhiteSpace())
                .Distinct(StringComparer.InvariantCultureIgnoreCase)
                .ToList();

            if (initialCategories.Empty() && postImportCategories.Empty())
            {
                return null;
            }

            var enabledPlugins = _proxy.GetEnabledPlugins(Settings);

            if (!enabledPlugins.Contains("Label"))
            {
                var field = initialCategories.Any() ? nameof(DelugeSettings.AudiobookCategory) : nameof(DelugeSettings.AudiobookImportedCategory);
                return new NzbDroneValidationFailure(field, "Label plugin not activated")
                {
                    DetailedDescription = "You must have the Label plugin enabled in Deluge to use categories."
                };
            }

            var labels = _proxy.GetAvailableLabels(Settings);

            foreach (var pair in new[]
                     {
                         new { Field = nameof(DelugeSettings.AudiobookCategory), Value = Settings.AudiobookCategory },
                         new { Field = nameof(DelugeSettings.EbookCategory), Value = Settings.EbookCategory },
                         new { Field = nameof(DelugeSettings.AudiobookImportedCategory), Value = Settings.AudiobookImportedCategory },
                         new { Field = nameof(DelugeSettings.EbookImportedCategory), Value = Settings.EbookImportedCategory }
                     })
            {
                if (pair.Value.IsNullOrWhiteSpace() || labels.Contains(pair.Value))
                {
                    continue;
                }

                _proxy.AddLabel(pair.Value, Settings);
                labels = _proxy.GetAvailableLabels(Settings);

                if (!labels.Contains(pair.Value))
                {
                    return new NzbDroneValidationFailure(pair.Field, "Configuration of label failed")
                    {
                        DetailedDescription = "Chaptarr was unable to add the label to Deluge."
                    };
                }
            }

            return null;
        }

        private ValidationFailure TestGetTorrents()
        {
            try
            {
                _proxy.GetTorrents(Settings);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Unable to get torrents");
                return new NzbDroneValidationFailure(string.Empty, "Failed to get the list of torrents: " + ex.Message);
            }

            return null;
        }
    }
}
