using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NLog;
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
    public class ProcessPendingImportsCommandHandlerFixture
    {
        private sealed class StubPendingAuthorImportService : IPendingAuthorImportService
        {
            public Queue<List<PendingAuthorImport>> DueResponses { get; } = new();
            public int CleanupCount { get; private set; }
            public List<int> DeletedIds { get; } = new();
            public List<string> RetryReasons { get; } = new();
            public bool DeleteIfUnchangedResult { get; set; } = true;
            public PendingAuthorImport CurrentByProviderId { get; set; }

            public Task<int> EnqueueAsync(string providerId, MonitoringConfig config, string sourceApplication) => throw new NotImplementedException();
            public List<PendingAuthorImport> GetAll() => throw new NotImplementedException();
            public List<PendingAuthorImport> GetDueForProcessing(int limit = 10) => DueResponses.Count > 0 ? DueResponses.Dequeue() : new List<PendingAuthorImport>();
            public PendingAuthorImport GetByProviderId(string providerId) => CurrentByProviderId;

            public void UpdateStatus(PendingAuthorImport item, PendingImportStatus status, string error)
            {
                item.OverallStatus = status;
                item.LastError = error;
            }

            public void ScheduleRetry(PendingAuthorImport item, string error)
            {
                item.OverallStatus = PendingImportStatus.Retrying;
                item.LastError = error;
                RetryReasons.Add(error);
            }
            public void Cancel(int id) => throw new NotImplementedException();
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

        private class AuthorServiceProxy : DispatchProxy
        {
            public Author CurrentAuthor { get; set; } = new Author { Id = 42, Name = "Existing Author" };

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case nameof(IAuthorService.FindByProviderId):
                        return CurrentAuthor;
                    case nameof(IAuthorService.GetAuthor):
                        return CurrentAuthor != null && CurrentAuthor.Id == (int)args[0] ? CurrentAuthor : null;
                    case nameof(IAuthorService.UpdateAuthor):
                        CurrentAuthor = (Author)args[0];
                        return CurrentAuthor;
                    case nameof(IAuthorService.EnsureMediaTypeMonitoring):
                        var mediaType = (string)args[1];
                        if (string.Equals(mediaType, "audiobook", StringComparison.OrdinalIgnoreCase) &&
                            CurrentAuthor.AudiobookMonitored != true)
                        {
                            CurrentAuthor.AudiobookMonitored = true;
                        }
                        else if (string.Equals(mediaType, "ebook", StringComparison.OrdinalIgnoreCase) &&
                                 CurrentAuthor.EbookMonitored != true)
                        {
                            CurrentAuthor.EbookMonitored = true;
                        }

                        CurrentAuthor.Monitored = CurrentAuthor.IsMonitoredFromMediaSettings();
                        return null;
                    default:
                        throw new NotImplementedException($"Test proxy does not implement IAuthorService.{targetMethod?.Name}");
                }
            }
        }

        private class BookServiceProxy : DispatchProxy
        {
            public List<Book> Books { get; set; } = new();
            public bool IgnoreMonitoringUpdates { get; set; }
            public bool ReplaceRowsOnMonitoringUpdate { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case nameof(IBookService.GetBooksByAuthor):
                        return Books;
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
                    case nameof(IBookService.SetMonitoredForMediaType):
                        if (!IgnoreMonitoringUpdates)
                        {
                            var monitoredIds = ((IEnumerable<int>)args[0]).ToHashSet();
                            var mediaType = (string)args[1];
                            var monitored = (bool)args[2];
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

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IAuthorLibraryService.AddAuthorAsync))
                {
                    LastConfig = (MonitoringConfig)args[1];
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

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IProvideBookInfo.GetWorkInfo))
                {
                    RequestedWorkIds.Add((string)args[0]);
                    if (WorkException != null)
                    {
                        throw WorkException;
                    }

                    return null;
                }

                throw new NotImplementedException($"Test proxy does not implement IProvideBookInfo.{targetMethod?.Name}");
            }
        }
        private sealed class RecordingCommandQueue : IManageCommandQueue
        {
            public List<Command> Pushed { get; } = new();

            public List<CommandModel> PushMany<TCommand>(List<TCommand> commands) where TCommand : Command => throw new NotImplementedException();

            public CommandModel Push<TCommand>(TCommand command, CommandPriority priority = CommandPriority.Normal, CommandTrigger trigger = CommandTrigger.Unspecified) where TCommand : Command
            {
                Pushed.Add(command);
                return new CommandModel { Name = command?.Name };
            }

            public CommandModel Push(string commandName, DateTime? lastExecutionTime, DateTime? lastStartTime, CommandPriority priority = CommandPriority.Normal, CommandTrigger trigger = CommandTrigger.Unspecified) => throw new NotImplementedException();
            public IEnumerable<CommandModel> Queue(CancellationToken cancellationToken) => throw new NotImplementedException();
            public List<CommandModel> All() => throw new NotImplementedException();
            public CommandModel Get(int id) => throw new NotImplementedException();
            public List<CommandModel> GetStarted() => throw new NotImplementedException();
            public void SetMessage(CommandModel command, string message) => throw new NotImplementedException();
            public void TouchProgress(CommandModel command) => throw new NotImplementedException();
            public void SetResult(CommandModel command, CommandResult result) => throw new NotImplementedException();
            public void Start(CommandModel command) => throw new NotImplementedException();
            public void Complete(CommandModel command, string message) => throw new NotImplementedException();
            public void Fail(CommandModel command, string message, Exception e) => throw new NotImplementedException();
            public void Requeue() => throw new NotImplementedException();
            public void Cancel(int id) => throw new NotImplementedException();
            public void Pause(int id) => throw new NotImplementedException();
            public void Resume(int id) => throw new NotImplementedException();
            public void CleanCommands() => throw new NotImplementedException();
            public CancellationToken GetCancellationToken(int commandId) => throw new NotImplementedException();
            public void RegisterCancellationToken(int commandId, CancellationTokenSource cancellationTokenSource) => throw new NotImplementedException();
            public void UnregisterCancellationToken(int commandId) => throw new NotImplementedException();
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
            IProvideBookInfo bookInfo = null)
        {
            author ??= new Author { Id = 42, Name = "Existing Author" };

            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            ((AuthorServiceProxy)authorService).CurrentAuthor = author;

            var authorLibraryService = DispatchProxy.Create<IAuthorLibraryService, AuthorLibraryProxy>();
            ((AuthorLibraryProxy)authorLibraryService).Terminal = terminal;
            ((AuthorLibraryProxy)authorLibraryService).AddedAuthor = author;

            if (bookService == null)
            {
                bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            }

            bookInfo ??= DispatchProxy.Create<IProvideBookInfo, BookInfoProxy>();

            return new ProcessPendingImportsCommandHandler(
                pendingImportService,
                authorLibraryService,
                authorService,
                bookService,
                bookInfo,
                commandQueue,
                eventAggregator,
                LogManager.GetCurrentClassLogger());
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
            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            ((AuthorServiceProxy)authorService).CurrentAuthor = ((AuthorLibraryProxy)authorLibrary).AddedAuthor;
            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var bookInfo = DispatchProxy.Create<IProvideBookInfo, BookInfoProxy>();
            var handler = new ProcessPendingImportsCommandHandler(
                pendingImportService,
                authorLibrary,
                authorService,
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
            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            ((AuthorServiceProxy)authorService).CurrentAuthor = ((AuthorLibraryProxy)authorLibrary).AddedAuthor;
            var handler = new ProcessPendingImportsCommandHandler(
                pendingImportService,
                authorLibrary,
                authorService,
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
            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            ((AuthorServiceProxy)authorService).CurrentAuthor = ((AuthorLibraryProxy)authorLibrary).AddedAuthor;
            var handler = new ProcessPendingImportsCommandHandler(
                pendingImportService,
                authorLibrary,
                authorService,
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
            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            ((AuthorServiceProxy)authorService).CurrentAuthor = ((AuthorLibraryProxy)authorLibrary).AddedAuthor;
            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var handler = new ProcessPendingImportsCommandHandler(
                pendingImportService,
                authorLibrary,
                authorService,
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

            var search = commandQueue.Pushed.OfType<BookSearchCommand>().Single();
            Assert.That(search.BookIds, Is.EquivalentTo(expectedBookIds));
            Assert.That(books.Where(book => expectedBookIds.Contains(book.Id)).All(book => book.IsMonitoredWithAuthor()), Is.True);
            Assert.That(author.AudiobookMonitored, Is.EqualTo(searchAudiobook ? true : (bool?)null));
            Assert.That(author.EbookMonitored, Is.EqualTo(searchEbook ? true : (bool?)null));
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
            var handler = BuildHandler(
                pendingImportService,
                commandQueue,
                eventAggregator,
                author: new Author { Id = 42, Name = "Now Available" },
                bookService: bookService);

            handler.Execute(new ProcessPendingImportsCommand());

            var search = commandQueue.Pushed.OfType<BookSearchCommand>().Single();
            Assert.That(search.BookIds, Is.EquivalentTo(new[] { 101, 202 }));
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
            pendingImportService.DueResponses.Enqueue(new List<PendingAuthorImport> { pending });
            pendingImportService.CurrentByProviderId = new PendingAuthorImport
            {
                Id = pending.Id,
                ProviderId = pending.ProviderId,
                Version = 5,
                OverallStatus = PendingImportStatus.Pending,
                EbookStatus = PendingImportStatus.Pending,
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
        public void should_repair_disabled_author_media_monitoring_before_exact_search()
        {
            var pendingImportService = new StubPendingAuthorImportService();
            var pending = Pending(152);
            pending.AudiobookStatus = PendingImportStatus.Pending;
            pending.EbookStatus = PendingImportStatus.NotRequested;
            pending.AudiobookMonitored = true;
            pending.AudiobookBooksToSearch = @"[""gr:1001""]";
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

            Assert.That(author.AudiobookMonitored, Is.True);
            Assert.That(((BookServiceProxy)bookService).Books.Single().IsMonitoredWithAuthor(), Is.True);
            Assert.That(commandQueue.Pushed.OfType<BookSearchCommand>().Single().BookIds, Is.EqualTo(new[] { book.Id }));
            Assert.That(pendingImportService.DeletedIds, Does.Contain(pending.Id));
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

        [Test]
        public void should_fail_terminally_when_requested_book_remains_unmonitored()
        {
            var pendingImportService = new StubPendingAuthorImportService();
            var pending = Pending(156);
            pending.AudiobookStatus = PendingImportStatus.Pending;
            pending.EbookStatus = PendingImportStatus.NotRequested;
            pending.AudiobookBooksToSearch = @"[""gr:1001""]";
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

            Assert.That(pending.OverallStatus, Is.EqualTo(PendingImportStatus.Failed));
            Assert.That(pending.LastError, Does.Contain("remained unmonitored"));
            Assert.That(pendingImportService.DeletedIds, Does.Not.Contain(pending.Id));
            Assert.That(commandQueue.Pushed.OfType<BookSearchCommand>(), Is.Empty);
            Assert.That(eventAggregator.Events.OfType<PendingAuthorImportFailedEvent>().Count(), Is.EqualTo(1));
        }
    }
}
