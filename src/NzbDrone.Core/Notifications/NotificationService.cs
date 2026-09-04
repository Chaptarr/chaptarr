using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Download;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Update.History.Events;

namespace NzbDrone.Core.Notifications
{
    public class NotificationService
        : IHandle<BookGrabbedEvent>,
          IHandle<BookImportedEvent>,
          IHandle<AuthorRenamedEvent>,
          IHandle<AuthorAddedEvent>,
          IHandle<BookAddedEvent>,
          IHandle<AuthorDeletedEvent>,
          IHandle<BookDeletedEvent>,
          IHandle<BookFileDeletedEvent>,
          IHandle<BookFileAddedEvent>,
          IHandle<HealthCheckFailedEvent>,
          IHandle<DownloadFailedEvent>,
          IHandle<BookImportIncompleteEvent>,
          IHandle<BookFileRetaggedEvent>,
          IHandleAsync<DeleteCompletedEvent>,
          IHandle<UpdateInstalledEvent>,
          IHandle<ImportSummaryEvent>
    {
        private readonly INotificationFactory _notificationFactory;
        private readonly INotificationStatusService _notificationStatusService;
        private readonly IEditionService _editionService;
        private readonly Logger _logger;

        public NotificationService(INotificationFactory notificationFactory, INotificationStatusService notificationStatusService, IEditionService editionService, Logger logger)
        {
            _notificationFactory = notificationFactory;
            _notificationStatusService = notificationStatusService;
            _editionService = editionService;
            _logger = logger;
        }

        private void EnsureEditionsLoaded(List<Book> books)
        {
            if (books == null || books.Count == 0)
            {
                return;
            }

            var booksNeedingEditions = books
                .Where(book => book != null && book.Id > 0 && book.Editions == null)
                .ToList();

            if (booksNeedingEditions.Count == 0)
            {
                return;
            }

            var bookIds = booksNeedingEditions.Select(book => book.Id).Distinct().ToList();
            var editions = _editionService.GetEditionsByBook(bookIds);

            var editionsByBook = editions?
                .GroupBy(edition => edition.BookId)
                .ToDictionary(group => group.Key, group => group.ToList())
                ?? new Dictionary<int, List<Edition>>();

            foreach (var book in booksNeedingEditions)
            {
                book.Editions = editionsByBook.TryGetValue(book.Id, out var bookEditions)
                    ? bookEditions
                    : new List<Edition>();
            }
        }

        private string GetMessage(Author author, List<Book> books, QualityModel quality)
        {
            var qualityString = quality.Quality.ToString();

            if (quality.Revision.Version > 1)
            {
                qualityString += " Proper";
            }

            var bookTitles = string.Join(" + ", books.Select(e => e.Title));

            return string.Format("{0} - {1} - [{2}]",
                                    author.Name,
                                    bookTitles,
                                    qualityString);
        }

        private string GetBookDownloadMessage(Author author, Book book, List<BookFile> tracks)
        {
            return string.Format("{0} - {1} ({2} Files Imported)",
                author.Name,
                book.Title,
                tracks.Count);
        }

        private string GetBookIncompleteImportMessage(string source)
        {
            return string.Format("Chaptarr failed to Import all files for {0}",
                source);
        }

        private string FormatMissing(object value)
        {
            var text = value?.ToString();
            return text.IsNullOrWhiteSpace() ? "<missing>" : text;
        }

        private string GetTrackRetagMessage(Author author, BookFile bookFile, Dictionary<string, Tuple<string, string>> diff)
        {
            return string.Format("{0}:\n{1}",
                                 bookFile.Path,
                                 string.Join("\n", diff.Select(x => $"{x.Key}: {FormatMissing(x.Value.Item1)} → {FormatMissing(x.Value.Item2)}")));
        }

        private bool ShouldHandleAuthor(ProviderDefinition definition, Author author)
        {
            if (definition.Tags.Empty())
            {
                _logger.Debug("No tags set for this notification.");
                return true;
            }

            if (definition.Tags.Intersect(author.Tags).Any())
            {
                _logger.Debug("Notification and author have one or more intersecting tags.");
                return true;
            }

            //TODO: this message could be more clear
            _logger.Debug("{0} does not have any intersecting tags with {1}. Notification will not be sent.", definition.Name, author.Name);
            return false;
        }

        private bool ShouldHandleHealthFailure(HealthCheck.HealthCheck healthCheck, bool includeWarnings)
        {
            if (healthCheck.Type == HealthCheckResult.Error)
            {
                return true;
            }

            if (healthCheck.Type == HealthCheckResult.Warning && includeWarnings)
            {
                return true;
            }

            return false;
        }

        public void Handle(BookGrabbedEvent message)
        {
            EnsureEditionsLoaded(message.Book.Books);

            var grabMessage = new GrabMessage
            {
                Message = GetMessage(message.Book.Author, message.Book.Books, message.Book.ParsedBookInfo.Quality),
                Author = message.Book.Author,
                Quality = message.Book.ParsedBookInfo.Quality,
                RemoteBook = message.Book,
                DownloadClientName = message.DownloadClientName,
                DownloadClientType = message.DownloadClient,
                DownloadId = message.DownloadId
            };

            foreach (var notification in _notificationFactory.OnGrabEnabled())
            {
                try
                {
                    if (!ShouldHandleAuthor(notification.Definition, message.Book.Author))
                    {
                        continue;
                    }

                    notification.OnGrab(grabMessage);
                    _notificationStatusService.RecordSuccess(notification.Definition.Id);
                }
                catch (Exception ex)
                {
                    _notificationStatusService.RecordFailure(notification.Definition.Id);
                    _logger.Error(ex, "Unable to send OnGrab notification to {0}", notification.Definition.Name);
                }
            }
        }

        public void Handle(BookImportedEvent message)
        {
            var isLibraryImport = !message.NewDownload;

            if (isLibraryImport && _notificationFactory.OnReleaseImportEnabled().All(n => !n.NotifyOnLibraryImports))
            {
                _logger.Info("Skipping OnReleaseImport for '{0}' (BookId={1}): import was not from a tracked download",
                    message.Book?.Title ?? "<unknown>",
                    message.Book?.Id ?? 0);
                return;
            }

            var author = message.Author ?? message.Book?.Author;
            var book = message.Book;

            if (author == null || book == null)
            {
                _logger.Warn("Skipping OnReleaseImport notifications due to missing book context (AuthorId={0}, BookId={1})",
                    message.Author?.Id ?? message.Book?.AuthorId ?? 0,
                    message.Book?.Id ?? 0);
                return;
            }

            var downloadMessage = new BookDownloadMessage
            {
                Message = GetBookDownloadMessage(author, book, message.ImportedBooks),
                Author = author,
                Book = book,
                DownloadClientInfo = message.DownloadClientInfo,
                DownloadId = message.DownloadId,
                BookFiles = message.ImportedBooks,
                OldFiles = message.OldFiles,
            };

            foreach (var notification in _notificationFactory.OnReleaseImportEnabled())
            {
                try
                {
                    if (isLibraryImport && !notification.NotifyOnLibraryImports)
                    {
                        continue;
                    }

                    if (ShouldHandleAuthor(notification.Definition, author))
                    {
                        if (downloadMessage.OldFiles.Empty() || ((NotificationDefinition)notification.Definition).OnUpgrade)
                        {
                            notification.OnReleaseImport(downloadMessage);
                            _notificationStatusService.RecordSuccess(notification.Definition.Id);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _notificationStatusService.RecordFailure(notification.Definition.Id);
                    _logger.Warn(ex, "Unable to send OnReleaseImport notification to: " + notification.Definition.Name);
                }
            }

            // Some integrations (e.g. Plex, AudioBookShelf) batch work in ProcessQueue
            // rather than doing it synchronously in the event handler.
            ProcessQueue();
        }

        public void Handle(AuthorRenamedEvent message)
        {
            foreach (var notification in _notificationFactory.OnRenameEnabled())
            {
                try
                {
                    if (ShouldHandleAuthor(notification.Definition, message.Author))
                    {
                        notification.OnRename(message.Author, message.RenamedFiles);
                        _notificationStatusService.RecordSuccess(notification.Definition.Id);
                    }
                }
                catch (Exception ex)
                {
                    _notificationStatusService.RecordFailure(notification.Definition.Id);
                    _logger.Warn(ex, "Unable to send OnRename notification to: " + notification.Definition.Name);
                }
            }

            ProcessQueue();
        }

        public void Handle(AuthorAddedEvent message)
        {
            foreach (var notification in _notificationFactory.OnAuthorAddedEnabled())
            {
                try
                {
                    if (ShouldHandleAuthor(notification.Definition, message.Author))
                    {
                        notification.OnAuthorAdded(message.Author);
                        _notificationStatusService.RecordSuccess(notification.Definition.Id);
                    }
                }
                catch (Exception ex)
                {
                    _notificationStatusService.RecordFailure(notification.Definition.Id);
                    _logger.Warn(ex, "Unable to send OnAuthorAdded notification to: " + notification.Definition.Name);
                }
            }
        }

        public void Handle(BookAddedEvent message)
        {
            if (message?.Book == null || message.Book.Author == null)
            {
                return;
            }

            foreach (var notification in _notificationFactory.OnBookAddedEnabled())
            {
                try
                {
                    if (ShouldHandleAuthor(notification.Definition, message.Book.Author))
                    {
                        notification.OnBookAdded(message.Book);
                        _notificationStatusService.RecordSuccess(notification.Definition.Id);
                    }
                }
                catch (Exception ex)
                {
                    _notificationStatusService.RecordFailure(notification.Definition.Id);
                    _logger.Warn(ex, "Unable to send OnBookAdded notification to: " + notification.Definition.Name);
                }
            }
        }

        public void Handle(AuthorDeletedEvent message)
        {
            var deleteMessage = new AuthorDeleteMessage(message.Author, message.DeleteFiles);

            foreach (var notification in _notificationFactory.OnAuthorDeleteEnabled())
            {
                try
                {
                    if (ShouldHandleAuthor(notification.Definition, deleteMessage.Author))
                    {
                        notification.OnAuthorDelete(deleteMessage);
                        _notificationStatusService.RecordSuccess(notification.Definition.Id);
                    }
                }
                catch (Exception ex)
                {
                    _notificationStatusService.RecordFailure(notification.Definition.Id);
                    _logger.Warn(ex, "Unable to send OnAuthorDelete notification to: " + notification.Definition.Name);
                }
            }
        }

        public void Handle(BookDeletedEvent message)
        {
            var deleteMessage = new BookDeleteMessage(message.Book, message.DeleteFiles);

            foreach (var notification in _notificationFactory.OnBookDeleteEnabled())
            {
                try
                {
                    if (ShouldHandleAuthor(notification.Definition, deleteMessage.Book.Author))
                    {
                        notification.OnBookDelete(deleteMessage);
                        _notificationStatusService.RecordSuccess(notification.Definition.Id);
                    }
                }
                catch (Exception ex)
                {
                    _notificationStatusService.RecordFailure(notification.Definition.Id);
                    _logger.Warn(ex, "Unable to send OnBookDelete notification to: " + notification.Definition.Name);
                }
            }
        }

        public void Handle(BookFileAddedEvent message)
        {
            var bookFile = message.BookFile;

            if (bookFile?.Path == null || bookFile.EditionId <= 0)
            {
                return;
            }

            Book book = null;

            try
            {
                book = bookFile.Edition?.Book;
            }
            catch
            {
            }

            if (book == null)
            {
                book = _editionService.GetEdition(bookFile.EditionId)?.Book;
            }

            if (book == null)
            {
                return;
            }

            foreach (var notification in _notificationFactory.OnReleaseImportEnabled())
            {
                if (!notification.NotifyOnLibraryImports)
                {
                    continue;
                }

                try
                {
                    notification.OnLibraryFileAdded(bookFile, book);
                    _notificationStatusService.RecordSuccess(notification.Definition.Id);
                }
                catch (Exception ex)
                {
                    _notificationStatusService.RecordFailure(notification.Definition.Id);
                    _logger.Warn(ex, "Unable to send library-file notification to: " + notification.Definition.Name);
                }
            }
        }

        public void Handle(BookFileDeletedEvent message)
        {
            var deleteMessage = new BookFileDeleteMessage();

            var book = new List<Book> { message.BookFile.Edition.Book };

            deleteMessage.Message = GetMessage(message.BookFile.Author, book, message.BookFile.Quality);
            deleteMessage.BookFile = message.BookFile;
            deleteMessage.Book = message.BookFile.Edition.Book;
            deleteMessage.Reason = message.Reason;

            foreach (var notification in _notificationFactory.OnBookFileDeleteEnabled())
            {
                try
                {
                    if (message.Reason != MediaFiles.DeleteMediaFileReason.Upgrade || ((NotificationDefinition)notification.Definition).OnBookFileDeleteForUpgrade)
                    {
                        if (ShouldHandleAuthor(notification.Definition, message.BookFile.Author))
                        {
                            notification.OnBookFileDelete(deleteMessage);
                            _notificationStatusService.RecordSuccess(notification.Definition.Id);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _notificationStatusService.RecordFailure(notification.Definition.Id);
                    _logger.Warn(ex, "Unable to send OnBookFileDelete notification to: " + notification.Definition.Name);
                }
            }

            ProcessQueue();
        }

        public void Handle(HealthCheckFailedEvent message)
        {
            // Don't send health check notifications during the start up grace period,
            // once that duration expires they they'll be retested and fired off if necessary.
            if (message.IsInStartupGracePeriod)
            {
                return;
            }

            foreach (var notification in _notificationFactory.OnHealthIssueEnabled())
            {
                try
                {
                    if (ShouldHandleHealthFailure(message.HealthCheck, ((NotificationDefinition)notification.Definition).IncludeHealthWarnings))
                    {
                        notification.OnHealthIssue(message.HealthCheck);
                        _notificationStatusService.RecordSuccess(notification.Definition.Id);
                    }
                }
                catch (Exception ex)
                {
                    _notificationStatusService.RecordFailure(notification.Definition.Id);
                    _logger.Warn(ex, "Unable to send OnHealthIssue notification to: " + notification.Definition.Name);
                }
            }
        }

        public void Handle(DownloadFailedEvent message)
        {
            var downloadFailedMessage = new DownloadFailedMessage
            {
                DownloadId = message.DownloadId,
                DownloadClient = message.DownloadClient,
                Quality = message.Quality,
                SourceTitle = message.SourceTitle,
                Message = message.Message
            };

            foreach (var notification in _notificationFactory.OnDownloadFailureEnabled())
            {
                try
                {
                    if (ShouldHandleAuthor(notification.Definition, message.TrackedDownload.RemoteBook.Author))
                    {
                        notification.OnDownloadFailure(downloadFailedMessage);
                        _notificationStatusService.RecordSuccess(notification.Definition.Id);
                    }
                }
                catch (Exception ex)
                {
                    _notificationStatusService.RecordFailure(notification.Definition.Id);
                    _logger.Warn(ex, "Unable to send OnDownloadFailure notification to: " + notification.Definition.Name);
                }
            }
        }

        public void Handle(BookImportIncompleteEvent message)
        {
            // TODO: Build out this message so that we can pass on what failed and what was successful
            var downloadMessage = new BookDownloadMessage
            {
                Message = GetBookIncompleteImportMessage(message.TrackedDownload.DownloadItem.Title)
            };

            foreach (var notification in _notificationFactory.OnImportFailureEnabled())
            {
                try
                {
                    if (ShouldHandleAuthor(notification.Definition, message.TrackedDownload.RemoteBook.Author))
                    {
                        notification.OnImportFailure(downloadMessage);
                        _notificationStatusService.RecordSuccess(notification.Definition.Id);
                    }
                }
                catch (Exception ex)
                {
                    _notificationStatusService.RecordFailure(notification.Definition.Id);
                    _logger.Warn(ex, "Unable to send OnImportFailure notification to: " + notification.Definition.Name);
                }
            }
        }

        public void Handle(BookFileRetaggedEvent message)
        {
            var retagMessage = new BookRetagMessage
            {
                Message = GetTrackRetagMessage(message.Author, message.BookFile, message.Diff),
                Author = message.Author,
                Book = message.BookFile.Edition.Book,
                BookFile = message.BookFile,
                Diff = message.Diff,
                Scrubbed = message.Scrubbed
            };

            foreach (var notification in _notificationFactory.OnBookRetagEnabled())
            {
                try
                {
                    if (ShouldHandleAuthor(notification.Definition, message.Author))
                    {
                        notification.OnBookRetag(retagMessage);
                        _notificationStatusService.RecordSuccess(notification.Definition.Id);
                    }
                }
                catch (Exception ex)
                {
                    _notificationStatusService.RecordFailure(notification.Definition.Id);
                    _logger.Warn(ex, "Unable to send OnBookRetag notification to: " + notification.Definition.Name);
                }
            }

            ProcessQueue();
        }

        public void Handle(UpdateInstalledEvent message)
        {
            var updateMessage = new ApplicationUpdateMessage();
            updateMessage.Message = $"Chaptarr updated from {message.PreviousVerison.ToString()} to {message.NewVersion.ToString()}";
            updateMessage.PreviousVersion = message.PreviousVerison;
            updateMessage.NewVersion = message.NewVersion;

            foreach (var notification in _notificationFactory.OnApplicationUpdateEnabled())
            {
                try
                {
                    notification.OnApplicationUpdate(updateMessage);
                    _notificationStatusService.RecordSuccess(notification.Definition.Id);
                }
                catch (Exception ex)
                {
                    _notificationStatusService.RecordFailure(notification.Definition.Id);
                    _logger.Warn(ex, "Unable to send OnApplicationUpdate notification to: " + notification.Definition.Name);
                }
            }
        }

        public void HandleAsync(DeleteCompletedEvent message)
        {
            ProcessQueue();
        }

        public void Handle(ImportSummaryEvent message)
        {
            // Process the queue to handle any batched notifications
            // This is triggered at the end of bulk imports
            _logger.Info("Import summary received - Folder: {0}, Authors added: {1}, Books matched: {2}, Books processed: {3}, Failed authors: {4}, Duration: {5}ms",
                message.FolderPath,
                message.TotalAuthorsAdded,
                message.TotalBooksMatched,
                message.TotalBooksProcessed,
                message.FailedAuthors?.Count ?? 0,
                message.ElapsedMilliseconds);

            _logger.Debug("Processing notification queues for bulk import completion");
            ProcessQueue();
        }

        private void ProcessQueue()
        {
            var blockedNotifications = _notificationStatusService.GetBlockedProviders().ToDictionary(v => v.ProviderId, v => v);

            foreach (var notification in _notificationFactory.GetAvailableProviders())
            {
                if (blockedNotifications.TryGetValue(notification.Definition.Id, out var notificationStatus))
                {
                    _logger.Debug("Temporarily ignoring notification {0} queue till {1} due to recent failures.", notification.Definition.Name, notificationStatus.DisabledTill?.ToLocalTime());
                    continue;
                }

                try
                {
                    var hadPendingQueue = notification.HasPendingQueue;
                    notification.ProcessQueue();
                    if (hadPendingQueue)
                    {
                        _notificationStatusService.RecordSuccess(notification.Definition.Id);
                    }
                }
                catch (Exception ex)
                {
                    _notificationStatusService.RecordFailure(notification.Definition.Id);
                    _logger.Warn(ex, "Unable to process notification queue for " + notification.Definition.Name);
                }
            }
        }
    }
}
