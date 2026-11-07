using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Core.Localization;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Configuration.Events;
using NzbDrone.Core.ThingiProvider.Events;
using NzbDrone.Core.Notifications.Webhook;
using Newtonsoft.Json.Linq;

namespace NzbDrone.Core.Notifications.MqttGateway
{
    public sealed class CanonicalEventGatewayService : IHandleAsync<IEvent>, IDisposable
    {
        private const int ServiceQueueCapacity = 512;
        private const int DownloadDeduplicationCapacity = 4096;

        private readonly INotificationFactory _notificationFactory;
        private readonly IWebhookProxy _webhookProxy;
        private readonly IConfigFileProvider _configFileProvider;
        private readonly IAuthorService _authorService;
        private readonly IBookService _bookService;
        private readonly IBookRepository _bookRepository;
        private readonly ISeriesService _seriesService;
        private readonly ISeriesBookLinkService _seriesBookLinkService;
        private readonly IMediaFileRepository _mediaFileRepository;
        private readonly MqttGatewaySnapshotProvider _snapshotProvider;
        private readonly Logger _logger;
        private readonly NotificationText _notificationText;
        private readonly Channel<IEvent> _events;
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Dictionary<int, MqttGatewayConnectionWorker> _connections = new();
        private readonly List<(NotificationDefinition Definition, WebhookSettings Settings)> _canonicalWebhooks = new();
        private readonly Dictionary<int, Author> _authorsById = new();
        private readonly HashSet<int> _authorsWithBooks = new();
        private readonly HashSet<int> _authorsWithoutBooks = new();
        private readonly HashSet<string> _downloadedIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<string> _downloadedOrder = new();
        private readonly Task _processor;
        private bool _started;

        public CanonicalEventGatewayService(INotificationFactory notificationFactory,
                                  IWebhookProxy webhookProxy,
                                  IConfigFileProvider configFileProvider,
                                  IAuthorService authorService,
                                  IBookService bookService,
                                  IBookRepository bookRepository,
                                  ISeriesService seriesService,
                                  ISeriesBookLinkService seriesBookLinkService,
                                  IMediaFileRepository mediaFileRepository,
                                  MqttGatewaySnapshotProvider snapshotProvider,
                                  ILocalizationService localizationService,
                                  Logger logger)
        {
            _notificationFactory = notificationFactory;
            _webhookProxy = webhookProxy;
            _configFileProvider = configFileProvider;
            _authorService = authorService;
            _bookService = bookService;
            _bookRepository = bookRepository;
            _seriesService = seriesService;
            _seriesBookLinkService = seriesBookLinkService;
            _mediaFileRepository = mediaFileRepository;
            _snapshotProvider = snapshotProvider;
            _logger = logger;
            _notificationText = new NotificationText(localizationService);
            _events = Channel.CreateBounded<IEvent>(new BoundedChannelOptions(ServiceQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });
            _processor = Task.Run(ProcessEventsAsync);
        }

        public void HandleAsync(IEvent message)
        {
            if (!_events.Writer.TryWrite(message))
            {
                _logger.Warn("Canonical event gateway queue is full; skipped event {0}", message.GetType().Name);
            }
        }

        public void Dispose()
        {
            _events.Writer.TryComplete();
            _shutdown.Cancel();

            try
            {
                if (!_processor.Wait(TimeSpan.FromSeconds(8)))
                {
                    _logger.Warn("Canonical event gateway did not finish shutting down before the timeout");
                }
            }
            catch (AggregateException)
            {
            }

            if (_processor.IsCompleted)
            {
                _shutdown.Dispose();
            }
        }

        private async Task ProcessEventsAsync()
        {
            try
            {
                await foreach (var message in _events.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
                {
                    try
                    {
                        await HandleEventAsync(message).ConfigureAwait(false);
                    }
                    catch
                    {
                        _logger.Warn("Canonical event gateway could not process event {0}", message.GetType().Name);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                await Task.WhenAll(_connections.Values.Select(connection => connection.StopAsync(cleanDiscovery: false))).ConfigureAwait(false);

                _connections.Clear();
            }
        }

        private async Task HandleEventAsync(IEvent message)
        {
            if (message is ApplicationStartedEvent)
            {
                InitializeCatalogState();
                _started = true;
                await ReconcileConnectionsAsync().ConfigureAwait(false);
                return;
            }

            if (message is ConfigSavedEvent ||
                message is ProviderAddedEvent<INotification> ||
                message is ProviderUpdatedEvent<INotification> ||
                message is ProviderDeletedEvent<INotification>)
            {
                if (_started)
                {
                    await ReconcileConnectionsAsync().ConfigureAwait(false);
                }

                return;
            }

            if (!_started)
            {
                return;
            }

            var canonicalEvents = UpdateCatalogState(message);
            canonicalEvents.AddRange(BuildEvents(message));
            foreach (var canonicalEvent in canonicalEvents)
            {
                PublishCanonicalEvent(canonicalEvent);
            }

            if (IsSensorRelevant(message))
            {
                foreach (var connection in _connections.Values)
                {
                    connection.RequestSnapshot();
                }
            }
        }

        private void InitializeCatalogState()
        {
            _authorsById.Clear();
            foreach (var author in _authorService.GetAllAuthors(true))
            {
                _authorsById[author.Id] = author;
            }

            _authorsWithBooks.Clear();
            _authorsWithBooks.UnionWith(_bookRepository.GetAuthorIdsWithBooks() ?? new HashSet<int>());
            _authorsWithoutBooks.Clear();
            _authorsWithoutBooks.UnionWith(_authorsById.Keys.Where(id => !_authorsWithBooks.Contains(id)));
        }

        private List<CanonicalEventData> UpdateCatalogState(IEvent message)
        {
            var newReleases = new List<CanonicalEventData>();
            if (message is AuthorAddedEvent authorAdded && authorAdded.Author != null)
            {
                _authorsById[authorAdded.Author.Id] = authorAdded.Author;
                if (!_authorsWithBooks.Contains(authorAdded.Author.Id))
                {
                    _authorsWithoutBooks.Add(authorAdded.Author.Id);
                }
            }

            if (message is BookInfoRefreshedEvent infoRefreshed && infoRefreshed.Author != null)
            {
                var authorId = infoRefreshed.Author.Id;
                if (_authorsWithoutBooks.Remove(authorId))
                {
                    // The first catalog load for an author is not a newly detected release.
                    _authorsWithBooks.Add(authorId);
                }
                else if (infoRefreshed.Added != null && _authorsWithBooks.Contains(authorId))
                {
                    foreach (var book in infoRefreshed.Added)
                    {
                        newReleases.Add(new CanonicalEventData("new_release_detected", infoRefreshed.Author, book));
                    }
                }

                _authorsById[authorId] = infoRefreshed.Author;
            }

            return newReleases;
        }

        private async Task ReconcileConnectionsAsync()
        {
            var desired = new Dictionary<int, (NotificationDefinition Definition, MqttGatewaySettings Settings, string Fingerprint)>();
            var canonicalWebhooks = new List<(NotificationDefinition Definition, WebhookSettings Settings)>();
            foreach (var definition in _notificationFactory.All())
            {
                if (!definition.Enable)
                {
                    continue;
                }

                if (string.Equals(definition.Implementation, nameof(MqttGateway), StringComparison.OrdinalIgnoreCase) &&
                    definition.Settings is MqttGatewaySettings mqttSettings &&
                    mqttSettings.Validate().IsValid)
                {
                    desired[definition.Id] = (definition, mqttSettings, _notificationText.LocaleFingerprint(MqttGatewayMqtt.GetSettingsFingerprint(mqttSettings)));
                }

                if (string.Equals(definition.Implementation, nameof(Webhook), StringComparison.OrdinalIgnoreCase) &&
                    definition.Settings is WebhookSettings webhookSettings &&
                    webhookSettings.IncludeCanonicalEvents &&
                    webhookSettings.Validate().IsValid)
                {
                    canonicalWebhooks.Add((definition, webhookSettings));
                }
            }

            _canonicalWebhooks.Clear();
            _canonicalWebhooks.AddRange(canonicalWebhooks);

            foreach (var existing in _connections.ToList())
            {
                if (!desired.TryGetValue(existing.Key, out var replacement) ||
                    !string.Equals(existing.Value.ConfigurationFingerprint(), replacement.Fingerprint, StringComparison.Ordinal))
                {
                    _connections.Remove(existing.Key);
                    await existing.Value.StopAsync(cleanDiscovery: true).ConfigureAwait(false);
                }
            }

            foreach (var item in desired)
            {
                if (!_connections.ContainsKey(item.Key))
                {
                    _connections[item.Key] = new MqttGatewayConnectionWorker(
                        item.Value.Definition,
                        item.Value.Settings,
                        _snapshotProvider,
                        _configFileProvider,
                        _notificationText,
                        _logger);
                }
            }
        }

        private IEnumerable<CanonicalEventData> BuildEvents(IEvent message)
        {
            if (message is BookAddedEvent bookAdded && bookAdded.Book != null)
            {
                var author = ResolveAuthor(bookAdded.Book.Author, bookAdded.Book.AuthorId);
                yield return new CanonicalEventData("book_added", author, bookAdded.Book);
            }
            else if (message is BookImportedEvent imported && imported.Book != null)
            {
                var author = ResolveAuthor(imported.Author ?? imported.Book.Author, imported.Book.AuthorId);
                var correlationId = Guid.NewGuid().ToString("N");
                var isUpgrade = imported.OldFiles?.Count > 0;
                yield return new CanonicalEventData(
                    "book_imported", author, imported.Book,
                    downloadId: imported.DownloadId,
                    downloadClient: imported.DownloadClientInfo?.Name,
                    isUpgrade: isUpgrade,
                    correlationId: correlationId);

                if (isUpgrade)
                {
                    yield return new CanonicalEventData(
                        "book_upgraded", author, imported.Book,
                        downloadId: imported.DownloadId,
                        downloadClient: imported.DownloadClientInfo?.Name,
                        isUpgrade: true,
                        correlationId: correlationId);
                }
                else
                {
                    foreach (var completedSeries in GetCompletedSeries(imported.Book))
                    {
                        yield return new CanonicalEventData(
                            "series_completed", author, imported.Book, completedSeries,
                            downloadId: imported.DownloadId,
                            downloadClient: imported.DownloadClientInfo?.Name,
                            isUpgrade: false,
                            correlationId: correlationId);
                    }
                }
            }
            else if (message is DownloadFailedEvent downloadFailed)
            {
                var author = downloadFailed.TrackedDownload?.RemoteBook?.Author ?? ResolveAuthor(null, downloadFailed.AuthorId);
                var books = downloadFailed.TrackedDownload?.RemoteBook?.GetBooksMatchingReleaseMediaType() ?? new List<Book>();
                if (books.Count == 0 && downloadFailed.BookIds?.Count > 0)
                {
                    books = _bookService.GetBooks(downloadFailed.BookIds);
                }

                if (books.Count == 0)
                {
                    yield return new CanonicalEventData("book_failed", author,
                        downloadId: downloadFailed.DownloadId,
                        downloadClient: downloadFailed.DownloadClient,
                        stage: "download");
                }
                else
                {
                    foreach (var book in books)
                    {
                        var currentBook = book;
                        yield return new CanonicalEventData("book_failed",
                            ResolveAuthor(author ?? currentBook.Author, currentBook.AuthorId), currentBook,
                            downloadId: downloadFailed.DownloadId,
                            downloadClient: downloadFailed.DownloadClient,
                            stage: "download");
                    }
                }
            }
            else if (message is BookImportIncompleteEvent importIncomplete && importIncomplete.TrackedDownload != null)
            {
                var tracked = importIncomplete.TrackedDownload;
                var books = tracked.RemoteBook?.GetBooksMatchingReleaseMediaType() ?? new List<Book>();
                var author = ResolveAuthor(tracked.RemoteBook?.Author, books.FirstOrDefault()?.AuthorId ?? 0);
                if (books.Count == 0)
                {
                    yield return new CanonicalEventData("book_failed", author,
                        downloadId: tracked.DownloadItem?.DownloadId,
                        downloadClient: tracked.DownloadItem?.DownloadClientInfo?.Name,
                        stage: "import");
                }
                else
                {
                    foreach (var book in books)
                    {
                        var currentBook = book;
                        yield return new CanonicalEventData("book_failed",
                            ResolveAuthor(author ?? currentBook.Author, currentBook.AuthorId), currentBook,
                            downloadId: tracked.DownloadItem?.DownloadId,
                            downloadClient: tracked.DownloadItem?.DownloadClientInfo?.Name,
                            stage: "import");
                    }
                }
            }
            else if (message is TrackedDownloadUpdatedEvent downloadUpdated)
            {
                foreach (var downloaded in GetDownloadedItems(new[] { downloadUpdated.TrackedDownload }))
                {
                    yield return new CanonicalEventData("book_downloaded",
                        downloaded.Author, downloaded.Book,
                        downloadId: downloaded.DownloadId,
                        downloadClient: downloaded.Client);
                }
            }
            else if (message is TrackedDownloadRefreshedEvent downloadsRefreshed)
            {
                foreach (var downloaded in GetDownloadedItems(downloadsRefreshed.TrackedDownloads))
                {
                    yield return new CanonicalEventData("book_downloaded",
                        downloaded.Author, downloaded.Book,
                        downloadId: downloaded.DownloadId,
                        downloadClient: downloaded.Client);
                }
            }
        }

        private void PublishCanonicalEvent(CanonicalEventData eventData)
        {
            var instanceId = _configFileProvider.InstallationId;
            var instanceName = _configFileProvider.InstanceName;
            var payload = eventData.ToEvent(instanceId, instanceName);

            foreach (var connection in _connections.Values)
            {
                connection.TryPublishEvent(payload);
            }

            if (_canonicalWebhooks.Count == 0)
            {
                return;
            }

            foreach (var webhook in _canonicalWebhooks)
            {
                try
                {
                    _webhookProxy.SendWebhook(new WebhookPayload
                    {
                        EventType = WebhookEventType.Canonical,
                        InstanceName = instanceName,
                        CanonicalEvent = JObject.Parse(payload.ToJson())
                    }, webhook.Settings);
                }
                catch
                {
                    _logger.Warn("Unable to send canonical webhook event {0} for connection {1}", eventData.EventName, webhook.Definition.Name);
                }
            }
        }

        private IEnumerable<DownloadedItem> GetDownloadedItems(IEnumerable<TrackedDownload> trackedDownloads)
        {
            foreach (var tracked in trackedDownloads ?? Enumerable.Empty<TrackedDownload>())
            {
                var downloadId = tracked?.DownloadItem?.DownloadId;
                if (tracked?.State != TrackedDownloadState.ImportPending || string.IsNullOrWhiteSpace(downloadId) || !RememberDownloaded(downloadId))
                {
                    continue;
                }

                var books = tracked.RemoteBook?.GetBooksMatchingReleaseMediaType() ?? new List<Book>();
                if (books.Count == 0)
                {
                    yield return new DownloadedItem(
                        ResolveAuthor(tracked.RemoteBook?.Author, 0),
                        null,
                        downloadId,
                        tracked.DownloadItem?.DownloadClientInfo?.Name);
                    continue;
                }

                foreach (var book in books)
                {
                    yield return new DownloadedItem(
                        ResolveAuthor(book.Author ?? tracked.RemoteBook?.Author, book.AuthorId),
                        book,
                        downloadId,
                        tracked.DownloadItem?.DownloadClientInfo?.Name);
                }
            }
        }

        private bool RememberDownloaded(string downloadId)
        {
            if (!_downloadedIds.Add(downloadId))
            {
                return false;
            }

            _downloadedOrder.Enqueue(downloadId);
            while (_downloadedOrder.Count > DownloadDeduplicationCapacity)
            {
                _downloadedIds.Remove(_downloadedOrder.Dequeue());
            }

            return true;
        }

        private IEnumerable<Series> GetCompletedSeries(Book importedBook)
        {
            if (importedBook == null || importedBook.Id <= 0)
            {
                yield break;
            }

            var links = _seriesBookLinkService.GetLinksByBook(new List<int> { importedBook.Id });
            var now = DateTime.UtcNow;
            foreach (var primaryLink in links.Where(link => link.IsPrimary).GroupBy(link => link.SeriesId).Select(group => group.First()))
            {
                var series = _seriesService.GetSeries(primaryLink.SeriesId);
                if (series == null || series.MediaType != importedBook.MediaType)
                {
                    continue;
                }

                var primaryBookIds = _seriesBookLinkService.GetLinksBySeries(series.Id)
                    .Where(link => link.IsPrimary)
                    .Select(link => link.BookId)
                    .Distinct()
                    .ToList();
                var books = _bookService.GetBooks(primaryBookIds)
                    .Where(book => book.MediaType == series.MediaType && IsMonitored(book) && book.ReleaseDate.HasValue && book.ReleaseDate.Value <= now)
                    .ToList();

                if (!books.Any(book => book.Id == importedBook.Id) || books.Count == 0)
                {
                    continue;
                }

                var mediaType = series.MediaType == BookMediaType.Audiobook ? "audiobook" : "ebook";
                var booksWithFiles = _mediaFileRepository.GetFilesByBooks(books.Select(book => book.Id).ToList())
                    .Where(file => string.IsNullOrWhiteSpace(file.MediaType) || string.Equals(file.MediaType, mediaType, StringComparison.OrdinalIgnoreCase))
                    .Select(file => file.Edition?.BookId ?? 0)
                    .ToHashSet();

                if (books.All(book => booksWithFiles.Contains(book.Id)))
                {
                    yield return series;
                }
            }
        }

        private Author ResolveAuthor(Author author, int authorId)
        {
            if (author != null && author.Id > 0)
            {
                _authorsById[author.Id] = author;
                return author;
            }

            return authorId > 0 && _authorsById.TryGetValue(authorId, out var knownAuthor)
                ? knownAuthor
                : null;
        }

        private static bool IsMonitored(Book book)
        {
            return book.MediaType == BookMediaType.Audiobook ? book.AudiobookMonitored : book.EbookMonitored;
        }

        private bool IsSensorRelevant(IEvent message)
        {
            return message is BookAddedEvent ||
                   message is BookImportedEvent ||
                   message is BookDeletedEvent ||
                   message is AuthorDeletedEvent ||
                   message is BookUpdatedEvent ||
                   message is BookEditedEvent ||
                   message is BookInfoRefreshedEvent ||
                   message is BookFileDeletedEvent ||
                   message is TrackedDownloadUpdatedEvent ||
                   message is TrackedDownloadRefreshedEvent ||
                   message is TrackedDownloadsRemovedEvent ||
                   message is DownloadFailedEvent ||
                   message is BookImportIncompleteEvent ||
                   message is HealthCheckCompleteEvent ||
                   message is HealthCheckFailedEvent;
        }

        private sealed class CanonicalEventData
        {
            public CanonicalEventData(string eventName,
                                      Author author = null,
                                      Book book = null,
                                      Series series = null,
                                      string downloadId = null,
                                      string downloadClient = null,
                                      bool? isUpgrade = null,
                                      string correlationId = null,
                                      string stage = null)
            {
                EventName = eventName;
                Author = author;
                Book = book;
                Series = series;
                DownloadId = downloadId;
                DownloadClient = downloadClient;
                IsUpgrade = isUpgrade;
                CorrelationId = correlationId ?? Guid.NewGuid().ToString("N");
                EventId = Guid.NewGuid().ToString("N");
                OccurredAt = DateTime.UtcNow;
                Stage = stage;
            }

            public string EventName { get; }
            public Author Author { get; }
            public Book Book { get; }
            public Series Series { get; }
            public string DownloadId { get; }
            public string DownloadClient { get; }
            public bool? IsUpgrade { get; }
            public string CorrelationId { get; }
            public string EventId { get; }
            public DateTime OccurredAt { get; }
            public string Stage { get; }

            public MqttGatewayEvent ToEvent(string instanceId, string instanceName)
            {
                return MqttGatewayEvent.Create(
                    EventName,
                    instanceId,
                    instanceName,
                    Author,
                    Book,
                    Series,
                    DownloadId,
                    DownloadClient,
                    IsUpgrade,
                    CorrelationId,
                    Stage,
                    EventId,
                    OccurredAt);
            }
        }

        private sealed record DownloadedItem(Author Author, Book Book, string DownloadId, string Client);
    }
}
