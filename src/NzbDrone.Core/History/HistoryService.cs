using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.History
{
    public interface IHistoryService
    {
        PagingSpec<EntityHistory> Paged(PagingSpec<EntityHistory> pagingSpec);
        PagingSpec<EntityHistory> Paged(PagingSpec<EntityHistory> pagingSpec, BookMediaType? mediaType);
        EntityHistory MostRecentForBook(int bookId);
        EntityHistory MostRecentForDownloadId(string downloadId);
        EntityHistory Get(int historyId);
        List<EntityHistory> GetByAuthor(int authorId, EntityHistoryEventType? eventType);
        List<EntityHistory> GetByBook(int bookId, EntityHistoryEventType? eventType);
        List<EntityHistory> Find(string downloadId, EntityHistoryEventType eventType);
        List<EntityHistory> FindByDownloadId(string downloadId);
        List<EntityHistory> FindByDownloadIds(IEnumerable<string> downloadIds, EntityHistoryEventType eventType);
        string FindDownloadId(TrackImportedEvent trackedDownload);
        List<EntityHistory> Since(DateTime date, EntityHistoryEventType? eventType);
        void UpdateMany(IList<EntityHistory> items);
    }

    public class HistoryService : IHistoryService,
                                  IHandle<BookGrabbedEvent>,
                                  IHandle<BookImportIncompleteEvent>,
                                  IHandle<TrackImportedEvent>,
                                  IHandle<DownloadFailedEvent>,
                                  IHandle<BookFileAddedEvent>,
                                  IHandle<BookFileDeletedEvent>,
                                  IHandle<BookFileRenamedEvent>,
                                  IHandle<BookFileRetaggedEvent>,
                                  IHandle<BookFileConvertedEvent>,
                                  IHandle<BookFileConversionFailedEvent>,
                                  IHandle<AuthorDeletedEvent>,
                                  IHandle<DownloadIgnoredEvent>
    {
        private readonly IHistoryRepository _historyRepository;
        private readonly IDownloadHistoryRepository _downloadHistoryRepository;
        private readonly Logger _logger;
        private const string StatusMessagesKey = "StatusMessages";
        private const string SerializedStatusMessagesKey = "statusMessages";
        private const string PurgeReaddPendingKey = "PurgeReaddPending";
        private const string PurgeReaddOriginalAuthorIdKey = "PurgeReaddOriginalAuthorId";
        private const string PurgeReaddOriginalBookIdKey = "PurgeReaddOriginalBookId";
        private const string PurgeReaddOriginalEditionIdKey = "PurgeReaddOriginalEditionId";
        private const string PurgeReaddOriginalForeignEditionIdKey = "PurgeReaddOriginalForeignEditionId";

        public HistoryService(
            IHistoryRepository historyRepository,
            Logger logger,
            IDownloadHistoryRepository downloadHistoryRepository = null)
        {
            _historyRepository = historyRepository;
            _downloadHistoryRepository = downloadHistoryRepository;
            _logger = logger;
        }

        private static string NormalizeDownloadId(string downloadId)
        {
            return downloadId.IsNullOrWhiteSpace() ? null : downloadId.ToUpperInvariant();
        }

        public PagingSpec<EntityHistory> Paged(PagingSpec<EntityHistory> pagingSpec)
        {
            return _historyRepository.GetPaged(pagingSpec);
        }

        public PagingSpec<EntityHistory> Paged(PagingSpec<EntityHistory> pagingSpec, BookMediaType? mediaType)
        {
            return mediaType.HasValue
                ? _historyRepository.GetPaged(pagingSpec, mediaType.Value)
                : _historyRepository.GetPaged(pagingSpec);
        }

        public EntityHistory MostRecentForBook(int bookId)
        {
            return _historyRepository.MostRecentForBook(bookId);
        }

        public EntityHistory MostRecentForDownloadId(string downloadId)
        {
            downloadId = NormalizeDownloadId(downloadId);
            return downloadId == null ? null : _historyRepository.MostRecentForDownloadId(downloadId);
        }

        public EntityHistory Get(int historyId)
        {
            return _historyRepository.Get(historyId);
        }

        public List<EntityHistory> GetByAuthor(int authorId, EntityHistoryEventType? eventType)
        {
            return _historyRepository.GetByAuthor(authorId, eventType);
        }

        public List<EntityHistory> GetByBook(int bookId, EntityHistoryEventType? eventType)
        {
            return _historyRepository.GetByBook(bookId, eventType);
        }

        public List<EntityHistory> Find(string downloadId, EntityHistoryEventType eventType)
        {
            return FindByDownloadId(downloadId).Where(c => c.EventType == eventType).ToList();
        }

        public List<EntityHistory> FindByDownloadId(string downloadId)
        {
            downloadId = NormalizeDownloadId(downloadId);
            return downloadId == null ? new List<EntityHistory>() : _historyRepository.FindByDownloadId(downloadId);
        }

        public List<EntityHistory> FindByDownloadIds(IEnumerable<string> downloadIds, EntityHistoryEventType eventType)
        {
            var normalizedIds = (downloadIds ?? Enumerable.Empty<string>())
                .Select(NormalizeDownloadId)
                .Where(id => id != null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return normalizedIds.Count == 0
                ? new List<EntityHistory>()
                : _historyRepository.FindByDownloadIds(normalizedIds, eventType);
        }

        public string FindDownloadId(TrackImportedEvent trackedDownload)
        {
            var importedPath = trackedDownload?.ImportedBook?.Path ?? trackedDownload?.BookInfo?.Path ?? "<unknown>";
            _logger.Debug("Trying to find downloadId for {0} from history", importedPath);

            var authorId = ResolveImportedAuthorId(trackedDownload);
            var bookId = ResolveImportedBookId(trackedDownload);
            var quality = trackedDownload?.BookInfo?.Quality ?? trackedDownload?.ImportedBook?.Quality;

            if (authorId <= 0 || bookId <= 0 || quality == null)
            {
                return null;
            }

            var bookIds = new List<int> { bookId };
            var allHistory = _historyRepository.FindDownloadHistory(authorId, quality);

            //Find download related items for these episodes
            var booksHistory = allHistory.Where(h => bookIds.Contains(h.BookId)).ToList();

            var processedDownloadId = booksHistory
                .Where(c => c.EventType != EntityHistoryEventType.Grabbed && c.DownloadId != null)
                .Select(c => c.DownloadId);

            var stillDownloading = booksHistory.Where(c => c.EventType == EntityHistoryEventType.Grabbed && !processedDownloadId.Contains(c.DownloadId)).ToList();

            string downloadId = null;

            if (stillDownloading.Any())
            {
                var matchingHistory = stillDownloading.Where(c => c.BookId == bookId).ToList();

                if (matchingHistory.Count != 1)
                {
                    return null;
                }

                var newDownloadId = matchingHistory.Single().DownloadId;

                if (downloadId == null || downloadId == newDownloadId)
                {
                    downloadId = newDownloadId;
                }
                else
                {
                    return null;
                }
            }

            return downloadId;
        }

        public void Handle(BookGrabbedEvent message)
        {
            var historyToAdd = new List<EntityHistory>();
            foreach (var book in message.Book.GetBooksMatchingReleaseMediaType())
            {
                var grabQuality = ResolveGrabbedQuality(message.Book.ParsedBookInfo?.Quality, message.Book.Release);
                var editionId = 0;
                if (book.Editions != null)
                {
                    var monitoredEdition = book.Editions.FirstOrDefault(e => e.Monitored);
                    if (monitoredEdition != null)
                    {
                        editionId = monitoredEdition.Id;
                    }
                }

                var history = new EntityHistory
                {
                    EventType = EntityHistoryEventType.Grabbed,
                    Date = DateTime.UtcNow,
                    Quality = ResolveHistoryQuality(grabQuality, book, message.Book.Release),
                    SourceTitle = message.Book.Release.Title,
                    AuthorId = book.AuthorId,
                    BookId = book.Id,
                    EditionId = editionId,
                    DownloadId = NormalizeDownloadId(message.DownloadId)
                };

                history.Data.Add("Indexer", message.Book.Release.Indexer);
                history.Data.Add("NzbInfoUrl", message.Book.Release.InfoUrl);
                history.Data.Add("ReleaseGroup", message.Book.ParsedBookInfo.ReleaseGroup);
                history.Data.Add("Age", message.Book.Release.Age.ToString());
                history.Data.Add("AgeHours", message.Book.Release.AgeHours.ToString());
                history.Data.Add("AgeMinutes", message.Book.Release.AgeMinutes.ToString());
                history.Data.Add("PublishedDate", message.Book.Release.PublishDate.ToString("s") + "Z");
                history.Data.Add("DownloadClient", message.DownloadClient);
                history.Data.Add("DownloadClientName", message.DownloadClientName);
                history.Data.Add("Size", message.Book.Release.Size.ToString());
                history.Data.Add("DownloadUrl", message.Book.Release.DownloadUrl);
                history.Data.Add("Guid", message.Book.Release.Guid);
                history.Data.Add("Protocol", ((int)message.Book.Release.DownloadProtocol).ToString());
                var explicitInteractiveGrab = message.Book.ReleaseSource == ReleaseSourceType.InteractiveSearch;
                history.Data.Add("DownloadForced", (explicitInteractiveGrab || !message.Book.DownloadAllowed).ToString());
                history.Data.Add("CustomFormatScore", message.Book.CustomFormatScore.ToString());
                history.Data.Add("ReleaseSource", message.Book.ReleaseSource.ToString());
                history.Data.Add("IndexerFlags", message.Book.Release.IndexerFlags.ToString());

                if (!message.Book.ParsedBookInfo.ReleaseHash.IsNullOrWhiteSpace())
                {
                    history.Data.Add("ReleaseHash", message.Book.ParsedBookInfo.ReleaseHash);
                }

                if (message.Book.Release is TorrentInfo torrentRelease)
                {
                    history.Data.Add("TorrentInfoHash", torrentRelease.InfoHash);

                    // Store narrator information from MAM/indexer for later use in import pipeline
                    if (!string.IsNullOrWhiteSpace(torrentRelease.Narrator))
                    {
                        history.Data.Add("Narrator", torrentRelease.Narrator);
                    }

                    // Store additional TorrentInfo metadata for enhanced tracking
                    if (!string.IsNullOrWhiteSpace(torrentRelease.Duration))
                    {
                        history.Data.Add("Duration", torrentRelease.Duration);
                    }

                    if (!string.IsNullOrWhiteSpace(torrentRelease.FileType))
                    {
                        history.Data.Add("FileType", torrentRelease.FileType);
                    }

                    history.Data.Add("IsGraphicAudio", torrentRelease.IsGraphicAudio.ToString());

                    if (torrentRelease.Seeders.HasValue)
                    {
                        history.Data.Add("Seeders", torrentRelease.Seeders.Value.ToString());
                    }

                    if (torrentRelease.Peers.HasValue)
                    {
                        history.Data.Add("Peers", torrentRelease.Peers.Value.ToString());
                    }
                }

                historyToAdd.Add(history);
            }

            if (historyToAdd.Any())
            {
                _historyRepository.InsertMany(historyToAdd);
            }
        }

        private static QualityModel ResolveGrabbedQuality(QualityModel quality, ReleaseInfo release)
        {
            if (IsKnownQuality(quality))
            {
                return quality;
            }

            if (release is TorrentInfo torrentRelease && !torrentRelease.FileType.IsNullOrWhiteSpace())
            {
                var parsedQuality = QualityParser.ParseQualityFromFileType(
                    torrentRelease.FileType,
                    release.Title ?? string.Empty,
                    (int)release.IndexerFlags,
                    release.Indexer);

                if (IsKnownQuality(parsedQuality))
                {
                    return parsedQuality;
                }
            }

            return quality;
        }

        public void Handle(BookImportIncompleteEvent message)
        {
            if (message.TrackedDownload.RemoteBook == null)
            {
                return;
            }

            var statusMessages = message.TrackedDownload.StatusMessages.ToJson();
            var downloadId = NormalizeDownloadId(message.TrackedDownload.DownloadItem.DownloadId);

            foreach (var book in message.TrackedDownload.RemoteBook.GetBooksMatchingReleaseMediaType())
            {
                var editionId = 0;
                if (book.Editions != null)
                {
                    var monitoredEdition = book.Editions.FirstOrDefault(e => e.Monitored);
                    if (monitoredEdition != null)
                    {
                        editionId = monitoredEdition.Id;
                    }
                }

                var history = new EntityHistory
                {
                    EventType = EntityHistoryEventType.BookImportIncomplete,
                    Date = DateTime.UtcNow,
                    Quality = ResolveImportIncompleteQuality(message, book, downloadId),
                    SourceTitle = message.TrackedDownload.DownloadItem.Title,
                    AuthorId = book.AuthorId,
                    BookId = book.Id,
                    EditionId = editionId,
                    DownloadId = downloadId
                };

                history.Data.Add(StatusMessagesKey, statusMessages);
                history.Data.Add("ReleaseGroup", message.TrackedDownload?.RemoteBook?.ParsedBookInfo?.ReleaseGroup);
                history.Data.Add("IndexerFlags", message.TrackedDownload?.RemoteBook?.Release?.IndexerFlags.ToString());

                if (IsDuplicateImportIncomplete(history, statusMessages))
                {
                    _logger.Debug("Skipping duplicate import incomplete history for download {0}, book {1}", downloadId, book.Id);
                    continue;
                }

                _historyRepository.Insert(history);
            }
        }

        private QualityModel ResolveImportIncompleteQuality(BookImportIncompleteEvent message, Book book, string downloadId)
        {
            var parsedQuality = message.TrackedDownload.RemoteBook.ParsedBookInfo?.Quality;
            if (IsKnownQuality(parsedQuality))
            {
                return parsedQuality;
            }

            if (!downloadId.IsNullOrWhiteSpace())
            {
                var grabbedQuality = _historyRepository.FindByDownloadId(downloadId)
                    .Where(h => h.EventType == EntityHistoryEventType.Grabbed)
                    .Where(h => book == null || h.BookId == book.Id || h.AuthorId == book.AuthorId)
                    .OrderByDescending(h => h.Date)
                    .Select(h => h.Quality)
                    .FirstOrDefault(IsKnownQuality);

                if (grabbedQuality != null)
                {
                    return grabbedQuality;
                }
            }

            return ResolveHistoryQuality(parsedQuality, book, message.TrackedDownload.RemoteBook?.Release);
        }

        private static bool IsKnownQuality(QualityModel quality)
        {
            return quality?.Quality != null &&
                   quality.Quality != Quality.Unknown &&
                   quality.Quality != Quality.UnknownAudio;
        }

        private static QualityModel ResolveHistoryQuality(QualityModel quality, Book book, ReleaseInfo release = null)
        {
            if (IsKnownQuality(quality))
            {
                return quality;
            }

            var detectedMediaType = QualityMediaTypeHelper.DetectMediaType(quality?.Quality, release);
            var mediaType = detectedMediaType ?? book?.MediaType;
            var revision = quality?.Revision;

            if (mediaType == BookMediaType.Audiobook)
            {
                return new QualityModel(Quality.UnknownAudio, revision);
            }

            return quality ?? new QualityModel();
        }

        private bool IsDuplicateImportIncomplete(EntityHistory history, string statusMessages)
        {
            if (history.DownloadId.IsNullOrWhiteSpace())
            {
                return false;
            }

            var latestMatchingHistory = FindByDownloadId(history.DownloadId)
                .Where(existing =>
                    existing.BookId == history.BookId &&
                    existing.EditionId == history.EditionId)
                .OrderByDescending(existing => existing.Date)
                .FirstOrDefault();

            return latestMatchingHistory?.EventType == EntityHistoryEventType.BookImportIncomplete &&
                   string.Equals(latestMatchingHistory.SourceTitle, history.SourceTitle, StringComparison.Ordinal) &&
                   string.Equals(GetStatusMessages(latestMatchingHistory), statusMessages, StringComparison.Ordinal);
        }

        private static string GetStatusMessages(EntityHistory history)
        {
            return history.Data.GetValueOrDefault(SerializedStatusMessagesKey) ??
                   history.Data.GetValueOrDefault(StatusMessagesKey);
        }

        public void Handle(TrackImportedEvent message)
        {
            if (!message.NewDownload)
            {
                return;
            }

            var downloadId = message.DownloadId;

            if (downloadId.IsNullOrWhiteSpace())
            {
                downloadId = FindDownloadId(message);
            }

            var history = new EntityHistory
            {
                EventType = EntityHistoryEventType.BookFileImported,
                Date = DateTime.UtcNow,
                Quality = message.BookInfo?.Quality ?? message.ImportedBook?.Quality ?? new QualityModel(),
                SourceTitle = message.ImportedBook?.SceneName
                    ?? Path.GetFileNameWithoutExtension(message.BookInfo?.Path)
                    ?? Path.GetFileNameWithoutExtension(message.ImportedBook?.Path),
                AuthorId = ResolveImportedAuthorId(message),
                BookId = ResolveImportedBookId(message),
                EditionId = message.ImportedBook?.EditionId ?? 0,
                DownloadId = NormalizeDownloadId(downloadId)
            };

            history.Data.Add("FileId", (message.ImportedBook?.Id ?? 0).ToString());
            history.Data.Add("DroppedPath", message.BookInfo?.Path);
            history.Data.Add("ImportedPath", message.ImportedBook?.Path);
            history.Data.Add("DownloadClient", message.DownloadClientInfo?.Type);
            history.Data.Add("DownloadClientName", message.DownloadClientInfo?.Name);
            history.Data.Add("ReleaseGroup", message.BookInfo?.ReleaseGroup);
            history.Data.Add("CustomFormatScore", FindGrabbedCustomFormatScore(downloadId, history.BookId, history.AuthorId));
            history.Data.Add("Size", (message.BookInfo?.Size ?? message.ImportedBook?.Size ?? 0).ToString());
            history.Data.Add("IndexerFlags", message.BookInfo?.IndexerFlags.ToString());

            _historyRepository.Insert(history);
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

        private string FindGrabbedCustomFormatScore(string downloadId, int bookId, int authorId)
        {
            if (downloadId.IsNullOrWhiteSpace())
            {
                return null;
            }

            return _historyRepository.FindByDownloadId(NormalizeDownloadId(downloadId))
                .Where(h => h.EventType == EntityHistoryEventType.Grabbed)
                .Where(h => h.BookId == bookId || h.AuthorId == authorId)
                .OrderByDescending(h => h.Date)
                .Select(h => h.Data.GetValueOrDefault("CustomFormatScore") ?? h.Data.GetValueOrDefault("customFormatScore"))
                .FirstOrDefault(score => score.IsNotNullOrWhiteSpace());
        }

        public void Handle(BookFileConvertedEvent message)
        {
            var history = new EntityHistory
            {
                EventType = EntityHistoryEventType.BookFileConverted,
                Date = DateTime.UtcNow,
                Quality = message.TargetQuality ?? new QualityModel(),
                SourceTitle = message.SourceTitle,
                AuthorId = ResolveConversionAuthorId(message.Author, message.Book, message.Edition),
                BookId = ResolveConversionBookId(message.Book, message.Edition),
                EditionId = message.Edition?.Id ?? 0,
                DownloadId = NormalizeDownloadId(message.DownloadId)
            };

            AddConversionData(history, message.SourcePaths, message.SourceQuality, message.TargetQuality, message.ConvertedPath, message.ImportedPath, message.OutputSize, message.DownloadClientInfo, message.Message, message.TagMode, message.TagManifestJson);

            _historyRepository.Insert(history);
        }

        public void Handle(BookFileConversionFailedEvent message)
        {
            var history = new EntityHistory
            {
                EventType = EntityHistoryEventType.BookFileConversionFailed,
                Date = DateTime.UtcNow,
                Quality = message.TargetQuality ?? new QualityModel(),
                SourceTitle = message.SourceTitle,
                AuthorId = ResolveConversionAuthorId(message.Author, message.Book, message.Edition),
                BookId = ResolveConversionBookId(message.Book, message.Edition),
                EditionId = message.Edition?.Id ?? 0,
                DownloadId = NormalizeDownloadId(message.DownloadId)
            };

            AddConversionData(history, message.SourcePaths, message.SourceQuality, message.TargetQuality, message.ConvertedPath, null, null, message.DownloadClientInfo, message.Message, null, null);

            _historyRepository.Insert(history);
        }

        private static int ResolveConversionAuthorId(Author author, Book book, Edition edition)
        {
            return author?.Id
                   ?? book?.AuthorId
                   ?? edition?.Book?.AuthorId
                   ?? 0;
        }

        private static int ResolveConversionBookId(Book book, Edition edition)
        {
            return book?.Id
                   ?? edition?.BookId
                   ?? edition?.Book?.Id
                   ?? 0;
        }

        private static void AddConversionData(EntityHistory history, List<string> sourcePaths, QualityModel sourceQuality, QualityModel targetQuality, string convertedPath, string importedPath, long? outputSize, DownloadClientItemClientInfo downloadClientInfo, string message, string tagMode, string tagManifestJson)
        {
            var firstSourcePath = sourcePaths?.FirstOrDefault();

            history.Data.Add("SourcePath", firstSourcePath);
            history.Data.Add("SourcePaths", (sourcePaths ?? new List<string>()).ToJson());
            history.Data.Add("SourceFileCount", (sourcePaths?.Count ?? 0).ToString());
            history.Data.Add("SourceQuality", sourceQuality?.Quality?.Name);
            history.Data.Add("TargetQuality", targetQuality?.Quality?.Name);
            history.Data.Add("ConvertedPath", convertedPath);
            history.Data.Add("ImportedPath", importedPath);
            history.Data.Add("OutputSize", outputSize?.ToString() ?? string.Empty);
            history.Data.Add("DownloadClient", downloadClientInfo?.Type);
            history.Data.Add("DownloadClientName", downloadClientInfo?.Name);
            history.Data.Add("Message", message);
            history.Data.Add("TagMode", tagMode);
            history.Data.Add("TagManifestJson", tagManifestJson);
        }

        public void Handle(DownloadFailedEvent message)
        {
            var historyToAdd = new List<EntityHistory>();
            foreach (var bookId in message.BookIds)
            {
                var editionId = 0;
                if (message.TrackedDownload?.RemoteBook?.Books != null)
                {
                    var book = message.TrackedDownload.RemoteBook.Books.FirstOrDefault(b => b.Id == bookId);
                    if (book?.Editions != null)
                    {
                        var monitoredEdition = book.Editions.FirstOrDefault(e => e.Monitored);
                        if (monitoredEdition != null)
                        {
                            editionId = monitoredEdition.Id;
                        }
                    }
                }

                var history = new EntityHistory
                {
                    EventType = EntityHistoryEventType.DownloadFailed,
                    Date = DateTime.UtcNow,
                    Quality = message.Quality,
                    SourceTitle = message.SourceTitle,
                    AuthorId = message.AuthorId,
                    BookId = bookId,
                    EditionId = editionId,
                    DownloadId = NormalizeDownloadId(message.DownloadId)
                };

                history.Data.Add("DownloadClient", message.DownloadClient);
                history.Data.Add("DownloadClientName", message.TrackedDownload?.DownloadItem.DownloadClientInfo.Name);
                history.Data.Add("Message", message.Message);
                history.Data.Add("ReleaseGroup", message.TrackedDownload?.RemoteBook?.ParsedBookInfo?.ReleaseGroup ?? message.Data.GetValueOrDefault(EntityHistory.RELEASE_GROUP));
                history.Data.Add("Size", message.TrackedDownload?.DownloadItem.TotalSize.ToString() ?? message.Data.GetValueOrDefault(EntityHistory.SIZE));
                history.Data.Add("Indexer", message.TrackedDownload?.RemoteBook?.Release?.Indexer ?? message.Data.GetValueOrDefault(EntityHistory.INDEXER));

                historyToAdd.Add(history);
            }

            if (historyToAdd.Any())
            {
                _historyRepository.InsertMany(historyToAdd);
            }
        }

        public void Handle(BookFileDeletedEvent message)
        {
            if (message.Reason == DeleteMediaFileReason.NoLinkedEpisodes)
            {
                _logger.Debug("Removing book file from DB as part of cleanup routine, not creating history event.");
                return;
            }
            else if (message.Reason == DeleteMediaFileReason.ManualOverride)
            {
                _logger.Debug("Removing book file from DB as part of manual override of existing file, not creating history event.");
                return;
            }

            // Get the author ID from the book if not available in the file
            var authorId = 0;
            if (message.BookFile.Author != null)
            {
                authorId = message.BookFile.Author.Id;
            }
            else if (message.BookFile.Edition?.Book?.AuthorId > 0)
            {
                authorId = message.BookFile.Edition.Book.AuthorId;
            }
            else
            {
                // If we can't determine the author, we can't create a proper history record
                _logger.Warn("Unable to determine author ID for deleted book file: {0}", message.BookFile.Path);
                return;
            }

            var history = new EntityHistory
            {
                EventType = EntityHistoryEventType.BookFileDeleted,
                Date = DateTime.UtcNow,
                Quality = message.BookFile.Quality,
                SourceTitle = message.BookFile.Path,
                AuthorId = authorId,
                BookId = message.BookFile.Edition?.BookId ?? 0,
                EditionId = message.BookFile.EditionId
            };

            history.Data.Add("Reason", message.Reason.ToString());
            history.Data.Add("ReleaseGroup", message.BookFile.ReleaseGroup);
            history.Data.Add("IndexerFlags", message.BookFile.IndexerFlags.ToString());

            _historyRepository.Insert(history);
        }

        public void Handle(BookFileRenamedEvent message)
        {
            var sourcePath = message.OriginalPath;
            var path = message.BookFile.Path;

            var history = new EntityHistory
            {
                EventType = EntityHistoryEventType.BookFileRenamed,
                Date = DateTime.UtcNow,
                Quality = message.BookFile.Quality,
                SourceTitle = message.OriginalPath,
                AuthorId = message.BookFile.Author.Id,
                BookId = message.BookFile.Edition.BookId,
                EditionId = message.BookFile.EditionId
            };

            history.Data.Add("SourcePath", sourcePath);
            history.Data.Add("Path", path);
            history.Data.Add("ReleaseGroup", message.BookFile.ReleaseGroup);
            history.Data.Add("Size", message.BookFile.Size.ToString());
            history.Data.Add("IndexerFlags", message.BookFile.IndexerFlags.ToString());

            _historyRepository.Insert(history);
        }

        public void Handle(BookFileRetaggedEvent message)
        {
            var path = message.BookFile.Path;

            var history = new EntityHistory
            {
                EventType = EntityHistoryEventType.BookFileRetagged,
                Date = DateTime.UtcNow,
                Quality = message.BookFile.Quality,
                SourceTitle = path,
                AuthorId = message.BookFile.Author.Id,
                BookId = message.BookFile.Edition.BookId,
                EditionId = message.BookFile.EditionId
            };

            history.Data.Add("TagsScrubbed", message.Scrubbed.ToString());
            history.Data.Add("Diff", message.Diff.Select(x => new
            {
                Field = x.Key,
                OldValue = x.Value.Item1,
                NewValue = x.Value.Item2
            }).ToJson());

            _historyRepository.Insert(history);
        }

        public void Handle(AuthorDeletedEvent message)
        {
            if (message.PreserveRetainedFileHistory)
            {
                PreserveRetainedFileHistory(message);
                return;
            }

            _historyRepository.DeleteForAuthor(message.Author.Id);
        }

        private void PreserveRetainedFileHistory(AuthorDeletedEvent message)
        {
            if (_downloadHistoryRepository == null)
            {
                throw new InvalidOperationException("Download history repository is required for purge/re-add history preservation.");
            }

            var authorId = message.Author.Id;
            var retainedFileIds = message.RetainedBookFileIds.ToHashSet();
            var authorHistory = _historyRepository.GetByAuthor(authorId, null);
            var pendingImports = authorHistory
                .Where(history => history.EventType == EntityHistoryEventType.BookFileImported)
                .Where(history => TryGetPositiveDataInt(history.Data, "FileId", out var fileId) && retainedFileIds.Contains(fileId))
                .Where(history => !history.DownloadId.IsNullOrWhiteSpace())
                .ToList();
            var retainedDownloadIds = pendingImports
                .Select(history => NormalizeDownloadId(history.DownloadId))
                .Where(downloadId => downloadId != null)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var history in pendingImports)
            {
                history.Data ??= new Dictionary<string, string>();
                history.Data[PurgeReaddPendingKey] = bool.TrueString;
                history.Data[PurgeReaddOriginalAuthorIdKey] = history.AuthorId.ToString();
                history.Data[PurgeReaddOriginalBookIdKey] = history.BookId.ToString();
                history.Data[PurgeReaddOriginalEditionIdKey] = history.EditionId.ToString();
                if (TryGetPositiveDataInt(history.Data, "FileId", out var fileId) &&
                    message.RetainedBookFileEditionIds.TryGetValue(fileId, out var foreignEditionId))
                {
                    history.Data[PurgeReaddOriginalForeignEditionIdKey] = foreignEditionId;
                }

                // AuthorId=0 is an existing, API-safe unassigned shape and gives the relink
                // handler an indexed lookup for this exact retained BookFile anchor.
                history.AuthorId = 0;
                history.BookId = 0;
                history.EditionId = 0;
                history.Author = null;
                history.Book = null;
            }

            if (pendingImports.Count > 0)
            {
                _historyRepository.UpdateMany(pendingImports);
            }

            var entityHistoryToDelete = authorHistory
                .Where(history => !retainedDownloadIds.Contains(NormalizeDownloadId(history.DownloadId)))
                .Select(history => history.Id)
                .ToList();
            if (entityHistoryToDelete.Count > 0)
            {
                _historyRepository.DeleteMany(entityHistoryToDelete);
            }

            var downloadHistoryToDelete = _downloadHistoryRepository.GetByAuthorId(authorId)
                .Where(history => !retainedDownloadIds.Contains(NormalizeDownloadId(history.DownloadId)))
                .Select(history => history.Id)
                .ToList();
            if (downloadHistoryToDelete.Count > 0)
            {
                _downloadHistoryRepository.DeleteMany(downloadHistoryToDelete);
            }

            _logger.Info(
                "Preserved {0} download history chain(s) for {1} retained book file(s) while purging author {2}",
                retainedDownloadIds.Count,
                pendingImports.Count,
                authorId);
        }

        public void Handle(BookFileAddedEvent message)
        {
            var bookFile = message?.BookFile;
            var edition = bookFile?.Edition;
            var book = edition?.Book;
            var author = book?.Author ?? bookFile?.Author;

            if (_downloadHistoryRepository == null ||
                bookFile?.Id <= 0 ||
                edition?.Id <= 0 ||
                book?.Id <= 0 ||
                author?.Id <= 0)
            {
                return;
            }

            var pendingImports = _historyRepository.GetByAuthor(0, EntityHistoryEventType.BookFileImported)
                .Where(history => IsPendingPurgeReaddHistory(history.Data))
                .Where(history => TryGetPositiveDataInt(history.Data, "FileId", out var fileId) && fileId == bookFile.Id)
                .ToList();

            foreach (var pendingImport in pendingImports)
            {
                if (!TryGetPositiveDataInt(pendingImport.Data, PurgeReaddOriginalAuthorIdKey, out var originalAuthorId) ||
                    !TryGetPositiveDataInt(pendingImport.Data, PurgeReaddOriginalBookIdKey, out var originalBookId) ||
                    !TryGetPositiveDataInt(pendingImport.Data, PurgeReaddOriginalEditionIdKey, out var originalEditionId))
                {
                    continue;
                }

                var downloadId = NormalizeDownloadId(pendingImport.DownloadId);
                if (downloadId == null)
                {
                    continue;
                }

                if (TryGetDataValue(pendingImport.Data, PurgeReaddOriginalForeignEditionIdKey, out var originalForeignEditionId) &&
                    !string.IsNullOrWhiteSpace(edition.ForeignEditionId) &&
                    !string.Equals(originalForeignEditionId, edition.ForeignEditionId, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.Warn(
                        "Retained book file {0} rematched from edition {1} to {2}; following the successful file match",
                        bookFile.Id,
                        originalForeignEditionId,
                        edition.ForeignEditionId);
                }

                var entityHistory = _historyRepository.FindByDownloadId(downloadId);
                var entityUpdates = new List<EntityHistory>();
                foreach (var history in entityHistory)
                {
                    var isPendingImport = history.Id == pendingImport.Id;
                    var belongsToOriginalBook = history.AuthorId == originalAuthorId && history.BookId == originalBookId;
                    if (!isPendingImport && !belongsToOriginalBook)
                    {
                        continue;
                    }

                    history.AuthorId = author.Id;
                    history.BookId = book.Id;
                    history.Author = author;
                    history.Book = book;

                    if (isPendingImport || history.EditionId == originalEditionId)
                    {
                        history.EditionId = edition.Id;
                    }

                    if (isPendingImport)
                    {
                        RemoveDataKey(history.Data, PurgeReaddPendingKey);
                        RemoveDataKey(history.Data, PurgeReaddOriginalAuthorIdKey);
                        RemoveDataKey(history.Data, PurgeReaddOriginalBookIdKey);
                        RemoveDataKey(history.Data, PurgeReaddOriginalEditionIdKey);
                        RemoveDataKey(history.Data, PurgeReaddOriginalForeignEditionIdKey);
                    }

                    entityUpdates.Add(history);
                }

                if (entityUpdates.Count > 0)
                {
                    _historyRepository.UpdateMany(entityUpdates);
                }

                var downloadUpdates = _downloadHistoryRepository.FindByDownloadId(downloadId)
                    .Where(history => history.AuthorId == originalAuthorId &&
                                      (history.BookId == originalBookId || history.BookId <= 0))
                    .ToList();
                foreach (var history in downloadUpdates)
                {
                    history.AuthorId = author.Id;
                    history.BookId = book.Id;
                }

                if (downloadUpdates.Count > 0)
                {
                    _downloadHistoryRepository.UpdateMany(downloadUpdates);
                }

                _logger.Info(
                    "Re-keyed retained download history {0} after book file {1} rematched to author {2}, book {3}, edition {4}",
                    downloadId,
                    bookFile.Id,
                    author.Id,
                    book.Id,
                    edition.Id);
            }
        }

        private static bool IsPendingPurgeReaddHistory(IDictionary<string, string> data)
        {
            return TryGetDataValue(data, PurgeReaddPendingKey, out var value) &&
                   bool.TryParse(value, out var pending) &&
                   pending;
        }

        private static bool TryGetPositiveDataInt(IDictionary<string, string> data, string key, out int value)
        {
            value = 0;
            return TryGetDataValue(data, key, out var raw) &&
                   int.TryParse(raw, out value) &&
                   value > 0;
        }

        private static bool TryGetDataValue(IDictionary<string, string> data, string key, out string value)
        {
            value = null;
            if (data == null)
            {
                return false;
            }

            foreach (var pair in data)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    value = pair.Value;
                    return true;
                }
            }

            return false;
        }

        private static void RemoveDataKey(IDictionary<string, string> data, string key)
        {
            if (data == null)
            {
                return;
            }

            var storedKey = data.Keys.FirstOrDefault(candidate => string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase));
            if (storedKey != null)
            {
                data.Remove(storedKey);
            }
        }

        public void Handle(DownloadIgnoredEvent message)
        {
            var historyToAdd = new List<EntityHistory>();
            var bookIds = message.BookIds;

            // Unknown downloads can be ignored (Queue -> Remove -> Ignore) without a mapped author/book.
            // Ensure we still write a single history row so there's an audit trail in Activity -> History.
            if (bookIds == null || bookIds.Empty())
            {
                bookIds = new List<int> { 0 };
            }

            foreach (var bookId in bookIds)
            {
                var editionId = 0;
                if (bookId > 0 && message.TrackedDownload?.RemoteBook?.Books != null)
                {
                    var book = message.TrackedDownload.RemoteBook.Books.FirstOrDefault(b => b.Id == bookId);
                    if (book?.Editions != null)
                    {
                        var monitoredEdition = book.Editions.FirstOrDefault(e => e.Monitored);
                        if (monitoredEdition != null)
                        {
                            editionId = monitoredEdition.Id;
                        }
                    }
                }

                var history = new EntityHistory
                {
                    EventType = EntityHistoryEventType.DownloadIgnored,
                    Date = DateTime.UtcNow,
                    Quality = message.Quality,
                    SourceTitle = message.SourceTitle,
                    AuthorId = message.AuthorId,
                    BookId = bookId,
                    EditionId = editionId,
                    DownloadId = NormalizeDownloadId(message.DownloadId)
                };

                history.Data.Add("DownloadClient", message.DownloadClientInfo?.Name ?? string.Empty);
                history.Data.Add("Message", message.Message ?? string.Empty);
                history.Data.Add("ReleaseGroup", message.TrackedDownload?.RemoteBook?.ParsedBookInfo?.ReleaseGroup);
                var totalSize = message.TrackedDownload?.DownloadItem?.TotalSize;
                history.Data.Add("Size", totalSize.HasValue ? totalSize.Value.ToString() : string.Empty);
                history.Data.Add("Indexer", message.TrackedDownload?.RemoteBook?.Release?.Indexer);

                historyToAdd.Add(history);
            }

            if (historyToAdd.Any())
            {
                _historyRepository.InsertMany(historyToAdd);
            }
        }

        public List<EntityHistory> Since(DateTime date, EntityHistoryEventType? eventType)
        {
            return _historyRepository.Since(date, eventType);
        }

        public void UpdateMany(IList<EntityHistory> items)
        {
            _historyRepository.UpdateMany(items);
        }
    }
}
