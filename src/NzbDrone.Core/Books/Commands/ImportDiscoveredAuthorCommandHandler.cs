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

                    var before = new
                    {
                        existing.AudiobookQualityProfileId,
                        existing.EbookQualityProfileId,
                        existing.AudiobookMonitored,
                        existing.AudiobookMonitorNewItems,
                        existing.EbookMonitored,
                        existing.EbookMonitorNewItems
                    };

                    var changedSettings = false;
                    if (augmentRoot.FolderType is FolderType.Audiobook or FolderType.Mixed)
                    {
                        changedSettings |= ApplyMediaSettings(existing, BookMediaType.Audiobook, a, augmentRoot.Path);
                    }

                    if (augmentRoot.FolderType is FolderType.Ebook or FolderType.Mixed)
                    {
                        changedSettings |= ApplyMediaSettings(existing, BookMediaType.Ebook, e, augmentRoot.Path);
                    }

                    var updated = changedSettings ? _authorService.UpdateAuthor(existing) : existing;

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

                    var after = new
                    {
                        updated.AudiobookQualityProfileId,
                        updated.EbookQualityProfileId,
                        updated.AudiobookMonitored,
                        updated.AudiobookMonitorNewItems,
                        updated.EbookMonitored,
                        updated.EbookMonitorNewItems
                    };
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
                config.AudiobookMonitorExistingMode = RootFolderSettingsResolver.ResolveInitialMonitorMode(s.MonitorExistingMode);
                config.AudiobookMonitored = s.Monitored;
                config.AudiobookMonitorNewItems = s.MonitorNewItems;
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
                config.EbookMonitorExistingMode = RootFolderSettingsResolver.ResolveInitialMonitorMode(s.MonitorExistingMode);
                config.EbookMonitored = s.Monitored;
                config.EbookMonitorNewItems = s.MonitorNewItems;
                if (s.Tags != null && s.Tags.Any())
                {
                    config.Tags = config.Tags ?? new System.Collections.Generic.HashSet<int>();
                    foreach (var t in s.Tags) config.Tags.Add(t);
                }
            }
        }

        private static bool ApplyMediaSettings(Author author, BookMediaType mediaType, MediaTypeSettings settings, string rootFolderPath)
        {
            if (author == null || settings == null)
            {
                return false;
            }

            var changed = false;
            if (mediaType == BookMediaType.Audiobook)
            {
                if (!author.AudiobookQualityProfileId.HasValue && settings.QualityProfileId.HasValue)
                {
                    author.AudiobookQualityProfileId = settings.QualityProfileId;
                    changed = true;
                }

                if (!author.AudiobookMetadataProfileId.HasValue && settings.MetadataProfileId.HasValue)
                {
                    author.AudiobookMetadataProfileId = settings.MetadataProfileId;
                    changed = true;
                }

                if (!author.AudiobookMonitored.HasValue && settings.Monitored.HasValue)
                {
                    author.AudiobookMonitored = settings.Monitored;
                    changed = true;
                }

                if (!author.AudiobookMonitorNewItems.HasValue && settings.MonitorNewItems.HasValue)
                {
                    author.AudiobookMonitorNewItems = settings.MonitorNewItems;
                    changed = true;
                }

                if (string.IsNullOrWhiteSpace(author.AudiobookRootFolderPath) && !string.IsNullOrWhiteSpace(rootFolderPath))
                {
                    author.AudiobookRootFolderPath = rootFolderPath;
                    changed = true;
                }
            }
            else
            {
                if (!author.EbookQualityProfileId.HasValue && settings.QualityProfileId.HasValue)
                {
                    author.EbookQualityProfileId = settings.QualityProfileId;
                    changed = true;
                }

                if (!author.EbookMetadataProfileId.HasValue && settings.MetadataProfileId.HasValue)
                {
                    author.EbookMetadataProfileId = settings.MetadataProfileId;
                    changed = true;
                }

                if (!author.EbookMonitored.HasValue && settings.Monitored.HasValue)
                {
                    author.EbookMonitored = settings.Monitored;
                    changed = true;
                }

                if (!author.EbookMonitorNewItems.HasValue && settings.MonitorNewItems.HasValue)
                {
                    author.EbookMonitorNewItems = settings.MonitorNewItems;
                    changed = true;
                }

                if (string.IsNullOrWhiteSpace(author.EbookRootFolderPath) && !string.IsNullOrWhiteSpace(rootFolderPath))
                {
                    author.EbookRootFolderPath = rootFolderPath;
                    changed = true;
                }
            }

            return changed;
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
