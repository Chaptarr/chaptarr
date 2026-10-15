using System;
using System.IO;
using System.Collections.Generic;
using NzbDrone.Common;
using NzbDrone.Common.Serializer;
using NzbDrone.Common.Disk;
using NzbDrone.Core.MediaFiles;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.History;
using NzbDrone.Core.Books;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.Download.TrackedDownloads
{
    public interface ITrackedDownloadAlreadyImported
    {
        bool IsImported(TrackedDownload trackedDownload, List<EntityHistory> historyItems);
    }

    public class TrackedDownloadAlreadyImported : ITrackedDownloadAlreadyImported
    {
        private readonly Logger _logger;

        public TrackedDownloadAlreadyImported(Logger logger)
        {
            _logger = logger;
        }

        public bool IsImported(TrackedDownload trackedDownload, List<EntityHistory> historyItems)
        {
            _logger.Trace("Checking if all books for '{0}' have been imported", trackedDownload.DownloadItem.Title);

            if (historyItems.Empty())
            {
                _logger.Trace("No history for {0}", trackedDownload.DownloadItem.Title);
                return false;
            }

            if (trackedDownload.RemoteBook == null || trackedDownload.RemoteBook.Books == null)
            {
                _logger.Trace("Tracked book context is missing for {0}", trackedDownload.DownloadItem.Title);
                return false;
            }

            var trackedBooks = trackedDownload.RemoteBook.GetBooksMatchingReleaseMediaType()
                .Where(book => book != null && book.Id > 0)
                .ToList();

            if (trackedBooks.Empty())
            {
                _logger.Trace("No tracked books with ids are available for '{0}'", trackedDownload.DownloadItem.Title);
                return false;
            }

            if (trackedDownload.RemoteBook.AlternativeCopies)
            {
                var capturedDate = trackedDownload.RemoteBook.AlternativeCopyGrabDate;
                var targetIds = trackedBooks.Select(book => book.Id).ToHashSet();
                var media = trackedDownload.RemoteBook.GetMediaType();
                if (!media.HasValue || trackedBooks.Any(book => book.MediaType != media))
                {
                    return false;
                }
                var imported = historyItems.Where(row => row.EventType == EntityHistoryEventType.BookFileImported &&
                    targetIds.Contains(row.BookId) &&
                    trackedDownload.RemoteBook.TargetEditionIds.TryGetValue(row.BookId, out var editionId) && row.EditionId == editionId &&
                    capturedDate.HasValue && row.Date >= capturedDate.Value).ToList();
                var mediaPaths = trackedDownload.DownloadItem.FilePaths?.Where(path =>
                    !string.IsNullOrWhiteSpace(path) && QualityMediaTypeHelper.GetMediaTypeFromPath(path) == media).ToList();
                if (imported.Count == 0 || imported.Select(row => row.BookId).Distinct().Count() != 1 || mediaPaths == null || mediaPaths.Count == 0)
                {
                    return false;
                }

                var importedPaths = new HashSet<string>(PathEqualityComparer.Instance);
                foreach (var row in imported)
                {
                    if (row.GetDataValue("SourcePaths") is string sourcePaths)
                    {
                        if (!Json.TryDeserialize<List<string>>(sourcePaths, out var parsed) || parsed == null)
                        {
                            return false;
                        }

                        importedPaths.UnionWith(parsed.Where(path => !string.IsNullOrWhiteSpace(path)));
                    }

                    if (row.GetDataValue("DroppedPath") is string droppedPath)
                    {
                        importedPaths.Add(droppedPath);
                    }
                }
                return mediaPaths.All(importedPaths.Contains);
            }

            var allBooksImportedInHistory = trackedBooks.All(book =>
            {
                var lastHistoryItem = historyItems.FirstOrDefault(h => h.BookId == book.Id);

                if (lastHistoryItem == null)
                {
                    _logger.Trace($"No history for book: {book}");
                    return false;
                }

                _logger.Trace($"Last event for book: {book} is: {lastHistoryItem.EventType}");

                return lastHistoryItem.EventType == EntityHistoryEventType.BookFileImported;
            });

            _logger.Trace("All books for '{0}' have been imported: {1}", trackedDownload.DownloadItem.Title, allBooksImportedInHistory);

            return allBooksImportedInHistory;
        }
    }
}
