using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using NLog;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Notifications;

namespace NzbDrone.Core.Books
{
    public interface IPendingImportService
    {
        PendingImport QueueBookImport(Book book, PendingImportSettings settings);
        PendingImport QueueSeriesImport(Series series, List<Book> books, PendingImportSettings settings);
        void ProcessPendingImports();
        List<PendingImport> GetPending();
        void CancelPendingImport(int id);
    }

    public class PendingImportService : IPendingImportService
    {
        private readonly IPendingImportRepository _pendingImportRepository;
        private readonly IAuthorService _authorService;
        private readonly IBookService _bookService;
        private readonly IBookMonitoredService _bookMonitoredService;
        private readonly IProvideAuthorInfo _authorInfo;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;

        private const int MAX_RETRY_COUNT = 288; // 288 * 5 minutes = 24 hours
        private const int RETRY_DELAY_MINUTES = 5;

        public PendingImportService(
            IPendingImportRepository pendingImportRepository,
            IAuthorService authorService,
            IBookService bookService,
            IBookMonitoredService bookMonitoredService,
            IProvideAuthorInfo authorInfo,
            IManageCommandQueue commandQueueManager,
            IEventAggregator eventAggregator,
            Logger logger)
        {
            _pendingImportRepository = pendingImportRepository;
            _authorService = authorService;
            _bookService = bookService;
            _bookMonitoredService = bookMonitoredService;
            _authorInfo = authorInfo;
            _commandQueueManager = commandQueueManager;
            _eventAggregator = eventAggregator;
            _logger = logger;
        }

        public PendingImport QueueBookImport(Book book, PendingImportSettings settings)
        {
            var providerIds = new PendingImportProviderIds
            {
                HardcoverAuthorId = book.Author?.HardcoverAuthorId,
                GoodreadsAuthorId = book.Author?.GoodreadsAuthorId,
                OpenLibraryAuthorId = book.Author?.OpenLibraryAuthorId,
                GoogleBooksAuthorId = book.Author?.GoogleBooksAuthorId,
                HardcoverBookId = book.HardcoverBookId,
                GoodreadsBookId = book.GoodreadsWorkId ?? BookEditionIdentity.GetGoodreadsEditionProviderId(book, _logger, "PendingImportService.QueueBookImport"),
                OpenLibraryWorkId = book.OpenLibraryWorkId,
                GoogleBooksId = BookEditionIdentity.GetGoogleBooksEditionId(book, _logger, "PendingImportService.QueueBookImport"),
                AuthorName = book.Author?.Name,
                BookTitle = book.Title
            };

            var providerIdsJson = JsonConvert.SerializeObject(providerIds);

            // Check if already queued
            var existing = _pendingImportRepository.GetByProviderIds(providerIdsJson);
            if (existing != null)
            {
                _logger.Info("Book '{0}' by '{1}' already in pending import queue",
                    book.Title, book.Author?.Name);
                return existing;
            }

            var monitoringIds = new List<string>();
            if (!string.IsNullOrEmpty(book.HardcoverBookId))
                monitoringIds.Add(ProviderIdHelper.Canonicalize(book.HardcoverBookId, "hc"));
            if (!string.IsNullOrEmpty(book.GoodreadsWorkId))
                monitoringIds.Add(ProviderIdHelper.Canonicalize(book.GoodreadsWorkId, "gr"));

            var pendingImport = new PendingImport
            {
                ImportType = "book",
                ProviderIds = providerIdsJson,
                MediaType = book.MediaType.ToString().ToLower(),
                MonitoringType = "specific_book",
                MonitoringIds = JsonConvert.SerializeObject(monitoringIds),
                Settings = JsonConvert.SerializeObject(settings)
            };

            _pendingImportRepository.Insert(pendingImport);

            _logger.Info("Queued pending import for book '{0}' by '{1}'",
                book.Title, book.Author?.Name);

            // Send notification to UI
            _eventAggregator.PublishEvent(new PendingImportQueuedEvent
            {
                Message = $"Queued '{book.Title}' for import. Will retry every {RETRY_DELAY_MINUTES} minutes until available."
            });

            return pendingImport;
        }

        public PendingImport QueueSeriesImport(Series series, List<Book> books, PendingImportSettings settings)
        {
            // Group books by author for series import
            var authorGroups = books.Where(b => b.Author != null)
                .GroupBy(b => b.Author.Name)
                .ToList();

            // For now, queue the first author - we may need to handle multiple authors differently
            if (authorGroups.Any())
            {
                var firstGroup = authorGroups.First();
                var firstAuthor = firstGroup.First().Author;

                var providerIds = new PendingImportProviderIds
                {
                    HardcoverAuthorId = firstAuthor.HardcoverAuthorId,
                    GoodreadsAuthorId = firstAuthor.GoodreadsAuthorId,
                    OpenLibraryAuthorId = firstAuthor.OpenLibraryAuthorId,
                    GoogleBooksAuthorId = firstAuthor.GoogleBooksAuthorId,
                    HardcoverSeriesId = series.HardcoverSeriesId,
                    GoodreadsSeriesId = series.GoodreadsSeriesId,
                    AuthorName = firstAuthor.Name,
                    SeriesTitle = series.Title
                };

                var providerIdsJson = JsonConvert.SerializeObject(providerIds);

                // Collect all book IDs in the series for monitoring
                var monitoringIds = new List<string>();
                foreach (var book in firstGroup)
                {
                    if (!string.IsNullOrEmpty(book.HardcoverBookId))
                        monitoringIds.Add(ProviderIdHelper.Canonicalize(book.HardcoverBookId, "hc"));
                    if (!string.IsNullOrEmpty(book.GoodreadsWorkId))
                        monitoringIds.Add(ProviderIdHelper.Canonicalize(book.GoodreadsWorkId, "gr"));
                }

                // Series should create separate pending imports for each media type
                // For now, default to audiobook - later enhancement could create both
                var pendingImport = new PendingImport
                {
                    ImportType = "series",
                    ProviderIds = providerIdsJson,
                    MediaType = "audiobook", // Each import is single media type
                    MonitoringType = "series_books",
                    MonitoringIds = JsonConvert.SerializeObject(monitoringIds),
                    Settings = JsonConvert.SerializeObject(settings)
                };

                _pendingImportRepository.Insert(pendingImport);

                _logger.Info("Queued pending import for series '{0}'", series.Title);

                return pendingImport;
            }

            return null;
        }

        public void ProcessPendingImports()
        {
            var pendingImports = _pendingImportRepository.GetReadyForRetry();

            if (!pendingImports.Any())
            {
                return;
            }

            _logger.Info("Processing {0} pending imports", pendingImports.Count);

            foreach (var pendingImport in pendingImports)
            {
                try
                {
                    ProcessSingleImport(pendingImport);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Error processing pending import {0}", pendingImport.Id);
                    HandleImportFailure(pendingImport, ex.Message);
                }
            }
        }

        private void ProcessSingleImport(PendingImport pendingImport)
        {
            _pendingImportRepository.MarkAsProcessing(pendingImport.Id);

            var providerIds = JsonConvert.DeserializeObject<PendingImportProviderIds>(pendingImport.ProviderIds);
            var settings = JsonConvert.DeserializeObject<PendingImportSettings>(pendingImport.Settings);

            _logger.Info("Attempting to import {0}: {1}",
                pendingImport.ImportType,
                providerIds.AuthorName ?? providerIds.BookTitle ?? providerIds.SeriesTitle);

            // Try to find or import the author
            Author author = null;

            // First check if author already exists locally
            if (!string.IsNullOrEmpty(providerIds.HardcoverAuthorId))
            {
                author = _authorService.FindByProviderId("hc", providerIds.HardcoverAuthorId);
            }
            if (author == null && !string.IsNullOrEmpty(providerIds.GoodreadsAuthorId))
            {
                author = _authorService.FindByProviderId("gr", providerIds.GoodreadsAuthorId);
            }

            if (author == null)
            {
                // Try to import from the configured metadata server
                var authorIdToImport = providerIds.HardcoverAuthorId ??
                                      providerIds.GoodreadsAuthorId ??
                                      providerIds.OpenLibraryAuthorId;

                if (string.IsNullOrEmpty(authorIdToImport))
                {
                    HandleImportFailure(pendingImport, "No valid author provider ID available");
                    return;
                }

                try
                {
                    _logger.Info("Fetching author from metadata server: {0}", authorIdToImport);
                    var authorData = _authorInfo.GetAuthorInfo(authorIdToImport);

                    if (authorData == null)
                    {
                        // Server doesn't have the author yet, retry later
                        HandleImportRetry(pendingImport, "Author not yet available on server");
                        return;
                    }

                    // Import the author with the specified settings
                    author = authorData;
                    ApplyAuthorSettings(author, settings);

                    // Set monitoring to None initially - we'll handle specific monitoring after
                    author.AddOptions = new AddAuthorOptions
                    {
                        Monitor = MonitorTypes.None,
                        Monitored = true,
                        SearchForMissingBooks = settings.SearchForMissingBooks
                    };

                    author = _authorService.AddAuthor(author, true);

                    _logger.Info("Successfully imported author '{0}' (ID: {1})", author.Name, author.Id);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to import author from metadata server");
                    HandleImportRetry(pendingImport, $"Failed to fetch author: {ex.Message}");
                    return;
                }
            }

            // Now handle specific monitoring based on import type
            if (pendingImport.MonitoringType == "specific_book" && !string.IsNullOrEmpty(pendingImport.MonitoringIds))
            {
                var monitoringIds = JsonConvert.DeserializeObject<List<string>>(pendingImport.MonitoringIds);
                MonitorSpecificBooks(author, monitoringIds, pendingImport.MediaType);
            }
            else if (pendingImport.MonitoringType == "series_books" && !string.IsNullOrEmpty(pendingImport.MonitoringIds))
            {
                var monitoringIds = JsonConvert.DeserializeObject<List<string>>(pendingImport.MonitoringIds);
                MonitorSpecificBooks(author, monitoringIds, pendingImport.MediaType);
            }
            else if (pendingImport.MonitoringType == "all_books")
            {
                // Monitor all books for this author
                var books = _bookService.GetBooksByAuthor(author.Id);
                foreach (var book in books)
                {
                    // Only update books that match the specific media type requested
                    // Remove support for "both" - each import should be single media type
                    if (book.MediaType.ToString().ToLower() == pendingImport.MediaType)
                    {
                        if (pendingImport.MediaType == "audiobook" && book.MediaType == BookMediaType.Audiobook)
                        {
                            book.AudiobookMonitored = true;
                        }
                        else if (pendingImport.MediaType == "ebook" && book.MediaType == BookMediaType.Ebook)
                        {
                            book.EbookMonitored = true;
                        }
                        _bookService.UpdateBook(book);
                    }
                }
            }

            // Mark as completed
            _pendingImportRepository.MarkAsCompleted(pendingImport.Id, author.Id);

            // Send success notification
            var itemName = providerIds.BookTitle ?? providerIds.SeriesTitle ?? providerIds.AuthorName;
            _eventAggregator.PublishEvent(new PendingImportCompletedEvent
            {
                AuthorId = author.Id,
                AuthorName = author.Name,
                Message = $"Successfully added '{itemName}' to library"
            });

            _logger.Info("Completed pending import {0}: {1}", pendingImport.Id, itemName);
        }

        private void ApplyAuthorSettings(Author author, PendingImportSettings settings)
        {
            if (settings == null) return;

            author.AudiobookRootFolderPath = settings.AudiobookRootFolderPath;
            author.EbookRootFolderPath = settings.EbookRootFolderPath;
            author.AudiobookQualityProfileId = settings.AudiobookQualityProfileId ?? 0;
            author.EbookQualityProfileId = settings.EbookQualityProfileId ?? 0;
            author.AudiobookMetadataProfileId = settings.AudiobookMetadataProfileId ?? 0;
            author.EbookMetadataProfileId = settings.EbookMetadataProfileId ?? 0;
            author.AudiobookTags = settings.AudiobookTags ?? new HashSet<int>();
            author.EbookTags = settings.EbookTags ?? new HashSet<int>();
            author.Tags = author.AudiobookTags.Concat(author.EbookTags).ToHashSet();
        }

        private void MonitorSpecificBooks(Author author, List<string> monitoringIds, string mediaType)
        {
            var books = _bookService.GetBooksByAuthor(author.Id);

            foreach (var book in books)
            {
                // Check if this book should be monitored
                bool shouldMonitor = false;

                foreach (var monitoringId in monitoringIds)
                {
                    var parts = monitoringId.Split(':');
                    if (parts.Length == 2)
                    {
                        var provider = parts[0];
                        var id = parts[1];

                        if (provider == "hc" && book.HardcoverBookId == id)
                            shouldMonitor = true;
                        else if (provider == "gr" && ProviderIdHelper.StripPrefix(book.GoodreadsWorkId) == id)
                            shouldMonitor = true;
                        else if (provider == "ol" && book.OpenLibraryWorkId == id)
                            shouldMonitor = true;
                    }
                }

                // Only monitor if the book's MediaType matches the requested type
                if (shouldMonitor && book.MediaType.ToString().ToLower() == mediaType)
                {
                    if (mediaType == "audiobook" && book.MediaType == BookMediaType.Audiobook)
                    {
                        book.AudiobookMonitored = true;
                    }
                    else if (mediaType == "ebook" && book.MediaType == BookMediaType.Ebook)
                    {
                        book.EbookMonitored = true;
                    }
                    _bookService.UpdateBook(book);
                    _logger.Debug("Set book '{0}' to monitored for {1}", book.Title, mediaType);
                }
            }
        }

        private void HandleImportRetry(PendingImport pendingImport, string reason)
        {
            if (pendingImport.RetryCount >= MAX_RETRY_COUNT)
            {
                _pendingImportRepository.MarkAsFailed(pendingImport.Id,
                    $"Max retry count reached. Last error: {reason}");
                _logger.Warn("Pending import {0} failed after {1} retries",
                    pendingImport.Id, MAX_RETRY_COUNT);
                return;
            }

            var nextRetry = DateTime.UtcNow.AddMinutes(RETRY_DELAY_MINUTES);
            _pendingImportRepository.UpdateRetryInfo(pendingImport.Id, nextRetry, pendingImport.RetryCount + 1);

            _logger.Debug("Pending import {0} will retry at {1} (attempt {2}/{3}): {4}",
                pendingImport.Id, nextRetry, pendingImport.RetryCount + 1, MAX_RETRY_COUNT, reason);
        }

        private void HandleImportFailure(PendingImport pendingImport, string errorMessage)
        {
            _pendingImportRepository.MarkAsFailed(pendingImport.Id, errorMessage);
            _logger.Error("Pending import {0} failed: {1}", pendingImport.Id, errorMessage);
        }

        public List<PendingImport> GetPending()
        {
            return _pendingImportRepository.GetPendingImports();
        }

        public void CancelPendingImport(int id)
        {
            _pendingImportRepository.Delete(id);
        }
    }

    // Events for notifications
    public class PendingImportQueuedEvent : IEvent
    {
        public string Message { get; set; }
    }

    public class PendingImportCompletedEvent : IEvent
    {
        public int AuthorId { get; set; }
        public string AuthorName { get; set; }
        public string Message { get; set; }
    }
}
