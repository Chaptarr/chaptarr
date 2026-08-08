using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Download.History
{
    public interface IDownloadHistoryService
    {
        bool DownloadAlreadyImported(string downloadId);
        DownloadHistory GetLatestDownloadHistoryItem(string downloadId);
        DownloadHistory GetLatestGrab(string downloadId);
        PagingSpec<DownloadHistory> CurrentlyIgnored(PagingSpec<DownloadHistory> pagingSpec);
        List<string> RemoveIgnored(int id);
        List<string> RemoveIgnored(List<int> ids);
    }

    public class DownloadHistoryService : IDownloadHistoryService,
                                          IHandle<BookGrabbedEvent>,
                                          IHandle<TrackImportedEvent>,
                                          IHandle<BookImportIncompleteEvent>,
                                          IHandle<DownloadCompletedEvent>,
                                          IHandle<DownloadFailedEvent>,
                                          IHandle<DownloadIgnoredEvent>,
                                          IHandle<AuthorDeletedEvent>
    {
        private readonly IDownloadHistoryRepository _repository;
        private readonly IHistoryService _historyService;
        private const string StatusMessagesKey = "StatusMessages";
        private const string SerializedStatusMessagesKey = "statusMessages";

        public DownloadHistoryService(IDownloadHistoryRepository repository, IHistoryService historyService)
        {
            _repository = repository;
            _historyService = historyService;
        }

        private static string NormalizeDownloadId(string downloadId)
        {
            return downloadId.IsNullOrWhiteSpace() ? null : downloadId.ToUpperInvariant();
        }

        public bool DownloadAlreadyImported(string downloadId)
        {
            var events = FindByDownloadId(downloadId);

            // Events are ordered by date descending, if a grabbed event comes before an imported event then it was never imported
            // or grabbed again after importing and should be reprocessed.
            foreach (var e in events)
            {
                if (e.EventType == DownloadHistoryEventType.DownloadGrabbed)
                {
                    return false;
                }

                if (e.EventType == DownloadHistoryEventType.DownloadImported)
                {
                    return true;
                }
            }

            return false;
        }

        public DownloadHistory GetLatestDownloadHistoryItem(string downloadId)
        {
            var events = FindByDownloadId(downloadId);

            // Events are ordered by date descending. We'll return the most recent expected event.
            foreach (var e in events)
            {
                if (e.EventType == DownloadHistoryEventType.DownloadIgnored)
                {
                    return e;
                }

                if (e.EventType == DownloadHistoryEventType.DownloadGrabbed)
                {
                    return e;
                }

                if (e.EventType == DownloadHistoryEventType.DownloadImported)
                {
                    return e;
                }

                if (e.EventType == DownloadHistoryEventType.DownloadFailed)
                {
                    return e;
                }

                if (e.EventType == DownloadHistoryEventType.DownloadImportIncomplete)
                {
                    return e;
                }
            }

            return null;
        }

        public DownloadHistory GetLatestGrab(string downloadId)
        {
            return FindByDownloadId(downloadId)
                .FirstOrDefault(d => d.EventType == DownloadHistoryEventType.DownloadGrabbed);
        }

        public PagingSpec<DownloadHistory> CurrentlyIgnored(PagingSpec<DownloadHistory> pagingSpec)
        {
            return _repository.CurrentlyIgnored(pagingSpec);
        }

        public List<string> RemoveIgnored(int id)
        {
            return RemoveIgnored(new List<int> { id });
        }

        public List<string> RemoveIgnored(List<int> ids)
        {
            if (ids == null || ids.Empty())
            {
                return new List<string>();
            }

            var ignored = _repository.FindByIds(ids.Distinct())
                .Where(h => h.EventType == DownloadHistoryEventType.DownloadIgnored)
                .ToList();

            var downloadIds = ignored
                .Select(h => NormalizeDownloadId(h.DownloadId))
                .Where(id => id.IsNotNullOrWhiteSpace())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            _repository.DeleteIgnoredByDownloadIds(downloadIds);

            return downloadIds;
        }

        private List<DownloadHistory> FindByDownloadId(string downloadId)
        {
            downloadId = NormalizeDownloadId(downloadId);
            return downloadId == null ? new List<DownloadHistory>() : _repository.FindByDownloadId(downloadId);
        }

        public void Handle(BookGrabbedEvent message)
        {
            // Don't store grabbed events for clients that don't download IDs
            if (message.DownloadId.IsNullOrWhiteSpace())
            {
                return;
            }

            var targetBooks = message.Book.GetBooksMatchingReleaseMediaType();

            var history = new DownloadHistory
            {
                EventType = DownloadHistoryEventType.DownloadGrabbed,
                AuthorId = message.Book.Author.Id,
                BookId = targetBooks.Count > 0 ? targetBooks[0].Id : 0,
                DownloadId = NormalizeDownloadId(message.DownloadId),
                SourceTitle = message.Book.Release.Title,
                Date = DateTime.UtcNow,
                Protocol = message.Book.Release.DownloadProtocol,
                IndexerId = message.Book.Release.IndexerId,
                DownloadClientId = message.DownloadClientId,
                Release = message.Book.Release
            };

            history.Data.Add("Indexer", message.Book.Release.Indexer);
            history.Data.Add("DownloadClient", message.DownloadClient);
            history.Data.Add("DownloadClientName", message.DownloadClientName);
            history.Data.Add("CustomFormatScore", message.Book.CustomFormatScore.ToString());

            _repository.Insert(history);
        }

        public void Handle(TrackImportedEvent message)
        {
            if (!message.NewDownload)
            {
                return;
            }

            var downloadId = message.DownloadId;

            // Try to find the downloadId if the user used manual import (from wanted: missing) or the
            // API to import and downloadId wasn't provided.
            if (downloadId.IsNullOrWhiteSpace())
            {
                downloadId = _historyService.FindDownloadId(message);
            }

            if (downloadId.IsNullOrWhiteSpace())
            {
                return;
            }

            var downloadClientInfo = message.DownloadClientInfo;
            var sourcePath = message.BookInfo?.Path ?? message.ImportedBook?.Path;

            var history = new DownloadHistory
            {
                EventType = DownloadHistoryEventType.FileImported,

                AuthorId = ResolveImportedAuthorId(message),
                BookId = ResolveImportedBookId(message),
                DownloadId = NormalizeDownloadId(downloadId),
                SourceTitle = sourcePath,
                Date = DateTime.UtcNow,
                Protocol = downloadClientInfo?.Protocol ?? DownloadProtocol.Unknown,
                DownloadClientId = downloadClientInfo?.Id ?? 0
            };

            history.Data.Add("DownloadClient", downloadClientInfo?.Type);
            history.Data.Add("DownloadClientName", downloadClientInfo?.Name);
            history.Data.Add("SourcePath", sourcePath);
            history.Data.Add("DestinationPath", message.ImportedBook?.Path);

            _repository.Insert(history);
        }

        public void Handle(BookImportIncompleteEvent message)
        {
            var clientInfo = message.TrackedDownload?.DownloadItem?.DownloadClientInfo;
            var statusMessages = message.TrackedDownload.StatusMessages.ToJson();

            var targetBook = message.TrackedDownload.RemoteBook?.GetBooksMatchingReleaseMediaType().FirstOrDefault();

            var history = new DownloadHistory
            {
                EventType = DownloadHistoryEventType.DownloadImportIncomplete,
                AuthorId = message.TrackedDownload.RemoteBook?.Author?.Id ?? 0,
                BookId = targetBook?.Id ?? 0,
                DownloadId = NormalizeDownloadId(message.TrackedDownload.DownloadItem.DownloadId),
                SourceTitle = message.TrackedDownload.DownloadItem.OutputPath.ToString(),
                Date = DateTime.UtcNow,
                Protocol = message.TrackedDownload.Protocol,
                DownloadClientId = message.TrackedDownload.DownloadClient
            };

            history.Data.Add("DownloadClient", clientInfo?.Type);
            history.Data.Add("DownloadClientName", clientInfo?.Name);
            history.Data.Add(StatusMessagesKey, statusMessages);

            if (IsDuplicateImportIncomplete(history, statusMessages))
            {
                return;
            }

            _repository.Insert(history);
        }

        private bool IsDuplicateImportIncomplete(DownloadHistory history, string statusMessages)
        {
            if (history.DownloadId.IsNullOrWhiteSpace())
            {
                return false;
            }

            var latestMatchingHistory = FindByDownloadId(history.DownloadId)
                .Where(existing => existing.BookId == history.BookId)
                .OrderByDescending(existing => existing.Date)
                .FirstOrDefault();

            return latestMatchingHistory?.EventType == DownloadHistoryEventType.DownloadImportIncomplete &&
                   string.Equals(latestMatchingHistory.SourceTitle, history.SourceTitle, StringComparison.Ordinal) &&
                   string.Equals(GetStatusMessages(latestMatchingHistory), statusMessages, StringComparison.Ordinal);
        }

        private static string GetStatusMessages(DownloadHistory history)
        {
            return history.Data.GetValueOrDefault(SerializedStatusMessagesKey) ??
                   history.Data.GetValueOrDefault(StatusMessagesKey);
        }

        public void Handle(DownloadCompletedEvent message)
        {
            var downloadItem = message.TrackedDownload.DownloadItem;
            var clientInfo = downloadItem?.DownloadClientInfo;

            // Try to infer a representative BookId from recent history (prefer imported entries)
            var historyItems = _historyService.FindByDownloadId(downloadItem.DownloadId) ?? new System.Collections.Generic.List<EntityHistory>();
            var inferredBookId = 0;
            try
            {
                var imported = historyItems.Where(x => x.EventType == EntityHistoryEventType.BookFileImported).ToList();
                if (imported.Any())
                {
                    inferredBookId = imported.GroupBy(x => x.BookId).OrderByDescending(g => g.Count()).First().Key;
                }
                else if (historyItems.Any())
                {
                    inferredBookId = historyItems.GroupBy(x => x.BookId).OrderByDescending(g => g.Count()).First().Key;
                }
            }
            catch { inferredBookId = 0; }

            var history = new DownloadHistory
            {
                EventType = DownloadHistoryEventType.DownloadImported,
                AuthorId = message.AuthorId,
                BookId = inferredBookId,
                DownloadId = NormalizeDownloadId(downloadItem.DownloadId),
                SourceTitle = downloadItem.Title,
                Date = DateTime.UtcNow,
                Protocol = message.TrackedDownload.Protocol,
                DownloadClientId = message.TrackedDownload.DownloadClient
            };

            history.Data.Add("DownloadClient", clientInfo?.Type);
            history.Data.Add("DownloadClientName", clientInfo?.Name);

            _repository.Insert(history);
        }

        private static int ResolveImportedAuthorId(TrackImportedEvent message)
        {
            return message?.BookInfo?.Author?.Id
                   ?? message?.ImportedBook?.Author?.Id
                   ?? message?.BookInfo?.Book?.AuthorId
                   ?? message?.ImportedBook?.Edition?.Book?.AuthorId
                   ?? 0;
        }

        private static int ResolveImportedBookId(TrackImportedEvent message)
        {
            return message?.BookInfo?.Book?.Id
                   ?? message?.ImportedBook?.Edition?.BookId
                   ?? message?.ImportedBook?.Edition?.Book?.Id
                   ?? 0;
        }

        public void Handle(DownloadFailedEvent message)
        {
            // Don't track failed download for an unknown download
            if (message.TrackedDownload == null)
            {
                return;
            }

            var clientInfo = message.TrackedDownload.DownloadItem?.DownloadClientInfo;

            var history = new DownloadHistory
            {
                EventType = DownloadHistoryEventType.DownloadFailed,
                AuthorId = message.AuthorId,
                BookId = message.BookIds != null && message.BookIds.Count > 0 ? message.BookIds[0] : 0,
                DownloadId = NormalizeDownloadId(message.DownloadId),
                SourceTitle = message.SourceTitle,
                Date = DateTime.UtcNow,
                Protocol = message.TrackedDownload.Protocol,
                DownloadClientId = message.TrackedDownload.DownloadClient
            };

            history.Data.Add("DownloadClient", clientInfo?.Type);
            history.Data.Add("DownloadClientName", clientInfo?.Name);

            _repository.Insert(history);
        }

        public void Handle(DownloadIgnoredEvent message)
        {
            var clientInfo = message.DownloadClientInfo;

            var history = new DownloadHistory
            {
                EventType = DownloadHistoryEventType.DownloadIgnored,
                AuthorId = message.AuthorId,
                BookId = message.BookIds != null && message.BookIds.Count > 0 ? message.BookIds[0] : 0,
                DownloadId = NormalizeDownloadId(message.DownloadId),
                SourceTitle = message.SourceTitle,
                Date = DateTime.UtcNow,
                Protocol = clientInfo?.Protocol ?? DownloadProtocol.Unknown,
                DownloadClientId = clientInfo?.Id ?? 0
            };

            history.Data.Add("DownloadClient", clientInfo?.Type ?? string.Empty);
            history.Data.Add("DownloadClientName", clientInfo?.Name ?? string.Empty);

            _repository.Insert(history);
        }

        public void Handle(AuthorDeletedEvent message)
        {
            if (message.PreserveRetainedFileHistory)
            {
                // HistoryService owns the selective, DownloadId-scoped deletion for this
                // one lifecycle so both history tables retain the same imported state.
                return;
            }

            _repository.DeleteByAuthorId(message.Author.Id);
        }
    }
}
