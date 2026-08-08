using System;
using System.Linq;
using NLog;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MediaFiles.Events;

namespace NzbDrone.Core.Books.Commands
{
    public class ImportDiscoveredAuthorCommandHandler : IExecute<ImportDiscoveredAuthorCommand>
    {
        private readonly IAuthorLibraryService _authorLibraryService;
        private readonly IAuthorService _authorService;
        private readonly IRootFolderService _rootFolderService;
        private readonly Logger _logger;
        private readonly IEventAggregator _eventAggregator;

        public ImportDiscoveredAuthorCommandHandler(
            IAuthorLibraryService authorLibraryService,
            IAuthorService authorService,
            IRootFolderService rootFolderService,
            Logger logger,
            IEventAggregator eventAggregator)
        {
            _authorLibraryService = authorLibraryService;
            _authorService = authorService;
            _rootFolderService = rootFolderService;
            _logger = logger;
            _eventAggregator = eventAggregator;
        }

        public void Execute(ImportDiscoveredAuthorCommand message)
        {
            if (string.IsNullOrWhiteSpace(message.ProviderId))
            {
                _logger.Warn("[DISCOVERED-AUTHOR] Missing ProviderId; skipping import");
                return;
            }

            try
            {
                _logger.Info("[DISCOVERED-AUTHOR] Importing {0} into root '{1}'", message.ProviderId, message.RootFolderPath);

                // If the author already exists locally, augment missing per-media settings from this root
                var (prefix, id) = SplitProviderId(message.ProviderId);
                var existing = _authorService.FindByProviderId(prefix, id);
                if (existing != null)
                {
                    _logger.Debug("[DISCOVERED-AUTHOR] Author already exists locally (ID: {0}); augmenting missing settings from root '{1}'", existing.Id, message.RootFolderPath);

                    var augmentRoot = ResolveRootFolder(message.RootFolderPath);
                    if (augmentRoot == null)
                    {
                        _logger.Error("[DISCOVERED-AUTHOR] Root folder not found for augmentation: {0}", message.RootFolderPath);
                        return;
                    }

                    // Gather per-media settings from root
                    var a = augmentRoot.GetAudiobookSettings();
                    var e = augmentRoot.GetEbookSettings();

                    // Prepare progressive update args
                    int? auQp = null, ebQp = null;
                    int? auMp = null, ebMp = null;
                    int? auMonExist = null, ebMonExist = null;
                    bool? auMonFuture = null, ebMonFuture = null;

                    switch (augmentRoot.FolderType)
                    {
                        case FolderType.Audiobook:
                            if (a != null)
                            {
                                auQp = a.QualityProfileId;
                                auMp = a.MetadataProfileId;
                                auMonExist = a.MonitorExisting;
                                auMonFuture = a.MonitorFuture;
                            }
                            break;
                        case FolderType.Ebook:
                            if (e != null)
                            {
                                ebQp = e.QualityProfileId;
                                ebMp = e.MetadataProfileId;
                                ebMonExist = e.MonitorExisting;
                                ebMonFuture = e.MonitorFuture;
                            }
                            break;
                        case FolderType.Mixed:
                        default:
                            if (a != null)
                            {
                                auQp = a.QualityProfileId;
                                auMp = a.MetadataProfileId;
                                auMonExist = a.MonitorExisting;
                                auMonFuture = a.MonitorFuture;
                            }
                            if (e != null)
                            {
                                ebQp = e.QualityProfileId;
                                ebMp = e.MetadataProfileId;
                                ebMonExist = e.MonitorExisting;
                                ebMonFuture = e.MonitorFuture;
                            }
                            break;
                    }

                    var before = new { existing.AudiobookQualityProfileId, existing.EbookQualityProfileId, existing.AudiobookMonitorExisting, existing.AudiobookMonitorFuture, existing.EbookMonitorExisting, existing.EbookMonitorFuture };
                    var updated = _authorService.UpdateAuthorProgressiveSettings(
                        existing,
                        auQp, auMp, auMonExist, auMonFuture,
                        ebQp, ebMp, ebMonExist, ebMonFuture,
                        augmentRoot.Path);

                    // Optionally set discovered media-type path if provided and not already set
                    if (!string.IsNullOrWhiteSpace(message.DiscoveredAuthorFolderPath))
                    {
                        var changedPath = false;
                        if ((augmentRoot.FolderType == FolderType.Audiobook || augmentRoot.FolderType == FolderType.Mixed) && string.IsNullOrWhiteSpace(updated.AudiobookPath))
                        {
                            updated.AudiobookPath = message.DiscoveredAuthorFolderPath;
                            changedPath = true;
                        }
                        if ((augmentRoot.FolderType == FolderType.Ebook || augmentRoot.FolderType == FolderType.Mixed) && string.IsNullOrWhiteSpace(updated.EbookPath))
                        {
                            updated.EbookPath = message.DiscoveredAuthorFolderPath;
                            changedPath = true;
                        }
                        if (changedPath)
                        {
                            updated = _authorService.UpdateAuthor(updated);
                        }
                    }

                    var after = new { updated.AudiobookQualityProfileId, updated.EbookQualityProfileId, updated.AudiobookMonitorExisting, updated.AudiobookMonitorFuture, updated.EbookMonitorExisting, updated.EbookMonitorFuture };
                    _logger.Debug("[DISCOVERED-AUTHOR] Augmentation complete for author {0}: {1} -> {2}", updated.Id, Newtonsoft.Json.JsonConvert.SerializeObject(before), Newtonsoft.Json.JsonConvert.SerializeObject(after));

                    // Ensure event-driven matching kicks off for existing authors too
                    try
                    {
                        _eventAggregator.PublishEvent(new AuthorRefreshCompleteEvent(updated));
                        _logger.Debug("[DISCOVERED-AUTHOR] Published AuthorRefreshCompleteEvent for existing author {0}", updated.Name);
                    }
                    catch (Exception exEvt)
                    {
                        _logger.Debug(exEvt, "[DISCOVERED-AUTHOR] Failed to publish AuthorRefreshCompleteEvent for existing author {0}", updated.Name);
                    }
                    return;
                }

                // Resolve root folder
                var root = ResolveRootFolder(message.RootFolderPath);
                if (root == null)
                {
                    _logger.Error("[DISCOVERED-AUTHOR] Root folder not found: {0}", message.RootFolderPath);
                    return;
                }

                // Build MonitoringConfig per root type. Always create both instances.
                var config = new MonitoringConfig
                {
                    CreateAudiobook = true,
                    CreateEbook = true,
                    RequestedBy = message.RequestedBy,
                    DiscoveredAuthorFolderPath = message.DiscoveredAuthorFolderPath
                };

                // Apply per-media settings from the root
                switch (root.FolderType)
                {
                    case FolderType.Audiobook:
                        ApplyAudiobookSettings(config, root);
                        // Leave ebook settings unset (null) — instance will exist but be unmonitored
                        break;
                    case FolderType.Ebook:
                        ApplyEbookSettings(config, root);
                        // Leave audiobook settings unset (null)
                        break;
                    case FolderType.Mixed:
                    default:
                        // Mixed: apply BOTH audiobook and ebook settings simultaneously
                        ApplyAudiobookSettings(config, root);
                        ApplyEbookSettings(config, root);
                        break;
                }

                // Import via library service (handles inheritance, transactions, events)
                var added = _authorLibraryService.AddAuthorAsync(message.ProviderId, config).GetAwaiter().GetResult();
                _logger.Info("[DISCOVERED-AUTHOR] Successfully imported {0}", message.ProviderId);

                // Publish progress update for UI (authors imported +1)
                if (added != null)
                {
                    var evt = new ImportStageProgressEvent(ImportStage.ImportingAuthorsToDatabase,
                        $"Imported author: {added.Name}")
                    {
                        AuthorsImported = 1,
                        CurrentItemName = added.Name,
                        CurrentItemType = "author"
                    };
                    _eventAggregator.PublishEvent(evt);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[DISCOVERED-AUTHOR] Error importing {0}", message.ProviderId);
            }
        }

        private void ApplyAudiobookSettings(MonitoringConfig config, RootFolder root)
        {
            var s = root.GetAudiobookSettings();
            config.AudiobookRootFolderPath = root.Path;
            if (s != null)
            {
                config.AudiobookQualityProfileId = s.QualityProfileId;
                config.AudiobookMetadataProfileId = s.MetadataProfileId;
                config.AudiobookMonitorExisting = s.MonitorExisting;
                config.AudiobookMonitorFuture = s.MonitorFuture;
                if (s.Tags != null && s.Tags.Any())
                {
                    config.Tags = config.Tags ?? new System.Collections.Generic.HashSet<int>();
                    foreach (var t in s.Tags) config.Tags.Add(t);
                }
            }
        }

        private void ApplyEbookSettings(MonitoringConfig config, RootFolder root)
        {
            var s = root.GetEbookSettings();
            config.EbookRootFolderPath = root.Path;
            if (s != null)
            {
                config.EbookQualityProfileId = s.QualityProfileId;
                config.EbookMetadataProfileId = s.MetadataProfileId;
                config.EbookMonitorExisting = s.MonitorExisting;
                config.EbookMonitorFuture = s.MonitorFuture;
                if (s.Tags != null && s.Tags.Any())
                {
                    config.Tags = config.Tags ?? new System.Collections.Generic.HashSet<int>();
                    foreach (var t in s.Tags) config.Tags.Add(t);
                }
            }
        }

        private RootFolder ResolveRootFolder(string rootFolderPath)
        {
            if (string.IsNullOrWhiteSpace(rootFolderPath)) return null;
            var all = _rootFolderService.All();
            // Match by exact path (case-insensitive)
            return all.FirstOrDefault(r => string.Equals(r.Path, rootFolderPath, StringComparison.OrdinalIgnoreCase))
                   ?? all.FirstOrDefault(r => string.Equals(r.Name, rootFolderPath, StringComparison.OrdinalIgnoreCase));
        }

        private (string prefix, string id) SplitProviderId(string providerId)
        {
            var idx = providerId?.IndexOf(':') ?? -1;
            if (idx <= 0) return (providerId?.ToLowerInvariant() ?? "", "");
            return (providerId.Substring(0, idx).ToLowerInvariant(), providerId.Substring(idx + 1));
        }
    }
}
