using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.ProgressMessaging;

namespace NzbDrone.Core.MediaFiles.Commands
{
    public class RefreshUnmappedFilesCommandHandler : IExecute<RefreshUnmappedFilesCommand>
    {
        private const int MaximumIntermediateProgressUpdates = 100;
        private const int MinimumProgressInterval = 25;

        private readonly IMediaFileService _mediaFileService;
        private readonly IMetadataTagService _metadataTagService;
        private readonly IDiskProvider _diskProvider;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;

        public RefreshUnmappedFilesCommandHandler(
            IMediaFileService mediaFileService,
            IMetadataTagService metadataTagService,
            IDiskProvider diskProvider,
            IEventAggregator eventAggregator,
            Logger logger)
        {
            _mediaFileService = mediaFileService;
            _metadataTagService = metadataTagService;
            _diskProvider = diskProvider;
            _eventAggregator = eventAggregator;
            _logger = logger;
        }

        public void Execute(RefreshUnmappedFilesCommand message)
        {
            var selection = message.UnmappedFiles ?? new UnmappedFilesSelection { Scope = "all" };
            var files = ResolveUnmappedFiles(selection, message.MediaType);

            if (!files.Any())
            {
                _logger.Debug("[UNMAPPED-REFRESH] No currently unmapped files matched scope '{0}' and mediaType '{1}'",
                    selection.Scope,
                    message.MediaType ?? "all");
                return;
            }

            var updated = new List<BookFile>();
            var unchanged = 0;
            var unavailable = 0;
            var failed = 0;
            var processed = 0;
            var progressInterval = GetProgressInterval(files.Count);

            PublishProgress("Refreshing file metadata", processed, files.Count, null);

            foreach (var file in files)
            {
                try
                {
                    if (file == null || string.IsNullOrWhiteSpace(file.Path))
                    {
                        continue;
                    }

                    IFileInfo fileInfo;
                    try
                    {
                        fileInfo = _diskProvider.GetFileInfo(file.Path);
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "[UNMAPPED-REFRESH] Could not stat '{0}'", file.Path);
                        failed++;
                        continue;
                    }

                    if (fileInfo == null || !fileInfo.Exists)
                    {
                        unavailable++;
                        _logger.Warn("[UNMAPPED-REFRESH][FILE-NOT-VISIBLE] Leaving unmapped row unchanged for '{0}'. A trustworthy root scan owns missing-file cleanup.", file.Path);
                        continue;
                    }

                    if (TryRefreshFile(file, fileInfo, out var changed))
                    {
                        if (changed)
                        {
                            updated.Add(file);
                        }
                        else
                        {
                            unchanged++;
                        }
                    }
                    else
                    {
                        failed++;
                    }
                }
                finally
                {
                    processed++;
                    if (ShouldPublishProgress(processed, files.Count, progressInterval))
                    {
                        PublishProgress("Refreshing file metadata", processed, files.Count, file?.Path);
                    }
                }
            }

            if (updated.Any())
            {
                _mediaFileService.Update(updated);
            }

            _logger.Info("[UNMAPPED-REFRESH] Refresh complete: selected={0}, updated={1}, unavailablePreserved={2}, unchanged={3}, failed={4}",
                files.Count,
                updated.Count,
                unavailable,
                unchanged,
                failed);

            PublishProgress("File metadata refresh complete", files.Count, files.Count, null);
        }

        private static int GetProgressInterval(int total)
        {
            if (total <= MinimumProgressInterval)
            {
                return 1;
            }

            return Math.Max(MinimumProgressInterval, (int)Math.Ceiling(total / (double)MaximumIntermediateProgressUpdates));
        }

        private static bool ShouldPublishProgress(int processed, int total, int interval)
        {
            return processed == 1 || processed == total || processed % interval == 0;
        }

        private void PublishProgress(string message, int current, int total, string path)
        {
            try
            {
                _eventAggregator.PublishEvent(new ImportStageProgressEvent(ImportStage.MatchingBooks, message, current, total)
                {
                    ProcessedBookFolders = current,
                    TotalBookFolders = total,
                    CurrentItemName = string.IsNullOrWhiteSpace(path) ? null : System.IO.Path.GetFileName(path),
                    CurrentItemType = "file",
                    CommandId = ProgressMessageContext.CommandModel?.Id
                });
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[UNMAPPED-REFRESH] Could not publish refresh progress");
            }
        }

        private bool TryRefreshFile(BookFile file, IFileInfo fileInfo, out bool changed)
        {
            var refreshed = UnmappedFileStoredEvidence.TryRefreshIfNeeded(
                file,
                fileInfo,
                _metadataTagService,
                _logger,
                "[UNMAPPED-REFRESH]",
                out var evidence);

            changed = refreshed && evidence.Mutated;
            return refreshed;
        }

        private List<BookFile> ResolveUnmappedFiles(UnmappedFilesSelection selection, string mediaType)
        {
            return UnmappedFileSelectionResolver.ResolveRows(
                _mediaFileService,
                selection,
                mediaType,
                _logger,
                "[UNMAPPED-REFRESH]");
        }
    }
}
