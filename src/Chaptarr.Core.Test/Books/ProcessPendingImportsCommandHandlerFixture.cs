using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Chaptarr.Core.Test.Common;
using FluentValidation;
using FluentValidation.Results;
using NLog;
using Newtonsoft.Json;
using NUnit.Framework;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MetadataSource;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public partial class ProcessPendingImportsCommandHandlerFixture
    {
        private sealed class StubPendingAuthorImportService : IPendingAuthorImportService
        {
            public Queue<List<PendingAuthorImport>> DueResponses { get; } = new();
            public int CleanupCount { get; private set; }
            public List<int> DeletedIds { get; } = new();
            public List<string> RetryReasons { get; } = new();
            public bool DeleteIfUnchangedResult { get; set; } = true;
            public PendingAuthorImport CurrentByProviderId { get; set; }
            public bool UpdateStatusResult { get; set; } = true;
            public long? StatusExpectedVersion { get; private set; }
            public List<PendingAuthorImport> QueuedRequests { get; } = new();

            public Task<int> EnqueueAsync(string providerId, MonitoringConfig config, string sourceApplication)
            {
                var request = new PendingAuthorImport
                {
                    Id = 3000 + QueuedRequests.Count, ProviderId = providerId, AuthorName = config.AuthorName,
                    OverallStatus = PendingImportStatus.Pending, Version = 1, RequestedBy = config.RequestedBy,
                    AudiobookStatus = config.CreateAudiobook ? PendingImportStatus.Pending : PendingImportStatus.NotRequested,
                    EbookStatus = config.CreateEbook ? PendingImportStatus.Pending : PendingImportStatus.NotRequested,
                    AudiobookMonitorExistingMode = config.AudiobookMonitorExistingMode, EbookMonitorExistingMode = config.EbookMonitorExistingMode,
                    AudiobookMonitorNewItems = config.AudiobookMonitorNewItems, EbookMonitorNewItems = config.EbookMonitorNewItems,
                    AudiobookBooksToMonitor = config.AudiobookBooksToMonitor?.Any() == true ? JsonConvert.SerializeObject(config.AudiobookBooksToMonitor) : null,
                    EbookBooksToMonitor = config.EbookBooksToMonitor?.Any() == true ? JsonConvert.SerializeObject(config.EbookBooksToMonitor) : null,
                    AudiobookBooksToSearch = config.AudiobookBooksToSearch?.Any() == true ? JsonConvert.SerializeObject(config.AudiobookBooksToSearch) : null,
                    EbookBooksToSearch = config.EbookBooksToSearch?.Any() == true ? JsonConvert.SerializeObject(config.EbookBooksToSearch) : null,
                    AudiobookNarrations = config.AudiobookNarrations?.Any() == true ? JsonConvert.SerializeObject(config.AudiobookNarrations) : null,
                    SearchForMissingBooks = config.SearchForMissingBooks == true
                };
                QueuedRequests.Add(request);
                DueResponses.Enqueue(new List<PendingAuthorImport> { request });
                return Task.FromResult(request.Id);
            }
            public PendingAuthorImport Get(int id) => GetAll().Find(item => item.Id == id);
            public NzbDrone.Core.Datastore.PagingSpec<PendingAuthorImport> GetPaged(NzbDrone.Core.Datastore.PagingSpec<PendingAuthorImport> pagingSpec) => throw new NotImplementedException();
            public List<PendingAuthorImport> GetAll() => throw new NotImplementedException();
            public List<PendingAuthorImport> GetDueForProcessing(int limit = 10) => DueResponses.Count > 0 ? DueResponses.Dequeue() : new List<PendingAuthorImport>();
            public PendingAuthorImport GetByProviderId(string providerId) => CurrentByProviderId;

            public bool UpdateStatus(PendingAuthorImport item, PendingImportStatus status, string error, long? expectedVersion = null)
            {
                StatusExpectedVersion = expectedVersion;
                if (!UpdateStatusResult) return false;
                item.OverallStatus = status;
                item.LastError = error;
                return true;
            }

            public void ScheduleRetry(PendingAuthorImport item, string error)
            {
                item.OverallStatus = PendingImportStatus.Retrying;
                item.LastError = error;
                RetryReasons.Add(error);
            }
            public bool IsActiveRequest(int id, long version) => true;
            public bool SaveProgress(PendingAuthorImport item, PendingAuthorImport previous) => true;
            public void Cancel(int id) => throw new NotImplementedException();
            public bool CancelIfQueued(int id) => throw new NotImplementedException();
            public void RetryNow(int id) => throw new NotImplementedException();
            public void Delete(int id) => DeletedIds.Add(id);
            public bool TryDeleteIfUnchanged(PendingAuthorImport item)
            {
                if (DeleteIfUnchangedResult)
                {
                    DeletedIds.Add(item.Id);
                }

                return DeleteIfUnchangedResult;
            }
            public void CleanupOldCompleted() => CleanupCount++;
        }

        private class BookServiceProxy : DispatchProxy
        {
            public List<Book> Books { get; set; } = new();
            public Func<List<Edition>, List<Book>, Dictionary<int, Book>> WantedEditions { get; set; }
            public int CatalogLoads { get; private set; }
            public bool IgnoreMonitoringUpdates { get; set; }
            public bool ReplaceRowsOnMonitoringUpdate { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case nameof(IBookService.AddWantedEditions):
                        return WantedEditions((List<Edition>)args[0], Books);
                    case nameof(IBookService.SetAddOptions):
                        return null;
                    case nameof(IBookService.GetBooksByAuthor):
                        CatalogLoads++;
                        return Books.Where(book => book.AuthorId == (int)args[0]).ToList();
                    case nameof(IBookService.GetBooks):
                        var ids = ((IEnumerable<int>)args[0]).ToHashSet();
                        return Books.Where(book => ids.Contains(book.Id)).ToList();
                    case nameof(IBookService.FindAllByWorkProviderId):
                        var provider = (string)args[0];
                        var providerId = ProviderIdHelper.Canonicalize((string)args[1], provider);
                        var requestedMediaType = (BookMediaType)args[2];
                        return Books.Where(book => book.MediaType == requestedMediaType &&
                                                   BookEditionIdentity.GetCanonicalWorkProviderIds(book)
                                                       .Concat(book.RemoteProviderIds ?? Enumerable.Empty<string>())
                                                       .Any(id => string.Equals(id, providerId, StringComparison.OrdinalIgnoreCase)))
                            .ToList();
                    case nameof(IBookService.MonitorRequestedBooks):
                    case nameof(IBookService.SetMonitoredForMediaType):
                        if (!IgnoreMonitoringUpdates)
                        {
                            var monitoredIds = ((IEnumerable<int>)args[0]).ToHashSet();
                            var mediaType = (string)args[1];
                            var monitored = targetMethod.Name == nameof(IBookService.MonitorRequestedBooks) || (bool)args[2];
                            for (var index = 0; index < Books.Count; index++)
                            {
                                var book = Books[index];
                                if (!monitoredIds.Contains(book.Id))
                                {
                                    continue;
                                }

                                if (ReplaceRowsOnMonitoringUpdate)
                                {
                                    book = new Book
                                    {
                                        Id = book.Id,
                                        AuthorId = book.AuthorId,
                                        Author = book.Author,
                                        MediaType = book.MediaType,
                                        HardcoverBookId = book.HardcoverBookId,
                                        GoodreadsWorkId = book.GoodreadsWorkId,
                                        RemoteProviderIds = book.RemoteProviderIds == null
                                            ? null
                                            : new HashSet<string>(book.RemoteProviderIds, StringComparer.OrdinalIgnoreCase),
                                        AudiobookMonitored = book.AudiobookMonitored,
                                        EbookMonitored = book.EbookMonitored
                                    };
                                    Books[index] = book;
                                }

                                book.SetMonitoredForMediaType(mediaType, monitored);
                            }
                        }

                        return null;
                    default:
                        throw new NotImplementedException($"Test proxy does not implement IBookService.{targetMethod?.Name}");
                }
            }
        }

        private class AuthorLibraryProxy : DispatchProxy
        {
            public AuthorTerminalException Terminal { get; set; }
            public Author AddedAuthor { get; set; }
            public MonitoringConfig LastConfig { get; private set; }
            public Action<MonitoringConfig> OnAdd { get; set; }
            public Func<string, MonitoringConfig, Author> Add { get; set; }
            public List<string> ProviderIds { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IAuthorLibraryService.AddAuthorAsync))
                {
                    LastConfig = (MonitoringConfig)args[1];
                    ProviderIds.Add((string)args[0]);
                    if (Add != null) return Task.FromResult(Add((string)args[0], LastConfig));
                    OnAdd?.Invoke(LastConfig);
                    if (Terminal != null)
                    {
                        return Task.FromException<Author>(Terminal);
                    }

                    return Task.FromResult(AddedAuthor);
                }

                throw new NotImplementedException($"Test proxy does not implement IAuthorLibraryService.{targetMethod?.Name}");
            }
        }

        private class BookInfoProxy : DispatchProxy
        {
            public Exception WorkException { get; set; }
            public List<string> RequestedWorkIds { get; } = new();
            public string PrimaryProviderId { get; set; }
            public Func<string, BookMediaType, Tuple<string, Book, List<Author>>> Work { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IProvideBookInfo.GetWorkInfo))
                {
                    RequestedWorkIds.Add((string)args[0]);
                    if (WorkException != null)
                    {
                        throw WorkException;
                    }

                    if (Work != null) return Work((string)args[0], (BookMediaType)args[1]);
                    var primary = new Author { RemoteProviderIds = new HashSet<string> { PrimaryProviderId } };
                    return Tuple.Create(PrimaryProviderId, new Book { MediaType = (BookMediaType)args[1], Author = primary,
                        RemoteProviderIds = new HashSet<string> { (string)args[0] } }, new List<Author> { primary });
                }

                throw new NotImplementedException($"Test proxy does not implement IProvideBookInfo.{targetMethod?.Name}");
            }
        }
        private sealed class RecordingCommandQueue : CommandQueueManager
        {
            public RecordingCommandQueue()
                : base(DispatchProxy.Create<ICommandRepository, RecordingCommandRepository>(), null, null, LogManager.GetCurrentClassLogger())
            {
            }

            public List<Command> Pushed => All().Select(model => model.Body).ToList();
        }

        private class RecordingCommandRepository : DispatchProxy
        {
            private int _nextId;

            protected override object Invoke(MethodInfo method, object[] args)
            {
                if (method.Name == nameof(ICommandRepository.Insert))
                {
                    var model = (CommandModel)args[0];
                    model.Id = ++_nextId;
                    return model;
                }

                throw new NotImplementedException(method.Name);
            }
        }

        private sealed class RecordingEventAggregator : IEventAggregator
        {
            public List<IEvent> Events { get; } = new();

            public void PublishEvent<TEvent>(TEvent @event) where TEvent : class, IEvent
            {
                Events.Add(@event);
            }
        }

        private static PendingAuthorImport Pending(int id)
        {
            return new PendingAuthorImport
            {
                Id = id,
                ProviderId = $"gr:{id}",
                AuthorName = $"Author {id}",
                OverallStatus = PendingImportStatus.Pending,
                AudiobookStatus = PendingImportStatus.NotRequested,
                EbookStatus = PendingImportStatus.Pending,
                SearchForMissingBooks = false
            };
        }

        private static ProcessPendingImportsCommandHandler BuildHandler(
            StubPendingAuthorImportService pendingImportService,
            RecordingCommandQueue commandQueue,
            RecordingEventAggregator eventAggregator,
            AuthorTerminalException terminal = null,
            Author author = null,
            IBookService bookService = null,
            IProvideBookInfo bookInfo = null,
            Action<MonitoringConfig> configCaptured = null)
        {
            author ??= new Author { Id = 42, Name = "Existing Author" };


            var authorLibraryService = DispatchProxy.Create<IAuthorLibraryService, AuthorLibraryProxy>();
            ((AuthorLibraryProxy)authorLibraryService).Terminal = terminal;
            ((AuthorLibraryProxy)authorLibraryService).AddedAuthor = author;
            ((AuthorLibraryProxy)authorLibraryService).OnAdd = configCaptured;

            if (bookService == null)
            {
                bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            }

            bookInfo ??= DispatchProxy.Create<IProvideBookInfo, BookInfoProxy>();
            if (bookInfo is BookInfoProxy info && info.PrimaryProviderId == null && pendingImportService.DueResponses.Count > 0)
                info.PrimaryProviderId = pendingImportService.DueResponses.Peek().First().ProviderId;

            return new ProcessPendingImportsCommandHandler(
                pendingImportService,
                authorLibraryService,
                bookService,
                bookInfo,
                commandQueue,
                eventAggregator,
                LogManager.GetCurrentClassLogger());
        }

        [TestCase(true)]
        [TestCase(false)]
        public void excluded_list_request_only_fails_the_version_that_was_processed(bool unchanged)
        {
            var pendingImportService = new StubPendingAuthorImportService { UpdateStatusResult = unchanged };
            var events = new RecordingEventAggregator();
            var pending = Pending(158);
            pending.ProviderId = "gr:58";
            pending.AuthorName = "Frank Herbert";
            pending.Version = 3;
            pending.RequestedBy = "ImportList:Favorites";
            pendingImportService.DueResponses.Enqueue(new List<PendingAuthorImport> { pending });

            MonitoringConfig observedConfig = null;
            var handler = BuildHandler(
                pendingImportService,
                new RecordingCommandQueue(),
                events,
                configCaptured: config =>
                {
                    observedConfig = config;
                    throw new ValidationException(new[]
                    {
                        new ValidationFailure("ProviderId", "Author is excluded.")
                        {
                            ErrorCode = AuthorLibraryService.ImportListAuthorExcludedErrorCode
                        }
                    });
                });

            handler.Execute(new ProcessPendingImportsCommand());

            Assert.That(observedConfig?.RequestedBy, Is.EqualTo("ImportList:Favorites"));
            Assert.That(pendingImportService.StatusExpectedVersion, Is.EqualTo(3));
            Assert.That(pending.OverallStatus, Is.EqualTo(unchanged ? PendingImportStatus.Failed : PendingImportStatus.Pending));
            Assert.That(events.Events.OfType<PendingAuthorImportFailedEvent>().Count(), Is.EqualTo(unchanged ? 1 : 0));
            Assert.That(pendingImportService.RetryReasons, Is.Empty);
        }

        [Test]
        public void deferred_import_should_pass_current_seed_modes_independently_of_new_item_policy()
        {
            var pendingImportService = new StubPendingAuthorImportService();
            var pending = Pending(159);
            pending.AudiobookStatus = PendingImportStatus.Pending;
            pending.EbookStatus = PendingImportStatus.Pending;
            pending.AudiobookMonitored = true;
            pending.AudiobookMonitorExistingMode = MonitorTypes.None;
            pending.AudiobookMonitorNewItems = NewItemMonitorTypes.All;
            pending.EbookMonitored = true;
            pending.EbookMonitorExistingMode = MonitorTypes.All;
            pending.EbookMonitorNewItems = NewItemMonitorTypes.None;
            pendingImportService.DueResponses.Enqueue(new List<PendingAuthorImport> { pending });

            var commandQueue = new RecordingCommandQueue();
            var eventAggregator = new RecordingEventAggregator();
            var authorLibrary = DispatchProxy.Create<IAuthorLibraryService, AuthorLibraryProxy>();
            ((AuthorLibraryProxy)authorLibrary).AddedAuthor = new Author { Id = 42, Name = "Deferred Author" };
            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var bookInfo = DispatchProxy.Create<IProvideBookInfo, BookInfoProxy>();
            var handler = new ProcessPendingImportsCommandHandler(
                pendingImportService,
                authorLibrary,
                bookService,
                bookInfo,
                commandQueue,
                eventAggregator,
                LogManager.GetCurrentClassLogger());

            handler.Execute(new ProcessPendingImportsCommand());

            var config = ((AuthorLibraryProxy)authorLibrary).LastConfig;
            Assert.That(config.AudiobookMonitored, Is.True);
            Assert.That(config.AudiobookMonitorExistingMode, Is.EqualTo(MonitorTypes.None));
            Assert.That(config.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.All));
            Assert.That(config.EbookMonitored, Is.True);
            Assert.That(config.EbookMonitorExistingMode, Is.EqualTo(MonitorTypes.All));
            Assert.That(config.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
        }

        [Test]
        public void deferred_import_should_restore_last_selected_media_type()
        {
            var pendingImportService = new StubPendingAuthorImportService();
            var pending = Pending(160);
            pending.LastSelectedMediaType = "ebook";
            pendingImportService.DueResponses.Enqueue(new List<PendingAuthorImport> { pending });

            var commandQueue = new RecordingCommandQueue();
            var eventAggregator = new RecordingEventAggregator();
            var authorLibrary = DispatchProxy.Create<IAuthorLibraryService, AuthorLibraryProxy>();
            ((AuthorLibraryProxy)authorLibrary).AddedAuthor = new Author { Id = 42, Name = "Deferred Author" };
            var handler = new ProcessPendingImportsCommandHandler(
                pendingImportService,
                authorLibrary,
                DispatchProxy.Create<IBookService, BookServiceProxy>(),
                DispatchProxy.Create<IProvideBookInfo, BookInfoProxy>(),
                commandQueue,
                eventAggregator,
                LogManager.GetCurrentClassLogger());

            handler.Execute(new ProcessPendingImportsCommand());

            Assert.That(((AuthorLibraryProxy)authorLibrary).LastConfig.LastSelectedMediaType, Is.EqualTo("ebook"));
        }

        [Test]
        public void deferred_import_should_restore_media_specific_tags_without_bleeding_between_sides()
        {
            var pendingImportService = new StubPendingAuthorImportService();
            var pending = Pending(161);
            pending.AudiobookStatus = PendingImportStatus.Pending;
            pending.EbookStatus = PendingImportStatus.Pending;
            pending.Tags = "[99]";
            pending.AudiobookTags = "[1,2]";
            pending.EbookTags = "[]";
            pendingImportService.DueResponses.Enqueue(new List<PendingAuthorImport> { pending });

            var commandQueue = new RecordingCommandQueue();
            var eventAggregator = new RecordingEventAggregator();
            var authorLibrary = DispatchProxy.Create<IAuthorLibraryService, AuthorLibraryProxy>();
            ((AuthorLibraryProxy)authorLibrary).AddedAuthor = new Author { Id = 42, Name = "Deferred Author" };
            var handler = new ProcessPendingImportsCommandHandler(
                pendingImportService,
                authorLibrary,
                DispatchProxy.Create<IBookService, BookServiceProxy>(),
                DispatchProxy.Create<IProvideBookInfo, BookInfoProxy>(),
                commandQueue,
                eventAggregator,
                LogManager.GetCurrentClassLogger());

            handler.Execute(new ProcessPendingImportsCommand());

            var config = ((AuthorLibraryProxy)authorLibrary).LastConfig;
            Assert.That(config.Tags, Is.EquivalentTo(new[] { 99 }));
            Assert.That(config.AudiobookTags, Is.EquivalentTo(new[] { 1, 2 }));
            Assert.That(config.EbookTags, Is.Empty);
        }

        [Test]
        public void deferred_exact_targets_should_remain_scoped_to_their_media_side()
        {
            var pendingImportService = new StubPendingAuthorImportService();
            var pending = Pending(160);
            pending.AudiobookStatus = PendingImportStatus.Pending;
            pending.EbookStatus = PendingImportStatus.Pending;
            pending.AudiobookBooksToMonitor = "[\"hc:audio\"]";
            pending.EbookBooksToMonitor = "[\"hc:ebook\"]";
            pendingImportService.DueResponses.Enqueue(new List<PendingAuthorImport> { pending });

            var commandQueue = new RecordingCommandQueue();
            var eventAggregator = new RecordingEventAggregator();
            var authorLibrary = DispatchProxy.Create<IAuthorLibraryService, AuthorLibraryProxy>();
            ((AuthorLibraryProxy)authorLibrary).AddedAuthor = new Author { Id = 42, Name = "Deferred Author" };
            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var handler = new ProcessPendingImportsCommandHandler(
                pendingImportService,
                authorLibrary,
                bookService,
                DispatchProxy.Create<IProvideBookInfo, BookInfoProxy>(),
                commandQueue,
                eventAggregator,
                LogManager.GetCurrentClassLogger());

            handler.Execute(new ProcessPendingImportsCommand());

            var config = ((AuthorLibraryProxy)authorLibrary).LastConfig;
            Assert.That(config.AudiobookBooksToMonitor, Is.EqualTo(new[] { "hc:audio" }));
            Assert.That(config.EbookBooksToMonitor, Is.EqualTo(new[] { "hc:ebook" }));
            Assert.That(config.SpecificBookProviderIds, Is.Null);
            Assert.That(config.SpecificBookMediaType, Is.Null);
        }
        [Test]
        public void should_not_emit_import_complete_while_continue_drain_has_more_due_items()
        {
            var pendingImportService = new StubPendingAuthorImportService();
            pendingImportService.DueResponses.Enqueue(new List<PendingAuthorImport> { Pending(1) });
            pendingImportService.DueResponses.Enqueue(new List<PendingAuthorImport> { Pending(2) });

            var commandQueue = new RecordingCommandQueue();
            var eventAggregator = new RecordingEventAggregator();
            var handler = BuildHandler(pendingImportService, commandQueue, eventAggregator);

            handler.Execute(new ProcessPendingImportsCommand { ContinueUntilEmpty = true, BatchSize = 1 });

            Assert.That(eventAggregator.Events.OfType<ImportStageProgressEvent>().Any(e => e.Stage == ImportStage.ImportComplete), Is.False);
            var continuation = commandQueue.Pushed.OfType<ProcessPendingImportsCommand>().Single();
            Assert.That(continuation.ContinueUntilEmpty, Is.True);
            Assert.That(continuation.Continuation, Is.EqualTo(1));
            Assert.That(pendingImportService.CleanupCount, Is.EqualTo(1));
        }

        [Test]
        public void should_emit_import_complete_on_final_continue_drain_batch()
        {
            var pendingImportService = new StubPendingAuthorImportService();
            pendingImportService.DueResponses.Enqueue(new List<PendingAuthorImport> { Pending(1) });
            pendingImportService.DueResponses.Enqueue(new List<PendingAuthorImport>());

            var commandQueue = new RecordingCommandQueue();
            var eventAggregator = new RecordingEventAggregator();
            var handler = BuildHandler(pendingImportService, commandQueue, eventAggregator);

            handler.Execute(new ProcessPendingImportsCommand { ContinueUntilEmpty = true, BatchSize = 1, Continuation = 1 });

            Assert.That(commandQueue.Pushed.OfType<ProcessPendingImportsCommand>(), Is.Empty);
            Assert.That(eventAggregator.Events.OfType<ImportStageProgressEvent>().Count(e => e.Stage == ImportStage.ImportComplete), Is.EqualTo(1));
            Assert.That(pendingImportService.CleanupCount, Is.EqualTo(0));
        }

        [Test]
        public void should_fail_declared_terminal_without_scheduling_an_automatic_retry()
        {
            var pendingImportService = new StubPendingAuthorImportService();
            var pending = Pending(99);
            pendingImportService.DueResponses.Enqueue(new List<PendingAuthorImport> { pending });

            var commandQueue = new RecordingCommandQueue();
            var eventAggregator = new RecordingEventAggregator();
            var terminal = new AuthorTerminalException(
                "author_identity_ambiguous",
                pending.ProviderId,
                pending.ProviderId,
                "Identity evidence is ambiguous.",
                reopenable: true);
            var handler = BuildHandler(
                pendingImportService,
                commandQueue,
                eventAggregator,
                terminal: terminal);

            handler.Execute(new ProcessPendingImportsCommand());

            Assert.That(pending.OverallStatus, Is.EqualTo(PendingImportStatus.Failed));
            Assert.That(pending.LastError, Does.Contain("author_identity_ambiguous"));
            Assert.That(eventAggregator.Events.OfType<PendingAuthorImportFailedEvent>().Count(), Is.EqualTo(1));
        }

        [Test]
        public void should_stop_automatic_retry_for_a_typed_never_served_not_found()
        {
            var pendingImportService = new StubPendingAuthorImportService();
            var pending = Pending(100);
            pendingImportService.DueResponses.Enqueue(new List<PendingAuthorImport> { pending });

            var commandQueue = new RecordingCommandQueue();
            var eventAggregator = new RecordingEventAggregator();
            var terminal = new AuthorTerminalException(
                "author_provider_record_missing",
                pending.ProviderId,
                pending.ProviderId,
                "The provider no longer has this author record.",
                reopenable: true);
            var handler = BuildHandler(
                pendingImportService,
                commandQueue,
                eventAggregator,
                terminal: terminal);

            handler.Execute(new ProcessPendingImportsCommand());

            Assert.That(pending.OverallStatus, Is.EqualTo(PendingImportStatus.Failed));
            Assert.That(pending.LastError, Does.Contain("author_provider_record_missing"));
            Assert.That(eventAggregator.Events.OfType<PendingAuthorImportFailedEvent>().Count(), Is.EqualTo(1));
        }

        [TestCase(true, false, new int[] { 101 })]
        [TestCase(false, true, new int[] { 202 })]
        [TestCase(true, true, new int[] { 101, 202 })]
        public void should_search_requested_books_for_the_selected_media_types(
            bool searchAudiobook,
            bool searchEbook,
            int[] expectedBookIds)
        {
            var pendingImportService = new StubPendingAuthorImportService();
            var pending = Pending(150);
            pending.AudiobookStatus = searchAudiobook ? PendingImportStatus.Pending : PendingImportStatus.NotRequested;
            pending.EbookStatus = searchEbook ? PendingImportStatus.Pending : PendingImportStatus.NotRequested;
            pending.AudiobookBooksToSearch = searchAudiobook ? @"[""gr:1001""]" : null;
            pending.EbookBooksToSearch = searchEbook ? @"[""gr:2002""]" : null;
            pending.AudiobookBooksToMonitor = pending.AudiobookBooksToSearch;
            pending.EbookBooksToMonitor = pending.EbookBooksToSearch;
            pending.ExplicitBookSearches = RequestedBookSearch.Write(new[]
            {
                searchAudiobook ? new RequestedBookSearch { MediaType = BookMediaType.Audiobook, WorkProviderId = "gr:1001" } : null,
                searchEbook ? new RequestedBookSearch { MediaType = BookMediaType.Ebook, WorkProviderId = "gr:2002" } : null
            }.Where(request => request != null));
            pendingImportService.DueResponses.Enqueue(new List<PendingAuthorImport> { pending });

            var author = new Author { Id = 42, Name = "Existing Author" };
            var books = new List<Book>
            {
                new Book
                {
                    Id = 101,
                    AuthorId = author.Id,
                    MediaType = BookMediaType.Audiobook,
                    GoodreadsWorkId = "gr:1001"
                },
                new Book
                {
                    Id = 202,
                    AuthorId = author.Id,
                    MediaType = BookMediaType.Ebook,
                    GoodreadsWorkId = "gr:2002"
                }
            };
            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)bookService).Books = books;
            var commandQueue = new RecordingCommandQueue();
            var handler = BuildHandler(
                pendingImportService,
                commandQueue,
                new RecordingEventAggregator(),
                author: author,
                bookService: bookService);

            handler.Execute(new ProcessPendingImportsCommand());

            var searches = commandQueue.Pushed.OfType<BookSearchCommand>().ToList();
            Assert.That(searches.SelectMany(search => search.BookIds), Is.EquivalentTo(expectedBookIds));
            Assert.That(searches.All(search => search.BookIds.Count == 1), Is.True);
            Assert.That(searches.All(search => search.Trigger == CommandTrigger.Manual), Is.True);
            Assert.That(books.Where(book => expectedBookIds.Contains(book.Id)).All(book => book.IsMonitored()), Is.True);
            Assert.That(author.AudiobookMonitored, Is.Null);
            Assert.That(author.EbookMonitored, Is.Null);
            Assert.That(commandQueue.Pushed.OfType<MissingBookSearchCommand>(), Is.Empty);
            Assert.That(pendingImportService.DeletedIds, Does.Contain(pending.Id));
        }

        [Test]
        public void should_retain_exact_searches_until_unavailable_author_is_imported()
        {
            var pendingImportService = new StubPendingAuthorImportService();
            var pending = Pending(151);
            pending.AudiobookStatus = PendingImportStatus.Pending;
            pending.EbookStatus = PendingImportStatus.Pending;
            pending.AudiobookBooksToSearch = "[\"gr:1001\"]";
            pending.EbookBooksToSearch = "[\"gr:2002\"]";
            pending.AudiobookBooksToMonitor = pending.AudiobookBooksToSearch;
            pending.EbookBooksToMonitor = pending.EbookBooksToSearch;
            pending.ExplicitBookSearches = RequestedBookSearch.Write(new[]
            {
                new RequestedBookSearch { MediaType = BookMediaType.Audiobook, WorkProviderId = "gr:1001" },
                new RequestedBookSearch { MediaType = BookMediaType.Ebook, WorkProviderId = "gr:2002" }
            });
            pendingImportService.DueResponses.Enqueue(new List<PendingAuthorImport> { pending });

            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)bookService).Books = new List<Book>
            {
                new Book
                {
                    Id = 101,
                    AuthorId = 42,
                    MediaType = BookMediaType.Audiobook,
                    GoodreadsWorkId = "gr:1001"
                },
                new Book
                {
                    Id = 202,
                    AuthorId = 42,
                    MediaType = BookMediaType.Ebook,
                    GoodreadsWorkId = "gr:2002"
                }
            };
            var commandQueue = new RecordingCommandQueue();
            var eventAggregator = new RecordingEventAggregator();
            var identityVerified = false;
            var handler = BuildHandler(
                pendingImportService,
                commandQueue,
                eventAggregator,
                author: new Author { Id = 42, Name = "Now Available" },
                bookService: bookService,
                configCaptured: _ =>
                {
                    if (!identityVerified) throw new AuthorIdentityUnverifiedException("Provider identity cannot be verified yet");
                });

            handler.Execute(new ProcessPendingImportsCommand());

            Assert.That(pending.OverallStatus, Is.EqualTo(PendingImportStatus.Retrying));
            Assert.That(pendingImportService.DeletedIds, Is.Empty);
            Assert.That(commandQueue.Pushed, Is.Empty, "unverified ownership must not start a search or scan");
            Assert.That(eventAggregator.Events.OfType<PendingAuthorImportSucceededEvent>(), Is.Empty);

            identityVerified = true;
            pendingImportService.DueResponses.Enqueue(new List<PendingAuthorImport> { pending });
            handler.Execute(new ProcessPendingImportsCommand());

            var searches = commandQueue.Pushed.OfType<BookSearchCommand>().ToList();
            Assert.That(searches.SelectMany(search => search.BookIds), Is.EquivalentTo(new[] { 101, 202 }));
            Assert.That(searches.All(search => search.BookIds.Count == 1), Is.True);
            Assert.That(searches.All(search => search.Trigger == CommandTrigger.Manual), Is.True);
            Assert.That(commandQueue.Pushed.OfType<MissingBookSearchCommand>(), Is.Empty);
            Assert.That(pendingImportService.DeletedIds, Does.Contain(pending.Id));
            Assert.That(eventAggregator.Events.OfType<PendingAuthorImportSucceededEvent>().Count(), Is.EqualTo(1));
        }

        [Test]
        public void should_not_delete_a_second_book_request_merged_while_the_first_was_processing()
        {
            var pendingImportService = new StubPendingAuthorImportService { DeleteIfUnchangedResult = false };
            var pending = Pending(158);
            pending.Version = 4;
            pending.EbookBooksToSearch = "[\"gr:2002\"]";
            pending.EbookBooksToMonitor = pending.EbookBooksToSearch;
            pendingImportService.DueResponses.Enqueue(new List<PendingAuthorImport> { pending });
            pendingImportService.CurrentByProviderId = new PendingAuthorImport
            {
                Id = pending.Id,
                ProviderId = pending.ProviderId,
                Version = 5,
                OverallStatus = PendingImportStatus.Pending,
                EbookStatus = PendingImportStatus.Pending,
                EbookBooksToMonitor = "[\"gr:2002\",\"gr:second\"]",
                EbookBooksToSearch = "[\"gr:2002\",\"gr:second\"]"
            };
            var author = new Author { Id = 42, Name = "Existing Author" };
            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)bookService).Books = new List<Book>
            {
                new Book { Id = 202, AuthorId = author.Id, MediaType = BookMediaType.Ebook, GoodreadsWorkId = "gr:2002" }
            };
            var events = new RecordingEventAggregator();
            var commandQueue = new RecordingCommandQueue();
            var handler = BuildHandler(
                pendingImportService,
                commandQueue,
                events,
                author: author,
                bookService: bookService);

            handler.Execute(new ProcessPendingImportsCommand());

            Assert.That(pendingImportService.DeletedIds, Is.Empty);
            Assert.That(pendingImportService.CurrentByProviderId.EbookBooksToSearch, Does.Contain("gr:second"));
            Assert.That(commandQueue.Pushed.OfType<BookSearchCommand>().Single().BookIds, Is.EqualTo(new[] { 202 }));
            Assert.That(commandQueue.Pushed.OfType<MissingBookSearchCommand>(), Is.Empty);
            Assert.That(events.Events.OfType<PendingAuthorImportSucceededEvent>(), Is.Empty);
        }

        [Test]
        public void exact_search_should_monitor_requested_row_without_enabling_paused_author_side()
        {
            var pendingImportService = new StubPendingAuthorImportService();
            var pending = Pending(152);
            pending.AudiobookStatus = PendingImportStatus.Pending;
            pending.EbookStatus = PendingImportStatus.NotRequested;
            pending.AudiobookMonitored = false;
            pending.AudiobookBooksToSearch = @"[""gr:1001""]";
            pending.ExplicitBookSearches = RequestedBookSearch.Write(new[]
                { new RequestedBookSearch { MediaType = BookMediaType.Audiobook, WorkProviderId = "gr:1001" } });
            pendingImportService.DueResponses.Enqueue(new List<PendingAuthorImport> { pending });

            var author = new Author
            {
                Id = 42,
                Name = "Existing Author",
                AudiobookMonitored = false,
                AudiobookMonitorNewItems = NewItemMonitorTypes.None
            };
            var book = new Book
            {
                Id = 101,
                AuthorId = author.Id,
                Author = author,
                MediaType = BookMediaType.Audiobook,
                GoodreadsWorkId = "gr:1001",
                AudiobookMonitored = true
            };
            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)bookService).Books = new List<Book> { book };
            ((BookServiceProxy)bookService).ReplaceRowsOnMonitoringUpdate = true;
            var commandQueue = new RecordingCommandQueue();
            var handler = BuildHandler(
                pendingImportService,
                commandQueue,
                new RecordingEventAggregator(),
                author: author,
                bookService: bookService);

            handler.Execute(new ProcessPendingImportsCommand());

            Assert.That(author.AudiobookMonitored, Is.False);
            Assert.That(((BookServiceProxy)bookService).Books.Single().IsMonitored(), Is.True);
            var search = commandQueue.Pushed.OfType<BookSearchCommand>().Single();
            Assert.That(search.BookIds, Is.EqualTo(new[] { book.Id }));
            Assert.That(search.Trigger, Is.EqualTo(CommandTrigger.Manual));
            Assert.That(pendingImportService.DeletedIds, Does.Contain(pending.Id));
        }

        [Test]
        public void queued_root_scoped_request_should_select_requested_book_without_resuming_author_side(
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType mediaType,
            [Values] MonitorTypes initial,
            [Values(false, true)] bool searchNow)
        {
            var audio = mediaType == BookMediaType.Audiobook;
            var pendingService = new StubPendingAuthorImportService();
            var pending = Pending(154);
            pending.AudiobookStatus = audio ? PendingImportStatus.Pending : PendingImportStatus.NotRequested;
            pending.EbookStatus = audio ? PendingImportStatus.NotRequested : PendingImportStatus.Pending;
            pending.AudiobookMonitored = false;
            pending.EbookMonitored = false;
            pending.AudiobookMonitorExistingMode = audio ? initial : null;
            pending.EbookMonitorExistingMode = audio ? null : initial;
            pending.AudiobookBooksToMonitor = audio ? @"[""gr:1001""]" : null;
            pending.EbookBooksToMonitor = audio ? null : @"[""gr:1001""]";
            pending.AudiobookBooksToSearch = audio && searchNow ? @"[""gr:1001""]" : null;
            pending.EbookBooksToSearch = !audio && searchNow ? @"[""gr:1001""]" : null;
            pending.ExplicitBookSearches = searchNow ? RequestedBookSearch.Write(new[]
                { new RequestedBookSearch { MediaType = mediaType, WorkProviderId = "gr:1001" } }) : null;
            pendingService.DueResponses.Enqueue(new List<PendingAuthorImport> { pending });
            var author = new Author
            {
                Id = 42, AudiobookMonitored = false, EbookMonitored = false,
                AddOptions = new AddAuthorOptions
                {
                    AudiobookMonitor = audio ? initial : null,
                    EbookMonitor = audio ? null : initial
                }
            };
            var target = new Book { Id = 101, AuthorId = author.Id, Author = author, MediaType = mediaType, GoodreadsWorkId = "gr:1001" };
            var sibling = new Book { Id = 102, AuthorId = author.Id, Author = author, MediaType = mediaType, GoodreadsWorkId = "gr:1002" };
            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)bookService).Books = new List<Book> { target, sibling };
            var queue = new RecordingCommandQueue();
            var handler = BuildHandler(pendingService, queue, new RecordingEventAggregator(), author: author, bookService: bookService);

            handler.Execute(new ProcessPendingImportsCommand());

            Assert.Multiple(() =>
            {
                Assert.That(author.AudiobookMonitored, Is.False);
                Assert.That(author.EbookMonitored, Is.False);
                Assert.That(target.IsMonitoredForMediaType(mediaType), Is.True);
                Assert.That(target.IsMonitoredWithAuthor(), Is.False);
                Assert.That(sibling.IsMonitored(), Is.False);
                Assert.That(target.AddOptions.AddType, Is.EqualTo(BookAddType.Manual));
                var searches = queue.Pushed.OfType<BookSearchCommand>().ToList();
                Assert.That(searches.Count, Is.EqualTo(searchNow ? 1 : 0));
                if (searchNow)
                {
                    Assert.That(searches.Single().BookIds, Is.EqualTo(new[] { target.Id }));
                    Assert.That(searches.Single().Trigger, Is.EqualTo(CommandTrigger.Manual));
                }
                Assert.That(queue.Pushed.OfType<MissingBookSearchCommand>(), Is.Empty);
                Assert.That(pendingService.DeletedIds, Does.Contain(pending.Id));
            });
        }

        [Test]
        public void should_repair_unmonitored_book_before_exact_search()
        {
            var pendingImportService = new StubPendingAuthorImportService();
            var pending = Pending(153);
            pending.AudiobookStatus = PendingImportStatus.Pending;
            pending.EbookStatus = PendingImportStatus.NotRequested;
            pending.AudiobookMonitored = true;
            pending.AudiobookBooksToSearch = @"[""gr:1001""]";
            pending.AudiobookBooksToMonitor = pending.AudiobookBooksToSearch;
            pendingImportService.DueResponses.Enqueue(new List<PendingAuthorImport> { pending });

            var author = new Author
            {
                Id = 42,
                Name = "Existing Author",
                AudiobookMonitored = true
            };
            var book = new Book
            {
                Id = 101,
                AuthorId = author.Id,
                Author = author,
                MediaType = BookMediaType.Audiobook,
                GoodreadsWorkId = "gr:1001",
                AudiobookMonitored = false
            };
            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)bookService).Books = new List<Book> { book };
            var commandQueue = new RecordingCommandQueue();
            var handler = BuildHandler(
                pendingImportService,
                commandQueue,
                new RecordingEventAggregator(),
                author: author,
                bookService: bookService);

            handler.Execute(new ProcessPendingImportsCommand());

            Assert.That(book.IsMonitoredWithAuthor(), Is.True);
            Assert.That(commandQueue.Pushed.OfType<BookSearchCommand>().Single().BookIds, Is.EqualTo(new[] { book.Id }));
            Assert.That(pendingImportService.DeletedIds, Does.Contain(pending.Id));
        }

        [Test]
        public void should_retain_request_when_requested_book_is_not_yet_in_the_author_catalog()
        {
            var pendingImportService = new StubPendingAuthorImportService();
            var pending = Pending(155);
            pending.AudiobookStatus = PendingImportStatus.Pending;
            pending.EbookStatus = PendingImportStatus.NotRequested;
            pending.AudiobookBooksToSearch = @"[""gr:missing""]";
            pendingImportService.DueResponses.Enqueue(new List<PendingAuthorImport> { pending });

            var commandQueue = new RecordingCommandQueue();
            var eventAggregator = new RecordingEventAggregator();
            var bookInfo = DispatchProxy.Create<IProvideBookInfo, BookInfoProxy>();
            ((BookInfoProxy)bookInfo).WorkException = new BookNotFoundException("gr:missing");
            var handler = BuildHandler(
                pendingImportService,
                commandQueue,
                eventAggregator,
                bookService: DispatchProxy.Create<IBookService, BookServiceProxy>(),
                bookInfo: bookInfo);

            handler.Execute(new ProcessPendingImportsCommand());

            Assert.That(pending.OverallStatus, Is.EqualTo(PendingImportStatus.Retrying));
            Assert.That(pending.LastError, Does.Contain("still being prepared"));
            Assert.That(pendingImportService.DeletedIds, Does.Not.Contain(pending.Id));
            Assert.That(commandQueue.Pushed.OfType<BookSearchCommand>(), Is.Empty);
            Assert.That(eventAggregator.Events.OfType<PendingAuthorImportFailedEvent>(), Is.Empty);
            Assert.That(((BookInfoProxy)bookInfo).RequestedWorkIds, Is.EqualTo(new[] { "gr:missing" }));
        }

        [Test]
        public void should_signal_every_missing_work_merged_for_the_same_author()
        {
            var pendingImportService = new StubPendingAuthorImportService();
            var pending = Pending(159);
            pending.AudiobookStatus = PendingImportStatus.Pending;
            pending.EbookStatus = PendingImportStatus.NotRequested;
            pending.AudiobookBooksToSearch = @"[""gr:first"",""gr:second""]";
            pendingImportService.DueResponses.Enqueue(new List<PendingAuthorImport> { pending });

            var bookInfo = DispatchProxy.Create<IProvideBookInfo, BookInfoProxy>();
            ((BookInfoProxy)bookInfo).WorkException = new BookNotFoundException("not ready");
            var commandQueue = new RecordingCommandQueue();
            var handler = BuildHandler(
                pendingImportService,
                commandQueue,
                new RecordingEventAggregator(),
                bookService: DispatchProxy.Create<IBookService, BookServiceProxy>(),
                bookInfo: bookInfo);

            handler.Execute(new ProcessPendingImportsCommand());

            Assert.That(((BookInfoProxy)bookInfo).RequestedWorkIds, Is.EqualTo(new[] { "gr:first", "gr:second" }));
            Assert.That(pending.OverallStatus, Is.EqualTo(PendingImportStatus.Retrying));
            Assert.That(pendingImportService.DeletedIds, Is.Empty);
            Assert.That(commandQueue.Pushed.OfType<BookSearchCommand>(), Is.Empty);
        }

        [Test]
        public void should_stop_retrying_and_preserve_the_declared_work_rescue_terminal_reason()
        {
            var pendingImportService = new StubPendingAuthorImportService();
            var pending = Pending(157);
            pending.AudiobookStatus = PendingImportStatus.NotRequested;
            pending.EbookStatus = PendingImportStatus.Pending;
            pending.EbookBooksToMonitor = "[\"hc:blocked\"]";
            pendingImportService.DueResponses.Enqueue(new List<PendingAuthorImport> { pending });
            var bookInfo = DispatchProxy.Create<IProvideBookInfo, BookInfoProxy>();
            ((BookInfoProxy)bookInfo).WorkException = new WorkRescueTerminalException(
                "hc:blocked",
                "Work rescue is blocked_safety_gate");
            var events = new RecordingEventAggregator();
            var handler = BuildHandler(
                pendingImportService,
                new RecordingCommandQueue(),
                events,
                bookService: DispatchProxy.Create<IBookService, BookServiceProxy>(),
                bookInfo: bookInfo);

            handler.Execute(new ProcessPendingImportsCommand());

            Assert.That(pending.OverallStatus, Is.EqualTo(PendingImportStatus.Failed));
            Assert.That(pending.LastError, Is.EqualTo("Work rescue is blocked_safety_gate"));
            Assert.That(pendingImportService.RetryReasons, Is.Empty);
            Assert.That(pendingImportService.DeletedIds, Is.Empty);
            Assert.That(events.Events.OfType<PendingAuthorImportFailedEvent>().Count(), Is.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_retry_when_requested_book_remains_unmonitored_without_a_declared_terminal(bool searchNow)
        {
            var pendingImportService = new StubPendingAuthorImportService();
            var pending = Pending(156);
            pending.AudiobookStatus = PendingImportStatus.Pending;
            pending.EbookStatus = PendingImportStatus.NotRequested;
            pending.AudiobookBooksToMonitor = @"[""gr:1001""]";
            pending.AudiobookBooksToSearch = searchNow ? @"[""gr:1001""]" : null;
            pendingImportService.DueResponses.Enqueue(new List<PendingAuthorImport> { pending });

            var author = new Author
            {
                Id = 42,
                Name = "Existing Author",
                AudiobookMonitored = true
            };
            var book = new Book
            {
                Id = 101,
                AuthorId = author.Id,
                Author = author,
                MediaType = BookMediaType.Audiobook,
                GoodreadsWorkId = "gr:1001"
            };
            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var bookServiceProxy = (BookServiceProxy)bookService;
            bookServiceProxy.Books = new List<Book> { book };
            bookServiceProxy.IgnoreMonitoringUpdates = true;
            var commandQueue = new RecordingCommandQueue();
            var eventAggregator = new RecordingEventAggregator();
            var handler = BuildHandler(
                pendingImportService,
                commandQueue,
                eventAggregator,
                author: author,
                bookService: bookService);

            handler.Execute(new ProcessPendingImportsCommand());

            Assert.That(pending.OverallStatus, Is.EqualTo(PendingImportStatus.Retrying));
            Assert.That(pending.LastError, Does.Contain("remained unmonitored"));
            Assert.That(pendingImportService.RetryReasons, Does.Contain(pending.LastError));
            Assert.That(pendingImportService.DeletedIds, Does.Not.Contain(pending.Id));
            Assert.That(commandQueue.Pushed.OfType<BookSearchCommand>(), Is.Empty);
            Assert.That(eventAggregator.Events.OfType<PendingAuthorImportFailedEvent>(), Is.Empty);
        }
        [TestCase(false, "ImportList:Selected ebooks")]
        [TestCase(true, "ImportList:Selected ebooks")]
        [TestCase(true, "Seerr")]
        public void queued_list_should_accept_one_matching_copy_and_preserve_its_requested_search(bool alreadyMonitored, string requestedBy)
        {
            var pendingService = new StubPendingAuthorImportService();
            var pending = Pending(180);
            pending.RequestedBy = requestedBy;
            pending.EbookBooksToMonitor = @"[""gr:1001""]";
            pending.EbookBooksToSearch = @"[""gr:1001""]";
            pending.EbookMonitorExistingMode = MonitorTypes.None;
            pendingService.DueResponses.Enqueue(new List<PendingAuthorImport> { pending });
            var author = new Author { Id = 42, EbookMonitored = false, AudiobookMonitored = false };
            var books = Enumerable.Range(101, 5).Select(id => new Book
            {
                Id = id, AuthorId = author.Id, Author = author, MediaType = BookMediaType.Ebook,
                GoodreadsWorkId = "gr:1001", EbookMonitored = alreadyMonitored && id == 105
            }).ToList();
            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)bookService).Books = books;
            var queue = new RecordingCommandQueue();
            BuildHandler(pendingService, queue, new RecordingEventAggregator(), author: author, bookService: bookService)
                .Execute(new ProcessPendingImportsCommand());
            Assert.Multiple(() =>
            {
                Assert.That(pendingService.DeletedIds, Does.Contain(pending.Id));
                Assert.That(books.Where(book => book.EbookMonitored).Select(book => book.Id), Is.EqualTo(new[] { alreadyMonitored ? 105 : 101 }));
                Assert.That(author.EbookMonitored, Is.False);
                Assert.That(queue.Pushed.OfType<BookSearchCommand>().Count(), Is.EqualTo(1));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void missing_list_edition_waits_only_when_the_list_identifies_it_as_audio(bool requiresEdition)
        {
            var pendingService = new StubPendingAuthorImportService();
            var pending = Pending(182);
            pending.RequestedBy = "ImportList:Edition request";
            pending.AudiobookStatus = PendingImportStatus.Pending;
            pending.EbookStatus = PendingImportStatus.NotRequested;
            pending.AudiobookBooksToMonitor = @"[""hc:1001""]";
            pending.AudiobookNarrations = Newtonsoft.Json.JsonConvert.SerializeObject(new[]
            {
                new RequestedNarration { WorkProviderId = "hc:1001", EditionProviderId = "hc:999", RequiresEdition = requiresEdition }
            });
            pendingService.DueResponses.Enqueue(new List<PendingAuthorImport> { pending });
            var author = new Author { Id = 42, AudiobookMonitored = false };
            var book = new Book { Id = 101, AuthorId = 42, HardcoverBookId = "hc:1001", MediaType = BookMediaType.Audiobook };
            var books = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)books).Books = new List<Book> { book };
            var metadata = DispatchProxy.Create<IProvideBookInfo, BookInfoProxy>();
            ((BookInfoProxy)metadata).WorkException = new BookNotFoundException("not ready");
            BuildHandler(pendingService, new RecordingCommandQueue(), new RecordingEventAggregator(), author: author, bookService: books, bookInfo: metadata)
                .Execute(new ProcessPendingImportsCommand());
            Assert.That(pendingService.DeletedIds.Contains(pending.Id), Is.EqualTo(!requiresEdition), pending.LastError);
            Assert.That(book.IsMonitored(), Is.EqualTo(!requiresEdition));
        }

        [TestCase(1)]
        [TestCase(3)]
        public void queued_print_or_ebook_hint_should_monitor_the_work_without_changing_its_audio_selection(int readingFormatId)
        {
            var pendingService = new StubPendingAuthorImportService();
            var pending = Pending(183);
            pending.RequestedBy = "ImportList:Shelf";
            pending.AudiobookStatus = PendingImportStatus.Pending;
            pending.EbookStatus = PendingImportStatus.NotRequested;
            pending.AudiobookBooksToMonitor = @"[""hc:1001""]";
            pending.AudiobookNarrations = @"[{""WorkProviderId"":""hc:1001"",""EditionProviderId"":""hc-ed:202""}]";
            pendingService.DueResponses.Enqueue(new List<PendingAuthorImport> { pending });
            var author = new Author { Id = 42, AudiobookMonitored = false };
            var selected = new Edition { Id = 302, BookId = 101, ReadingFormatId = 2, Narrator = "Selected narrator", Monitored = true, ManualAdd = true };
            var book = new Book { Id = 101, AuthorId = 42, HardcoverBookId = "hc:1001", MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition> { selected, new() { Id = 301, BookId = 101, HardcoverEditionId = "hc:202", ReadingFormatId = readingFormatId, Narrator = "Different narrator" } } };
            var books = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var proxy = (BookServiceProxy)books;
            proxy.Books = new List<Book> { book };
            proxy.WantedEditions = (_, _) => throw new InvalidOperationException("A non-audio hint must not request a narration");
            BuildHandler(pendingService, new RecordingCommandQueue(), new RecordingEventAggregator(), author: author, bookService: books)
                .Execute(new ProcessPendingImportsCommand());
            Assert.Multiple(() =>
            {
                Assert.That(pendingService.DeletedIds, Does.Contain(pending.Id), pending.LastError);
                Assert.That(book.IsMonitored(), Is.True);
                Assert.That(book.Editions.Single(edition => edition.Monitored), Is.SameAs(selected));
                Assert.That(selected.ManualAdd, Is.True);
                Assert.That(author.AudiobookMonitored, Is.False);
            });
        }

        [TestCase(false, "ImportList:Narrations")]
        [TestCase(true, "ImportList:Narrations")]
        [TestCase(true, "Seerr")]
        [TestCase(false, "UserInterface")]
        public void queued_narration_should_use_the_shared_writer_and_search_its_selected_copy(bool authorMonitored, string requestedBy)
        {
            var pendingService = new StubPendingAuthorImportService();
            var pending = Pending(181);
            pending.RequestedBy = requestedBy;
            pending.AudiobookStatus = PendingImportStatus.Pending;
            pending.EbookStatus = PendingImportStatus.NotRequested;
            pending.AudiobookBooksToMonitor = @"[""hc:1001""]";
            pending.AudiobookNarrations = @"[{""WorkProviderId"":""hc:1001"",""EditionProviderId"":""hc-ed:202"",""Search"":true}]";
            if (!requestedBy.StartsWith("ImportList:")) pending.ExplicitBookSearches = RequestedBookSearch.Write(new[]
                { new RequestedBookSearch { MediaType = BookMediaType.Audiobook, WorkProviderId = "hc:1001", EditionProviderId = "hc-ed:202" } });
            pendingService.DueResponses.Enqueue(new List<PendingAuthorImport> { pending });
            var author = new Author { Id = 42, AudiobookMonitored = authorMonitored };
            var first = new Book { Id = 101, AuthorId = 42, HardcoverBookId = "hc:1001", MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition> { new() { Id = 301, BookId = 101, HardcoverEditionId = "hc:202", ReadingFormatId = 2, Narrator = "Stephen Fry" } } };
            var second = new Book { Id = 102, AuthorId = 42, HardcoverBookId = "hc:1001", MediaType = BookMediaType.Audiobook };
            var books = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var proxy = (BookServiceProxy)books;
            proxy.Books = new List<Book> { first, second };
            var calls = 0;
            proxy.WantedEditions = (editions, candidates) =>
            {
                calls++;
                Assert.That(editions.Select(edition => edition.Id), Is.EqualTo(new[] { 301 }));
                Assert.That(candidates.Select(book => book.Id), Is.EquivalentTo(new[] { 101, 102 }));
                second.SetMonitored(true);
                return new Dictionary<int, Book> { [301] = second };
            };
            var queue = new RecordingCommandQueue();
            BuildHandler(pendingService, queue, new RecordingEventAggregator(), author: author, bookService: books)
                .Execute(new ProcessPendingImportsCommand());
            Assert.That(calls, Is.GreaterThan(0), pending.LastError);
            Assert.That(pendingService.DeletedIds, Does.Contain(pending.Id));
            Assert.That(queue.Pushed.OfType<BookSearchCommand>().Single().BookIds, Is.EqualTo(new[] { 102 }));
            Assert.That(first.IsMonitored(), Is.False);
            Assert.That(author.AudiobookMonitored, Is.EqualTo(authorMonitored));
            Assert.That(queue.Pushed.OfType<BookSearchCommand>().Single().Trigger,
                Is.EqualTo(authorMonitored || requestedBy.StartsWith("ImportList:") ? CommandTrigger.Unspecified : CommandTrigger.Manual));
        }

        [Test]
        public void active_author_list_searches_should_use_one_automatic_batch()
        {
            var pendingService = new StubPendingAuthorImportService();
            var pending = Pending(196);
            pending.RequestedBy = "ImportList:Selected books";
            pending.AudiobookStatus = PendingImportStatus.Pending;
            pending.EbookStatus = PendingImportStatus.NotRequested;
            pending.AudiobookBooksToMonitor = @"[""gr:1001"",""gr:1002""]";
            pending.AudiobookBooksToSearch = pending.AudiobookBooksToMonitor;
            pendingService.DueResponses.Enqueue(new List<PendingAuthorImport> { pending });
            var author = new Author { Id = 42, AudiobookMonitored = true };
            var books = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)books).Books = Enumerable.Range(1, 2).Select(id => new Book
            {
                Id = 100 + id, AuthorId = author.Id, Author = author, MediaType = BookMediaType.Audiobook,
                GoodreadsWorkId = $"gr:{1000 + id}"
            }).ToList();
            var queue = new RecordingCommandQueue();
            BuildHandler(pendingService, queue, new RecordingEventAggregator(), author: author, bookService: books)
                .Execute(new ProcessPendingImportsCommand());
            Assert.That(pendingService.DeletedIds, Does.Contain(pending.Id), pending.LastError);
            var search = queue.Pushed.OfType<BookSearchCommand>().Single();
            Assert.That(search.BookIds, Is.EquivalentTo(new[] { 101, 102 }));
            Assert.That(search.Trigger, Is.EqualTo(CommandTrigger.Unspecified));
            Assert.That(((BookServiceProxy)books).Books.All(book => book.AddOptions?.SearchForNewBook == false), Is.True);
        }

        [TestCase(false, true)]
        [TestCase(true, true)]
        [TestCase(false, false)]
        public void list_import_should_search_once_after_initial_monitoring_whether_scan_finishes_before_or_after_import(bool scanFirst, bool hasListedBook)
        {
            var pendingService = new StubPendingAuthorImportService();
            var pending = Pending(181);
            pending.RequestedBy = "ImportList:Selected ebooks";
            pending.SearchForMissingBooks = true;
            pending.EbookBooksToMonitor = hasListedBook ? @"[""gr:1001""]" : null;
            pending.EbookMonitorExistingMode = MonitorTypes.Existing;
            pendingService.DueResponses.Enqueue(new List<PendingAuthorImport> { pending });
            var author = new Author { Id = 42, EbookMonitored = true };
            var book = new Book { Id = 101, AuthorId = author.Id, Author = author, MediaType = BookMediaType.Ebook, GoodreadsWorkId = "gr:1001" };
            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)bookService).Books = new List<Book> { book };
            var queue = new RecordingCommandQueue();
            var initialMonitoringApplied = false;
            var scan = new AuthorScannedHandler(
                InterfaceProxy.Proxy<IBookMonitoredService>((method, args) => { initialMonitoringApplied = true; return null; }),
                InterfaceProxy.Proxy<IAuthorService>((method, args) => null), queue,
                InterfaceProxy.Proxy<IBookAddedService>((method, args) => null),
                new RecordingEventAggregator(), LogManager.GetCurrentClassLogger(), Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IBookService>((_, _) => throw new NotSupportedException()));
            void CompleteScan() => scan.Handle(new AuthorScanSkippedEvent(author, AuthorScanSkippedReason.NoFolderEvidence));
            BuildHandler(pendingService, queue, new RecordingEventAggregator(), author: author, bookService: bookService,
                configCaptured: config =>
                {
                    author.AddOptions = new AddAuthorOptions { EbookMonitor = config.EbookMonitorExistingMode, SearchForMissingBooks = config.SearchForMissingBooks == true };
                    if (scanFirst) CompleteScan();
                }).Execute(new ProcessPendingImportsCommand());
            Assert.That(queue.Pushed.OfType<MissingBookSearchCommand>().Count(), Is.EqualTo(scanFirst ? 1 : 0));
            Assert.That(queue.Pushed.OfType<RefreshAuthorCommand>().Count(), Is.EqualTo(scanFirst ? 0 : 1));
            CompleteScan();
            CompleteScan();
            Assert.Multiple(() =>
            {
                Assert.That(initialMonitoringApplied, Is.True);
                Assert.That(queue.Pushed.OfType<MissingBookSearchCommand>().Count(), Is.EqualTo(1));
                Assert.That(queue.Pushed.OfType<BookSearchCommand>(), Is.Empty);
                Assert.That(pendingService.DeletedIds, Does.Contain(pending.Id));
            });
        }

        [TestCase(2, null)]
        [TestCase(4, "Misplaced narrator credit")]
        public void catalog_edition_without_audio_narration_should_follow_normal_selection_after_pruning(int readingFormat, string narrator)
        {
            var pendingService = new StubPendingAuthorImportService();
            var pending = Pending(197);
            pending.RequestedBy = "ImportList:Shelf";
            pending.AudiobookStatus = PendingImportStatus.Pending;
            pending.EbookStatus = PendingImportStatus.NotRequested;
            pending.AudiobookBooksToMonitor = @"[""hc:1001""]";
            pending.AudiobookNarrations = @"[{""WorkProviderId"":""hc:1001"",""EditionProviderId"":""hc-ed:202"",""RequiresEdition"":true}]";
            pendingService.DueResponses.Enqueue(new List<PendingAuthorImport> { pending });
            var author = new Author { Id = 42, AudiobookMonitored = false };
            var book = new Book { Id = 101, AuthorId = author.Id, HardcoverBookId = "hc:1001", MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition> { new() { Id = 301, BookId = 101, ForeignEditionId = "hc:edition:303", ReadingFormatId = 2, Monitored = true } } };
            var books = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)books).Books = new List<Book> { book };
            ((BookServiceProxy)books).WantedEditions = (_, _) => throw new InvalidOperationException("A non-narration hint must not pin a selection");
            MonitoringConfig observed = null;
            BuildHandler(pendingService, new RecordingCommandQueue(), new RecordingEventAggregator(), author: author, bookService: books,
                configCaptured: config =>
                {
                    observed = config;
                    AuthorLibraryService.PrepareRequestedNarrations(new Author { Books = new List<Book>
                    {
                        new() { MediaType = BookMediaType.Audiobook, HardcoverBookId = "hc:1001", Editions = new List<Edition>
                        {
                            new() { ForeignEditionId = "hc:edition:202", HardcoverEditionId = "hc:202", ReadingFormatId = readingFormat, Narrator = narrator }
                        } }
                    } }, config);
                }).Execute(new ProcessPendingImportsCommand());
            Assert.Multiple(() =>
            {
                Assert.That(pendingService.DeletedIds, Does.Contain(pending.Id), pending.LastError);
                Assert.That(pendingService.RetryReasons, Is.Empty);
                Assert.That(observed.ProtectedNarrationIds, Is.Empty);
                Assert.That(observed.KnownParentEditionRequests, Is.Empty);
                Assert.That(book.IsMonitored(), Is.True);
                Assert.That(book.Editions.Single().ManualAdd, Is.False);
            });
        }

        [Test]
        public void missing_explicit_narration_should_fail_visibly_without_retry_or_changing_an_existing_pin()
        {
            var pendingService = new StubPendingAuthorImportService();
            var pending = Pending(194);
            pending.RequestedBy = "ImportList:Shelf";
            pending.AudiobookStatus = PendingImportStatus.Retrying;
            pending.EbookStatus = PendingImportStatus.NotRequested;
            pending.AudiobookBooksToMonitor = @"[""hc:1001""]";
            pending.AudiobookNarrations = @"[{""WorkProviderId"":""hc:1001"",""EditionProviderId"":""hc-ed:999"",""RequiresEdition"":true,""Search"":true}]";
            pendingService.DueResponses.Enqueue(new List<PendingAuthorImport> { pending });
            var author = new Author { Id = 42, AudiobookMonitored = false };
            var selected = new Edition { Id = 302, BookId = 101, ReadingFormatId = 2, Narrator = "Different Narrator", Monitored = true, ManualAdd = true };
            var book = new Book { Id = 101, AuthorId = 42, HardcoverBookId = "hc:1001", MediaType = BookMediaType.Audiobook, AnyEditionOk = false,
                Editions = new List<Edition> { selected } };
            var books = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)books).Books = new List<Book> { book };
            ((BookServiceProxy)books).WantedEditions = (_, _) => throw new InvalidOperationException("Missing narration must not pin a substitute");
            var calls = 0;
            var queue = new RecordingCommandQueue();
            BuildHandler(pendingService, queue, new RecordingEventAggregator(), author: author, bookService: books,
                configCaptured: config =>
                {
                    calls++;
                    AuthorLibraryService.PrepareRequestedNarrations(new Author { Books = new List<Book> { book } }, config);
                }).Execute(new ProcessPendingImportsCommand());
            Assert.Multiple(() =>
            {
                Assert.That(pending.OverallStatus, Is.EqualTo(PendingImportStatus.Failed));
                Assert.That(pending.LastError, Does.Contain("hc-ed:999").And.Contain("profile-filtered catalog"));
                Assert.That(pendingService.RetryReasons, Is.Empty);
                Assert.That(pendingService.DeletedIds, Is.Empty);
                Assert.That(calls, Is.EqualTo(1));
                Assert.That(book.IsMonitored(), Is.True);
                Assert.That(book.Editions.Single(edition => edition.Monitored), Is.SameAs(selected));
                Assert.That(selected.ManualAdd, Is.True);
                Assert.That(queue.Pushed.OfType<BookSearchCommand>(), Is.Empty);
            });
        }

        [TestCase(false, 1)]
        [TestCase(true, 1)]
        [TestCase(false, 5)]
        public void queued_cast_should_fulfill_from_an_alternate_edition_without_an_exact_first_gate(bool exactPresent, int requestCount)
        {
            var pendingService = new StubPendingAuthorImportService();
            var pending = Pending(201);
            pending.RequestedBy = "ImportList:Shelf";
            pending.AudiobookStatus = PendingImportStatus.Pending;
            pending.EbookStatus = PendingImportStatus.NotRequested;
            pending.AudiobookNarrations = Newtonsoft.Json.JsonConvert.SerializeObject(Enumerable.Range(0, requestCount).Select(index =>
                new RequestedNarration { WorkProviderId = "hc:1001", EditionProviderId = index == 0 ? "hc-ed:999" : $"hc-ed:{990 + index}", RequiresEdition = true,
                    NarratorNames = new List<string> { "Jim Dale", "Jane Smith" }, Search = true }));
            pendingService.DueResponses.Enqueue(new List<PendingAuthorImport> { pending });
            var author = new Author { Id = 42, AudiobookMonitored = false };
            var alternate = new Edition { Id = 301, BookId = 101, ForeignEditionId = "hc:edition:203", HardcoverEditionId = "hc:203",
                ReadingFormatId = 2, NarratorNames = new List<string> { "Jane Smith", "Jim Dale" } };
            var book = new Book { Id = 101, AuthorId = 42, HardcoverBookId = "hc:1001", MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition> { alternate } };
            if (exactPresent) book.Editions.Add(new Edition { Id = 305, BookId = 101, ForeignEditionId = "hc:edition:999", HardcoverEditionId = "hc:999",
                ReadingFormatId = 2, NarratorNames = new List<string> { "Jim Dale", "Jane Smith" } });
            var books = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var proxy = (BookServiceProxy)books;
            proxy.Books = new List<Book> { book };
            var writerCalls = 0;
            proxy.WantedEditions = (editions, _) =>
            {
                writerCalls++;
                Assert.That(editions.Select(edition => edition.Id), Is.EqualTo(new[] { alternate.Id }));
                alternate.Monitored = true;
                alternate.ManualAdd = true;
                return new Dictionary<int, Book> { [alternate.Id] = book };
            };
            var queue = new RecordingCommandQueue();
            BuildHandler(pendingService, queue, new RecordingEventAggregator(), author: author, bookService: books,
                configCaptured: config => AuthorLibraryService.PrepareRequestedNarrations(new Author { Books = new List<Book> { book } }, config))
                .Execute(new ProcessPendingImportsCommand());

            Assert.Multiple(() =>
            {
                Assert.That(writerCalls, Is.EqualTo(1), pending.LastError);
                Assert.That(proxy.CatalogLoads, Is.EqualTo(1));
                Assert.That(pendingService.DeletedIds, Does.Contain(pending.Id));
                Assert.That(pendingService.RetryReasons, Is.Empty);
                Assert.That(book.IsMonitored(), Is.True);
                Assert.That(queue.Pushed.OfType<BookSearchCommand>().Single().BookIds, Is.EqualTo(new[] { book.Id }));
            });
        }

        [Test]
        public void missing_narrated_parent_should_use_work_recovery_instead_of_terminal_unavailable()
        {
            var pendingService = new StubPendingAuthorImportService();
            var pending = Pending(202);
            pending.RequestedBy = "ImportList:Shelf";
            pending.AudiobookStatus = PendingImportStatus.Pending;
            pending.EbookStatus = PendingImportStatus.NotRequested;
            pending.AudiobookNarrations = Newtonsoft.Json.JsonConvert.SerializeObject(new[]
            {
                new RequestedNarration { WorkProviderId = "hc:1001", EditionProviderId = "hc-ed:999", RequiresEdition = true,
                    NarratorNames = new List<string> { "Jim Dale" } }
            });
            pendingService.DueResponses.Enqueue(new List<PendingAuthorImport> { pending });
            var author = new Author { Id = 42 };
            var unrelated = new Book { Id = 101, AuthorId = author.Id, HardcoverBookId = "hc:1002", MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition> { new() { Id = 301, BookId = 101, ForeignEditionId = "hc:edition:999", ReadingFormatId = 2, Narrator = "Jim Dale" } } };
            var books = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)books).Books = new List<Book> { unrelated };
            var metadata = DispatchProxy.Create<IProvideBookInfo, BookInfoProxy>();
            var metadataProxy = (BookInfoProxy)metadata;
            metadataProxy.WorkException = new BookNotFoundException("not ready");
            MonitoringConfig observed = null;
            BuildHandler(pendingService, new RecordingCommandQueue(), new RecordingEventAggregator(), author: author, bookService: books, bookInfo: metadata,
                configCaptured: config =>
                {
                    observed = config;
                    AuthorLibraryService.PrepareRequestedNarrations(new Author { Books = new List<Book> { unrelated } }, config);
                }).Execute(new ProcessPendingImportsCommand());

            Assert.Multiple(() =>
            {
                Assert.That(metadataProxy.RequestedWorkIds, Is.EqualTo(new[] { "hc:1001" }));
                Assert.That(pending.OverallStatus, Is.EqualTo(PendingImportStatus.Retrying));
                Assert.That(pendingService.RetryReasons, Has.Count.EqualTo(1));
                Assert.That(observed.KnownParentEditionRequests, Is.Empty);
                Assert.That(pendingService.DeletedIds, Is.Empty);
            });
        }

        [Test]
        public void authoritative_cast_learned_during_import_should_survive_a_pending_retry()
        {
            var pendingService = new StubPendingAuthorImportService();
            var pending = Pending(203);
            pending.AudiobookStatus = PendingImportStatus.Pending;
            pending.EbookStatus = PendingImportStatus.NotRequested;
            pending.AudiobookNarrations = @"[{""WorkProviderId"":""hc:1001"",""EditionProviderId"":""hc-ed:999"",""RequiresEdition"":true}]";
            pendingService.DueResponses.Enqueue(new List<PendingAuthorImport> { pending });
            pending.AudiobookBooksToMonitor = @"[""hc:1001"",""hc:1002""]";
            var remote = new Book { Id = 101, AuthorId = 42, HardcoverBookId = "hc:1001", MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition> { new() { Id = 301, BookId = 101, ForeignEditionId = "hc:edition:999", ReadingFormatId = 2,
                    NarratorNames = new List<string> { "Jim Dale", "Jane Smith" } } } };
            var metadata = DispatchProxy.Create<IProvideBookInfo, BookInfoProxy>();
            ((BookInfoProxy)metadata).WorkException = new BookNotFoundException("not ready");
            var books = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var proxy = (BookServiceProxy)books;
            proxy.Books = new List<Book> { remote };
            proxy.WantedEditions = (editions, _) =>
            {
                var selected = editions.Single();
                selected.Monitored = true;
                selected.ManualAdd = true;
                return new Dictionary<int, Book> { [selected.Id] = remote };
            };
            BuildHandler(pendingService, new RecordingCommandQueue(), new RecordingEventAggregator(), bookService: books, bookInfo: metadata,
                configCaptured: config => AuthorLibraryService.PrepareRequestedNarrations(new Author { Books = new List<Book> { remote } }, config))
                .Execute(new ProcessPendingImportsCommand());

            var saved = Newtonsoft.Json.JsonConvert.DeserializeObject<List<RequestedNarration>>(pending.AudiobookNarrations);
            Assert.That(pending.OverallStatus, Is.EqualTo(PendingImportStatus.Retrying));
            Assert.That(saved, Is.Empty);
            Assert.That(remote.Editions.Single().NarratorNames, Is.EqualTo(new[] { "Jim Dale", "Jane Smith" }));
            Assert.That(((BookInfoProxy)metadata).RequestedWorkIds, Is.EqualTo(new[] { "hc:1002" }));
            Assert.That(pendingService.RetryReasons, Has.Count.EqualTo(1));
            Assert.That(remote.Editions.Single().ManualAdd, Is.True);
            Assert.That(pendingService.DeletedIds, Is.Empty);
        }

        [TestCase("wrong-author")]
        [TestCase("wrong-work")]
        [TestCase("wrong-media")]
        [TestCase("partial-cast")]
        [TestCase("wrong-format")]
        public void pending_cast_resolution_should_reject_catalog_evidence_outside_its_identity_boundary(string mismatch)
        {
            var pendingService = new StubPendingAuthorImportService();
            var pending = Pending(204);
            pending.AudiobookStatus = PendingImportStatus.Pending;
            pending.EbookStatus = PendingImportStatus.NotRequested;
            pending.AudiobookNarrations = Newtonsoft.Json.JsonConvert.SerializeObject(new[]
            {
                new RequestedNarration { WorkProviderId = "hc:1001", EditionProviderId = "hc-ed:999", RequiresEdition = true,
                    NarratorNames = new List<string> { "Jim Dale", "Jane Smith" }, Search = true }
            });
            pendingService.DueResponses.Enqueue(new List<PendingAuthorImport> { pending });
            var parent = new Book { Id = 101, AuthorId = 42, HardcoverBookId = "hc:1001", MediaType = BookMediaType.Audiobook, Editions = new List<Edition>() };
            var candidate = new Book { Id = 102, AuthorId = mismatch == "wrong-author" ? 43 : 42,
                HardcoverBookId = mismatch == "wrong-work" ? "hc:1002" : "hc:1001",
                MediaType = mismatch == "wrong-media" ? BookMediaType.Ebook : BookMediaType.Audiobook,
                Editions = new List<Edition> { new() { Id = 301, BookId = 102, ForeignEditionId = "hc:edition:203",
                    ReadingFormatId = mismatch == "wrong-format" ? 3 : 2,
                    NarratorNames = mismatch == "partial-cast" ? new List<string> { "Jim Dale" } : new List<string> { "Jim Dale", "Jane Smith" } } } };
            var books = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var proxy = (BookServiceProxy)books;
            proxy.Books = new List<Book> { parent, candidate };
            proxy.WantedEditions = (_, _) => throw new InvalidOperationException("Incompatible catalog evidence must not select a narration");
            var queue = new RecordingCommandQueue();
            BuildHandler(pendingService, queue, new RecordingEventAggregator(), bookService: books,
                configCaptured: config => AuthorLibraryService.PrepareRequestedNarrations(new Author { Books = new List<Book> { parent } }, config))
                .Execute(new ProcessPendingImportsCommand());

            Assert.Multiple(() =>
            {
                Assert.That(pending.OverallStatus, Is.EqualTo(PendingImportStatus.Failed));
                Assert.That(pendingService.RetryReasons, Is.Empty);
                Assert.That(pendingService.DeletedIds, Is.Empty);
                Assert.That(candidate.IsMonitored(), Is.False);
                Assert.That(queue.Pushed.OfType<BookSearchCommand>(), Is.Empty);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void second_author_import_evidence_must_apply_to_existing_narration_targets(bool narrated)
        {
            var pendingService = new StubPendingAuthorImportService();
            var pending = Pending(205);
            pending.AudiobookStatus = PendingImportStatus.Pending;
            pending.EbookStatus = PendingImportStatus.NotRequested;
            pending.AudiobookNarrations = @"[{""WorkProviderId"":""hc:1001"",""EditionProviderId"":""hc-ed:999"",""RequiresEdition"":true,""Search"":true}]";
            pendingService.DueResponses.Enqueue(new List<PendingAuthorImport> { pending });
            var alternative = new Edition { Id = 301, BookId = 101, ForeignEditionId = "hc:edition:203", ReadingFormatId = 2,
                Narrator = narrated ? "Jim Dale" : "Other Narrator", Monitored = !narrated };
            var local = new Book { Id = 101, AuthorId = 42, HardcoverBookId = "hc:1001", MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition> { alternative } };
            var remote = new Book { HardcoverBookId = "hc:1001", MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition> { new() { ForeignEditionId = "hc:edition:999", ReadingFormatId = narrated ? 2 : 1,
                    Narrator = narrated ? "Jim Dale" : null } } };
            var books = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var proxy = (BookServiceProxy)books;
            var writerCalls = 0;
            proxy.WantedEditions = (editions, _) =>
            {
                writerCalls++;
                Assert.That(editions.Select(edition => edition.Id), Is.EqualTo(new[] { alternative.Id }));
                alternative.Monitored = true;
                alternative.ManualAdd = true;
                return new Dictionary<int, Book> { [alternative.Id] = local };
            };
            var metadata = DispatchProxy.Create<IProvideBookInfo, BookInfoProxy>();
            var imports = 0;
            var queue = new RecordingCommandQueue();
            BuildHandler(pendingService, queue, new RecordingEventAggregator(), bookService: books, bookInfo: metadata,
                configCaptured: config =>
                {
                    imports++;
                    AuthorLibraryService.PrepareRequestedNarrations(new Author { Books = imports == 1 ? new List<Book>() : new List<Book> { remote } }, config);
                    if (imports == 2) proxy.Books = new List<Book> { local };
                }).Execute(new ProcessPendingImportsCommand());

            Assert.Multiple(() =>
            {
                Assert.That(imports, Is.EqualTo(2));
                Assert.That(((BookInfoProxy)metadata).RequestedWorkIds, Is.EqualTo(new[] { "hc:1001" }));
                Assert.That(writerCalls, Is.EqualTo(narrated ? 1 : 0), pending.LastError);
                Assert.That(pendingService.DeletedIds, Does.Contain(pending.Id));
                Assert.That(pendingService.RetryReasons, Is.Empty);
                Assert.That(local.IsMonitored(), Is.True);
                Assert.That(queue.Pushed.OfType<BookSearchCommand>().SelectMany(command => command.BookIds), Is.EqualTo(new[] { local.Id }));
            });
        }

    }
}
