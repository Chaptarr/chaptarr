using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MediaFiles.TorrentInfo;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RemotePathMappings;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Download.Clients.Deluge
{
    public class Deluge : TorrentClientBase<DelugeSettings>, IPreserveDownloadClientItemAfterImport
    {
        private readonly IDelugeProxy _proxy;

        public Deluge(IDelugeProxy proxy,
                      ITorrentFileInfoReader torrentFileInfoReader,
                      IHttpClient httpClient,
                      IConfigService configService,
                      IDiskProvider diskProvider,
                      IRemotePathMappingService remotePathMappingService,
                      IBlocklistService blocklistService,
                      Logger logger)
            : base(torrentFileInfoReader, httpClient, configService, diskProvider, remotePathMappingService, blocklistService, logger)
        {
            _proxy = proxy;
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
                    _proxy.SetTorrentLabel(downloadClientItem.DownloadId.ToLower(), postImportCategory, Settings);
                    downloadClientItem.Category = postImportCategory;
                }
                catch (DownloadClientUnavailableException)
                {
                    _logger.Warn("Failed to set torrent post-import label \"{0}\" for {1} in Deluge. Does the label exist?",
                        postImportCategory,
                        downloadClientItem.Title);
                }
            }
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

            _proxy.SetTorrentSeedingConfiguration(actualHash, remoteBook.SeedConfiguration, Settings);

            var category = remoteBook.GetPreferredMediaType() == BookMediaType.Ebook ? Settings.EbookCategory : Settings.AudiobookCategory;

            if (category.IsNotNullOrWhiteSpace())
            {
                _proxy.SetTorrentLabel(actualHash, category, Settings);
            }

            var isRecentBook = remoteBook.IsRecentBook();

            if ((isRecentBook && Settings.RecentTvPriority == (int)DelugePriority.First) ||
                (!isRecentBook && Settings.OlderTvPriority == (int)DelugePriority.First))
            {
                _proxy.MoveTorrentToTopInQueue(actualHash, Settings);
            }

            return actualHash.ToUpper();
        }

        protected override string AddFromTorrentFile(RemoteBook remoteBook, string hash, string filename, byte[] fileContent)
        {
            var actualHash = AddTorrent(remoteBook, () => _proxy.AddTorrentFromFile(filename, fileContent, Settings));

            if (actualHash.IsNullOrWhiteSpace())
            {
                throw new DownloadClientException("Deluge failed to add torrent " + filename);
            }

            _proxy.SetTorrentSeedingConfiguration(actualHash, remoteBook.SeedConfiguration, Settings);

            var category = remoteBook.GetPreferredMediaType() == BookMediaType.Ebook ? Settings.EbookCategory : Settings.AudiobookCategory;

            if (category.IsNotNullOrWhiteSpace())
            {
                _proxy.SetTorrentLabel(actualHash, category, Settings);
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
            var label = Settings.MusicCategory.IsNotNullOrWhiteSpace() ? _proxy.GetLabelOptions(Settings) : null;

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
