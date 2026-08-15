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

            public Task<int> EnqueueAsync(string providerId, MonitoringConfig config, string sourceApplication) => throw new NotImplementedException();
            public List<PendingAuthorImport> GetAll() => throw new NotImplementedException();
            public List<PendingAuthorImport> GetDueForProcessing(int limit = 10) => DueResponses.Count > 0 ? DueResponses.Dequeue() : new List<PendingAuthorImport>();
            public PendingAuthorImport GetByProviderId(string providerId) => throw new NotImplementedException();

            public void UpdateStatus(PendingAuthorImport item, PendingImportStatus status, string error)
            {
                item.OverallStatus = status;
                item.LastError = error;
            }

            public void ScheduleRetry(PendingAuthorImport item, string error) => throw new NotImplementedException();
            public void Cancel(int id) => throw new NotImplementedException();
            public void RetryNow(int id) => throw new NotImplementedException();
            public void Delete(int id) => DeletedIds.Add(id);
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
                    case nameof(IAuthorService.PromoteMediaTypeMonitoringToSelected):
                        var mediaType = (string)args[1];
                        if (string.Equals(mediaType, "audiobook", StringComparison.OrdinalIgnoreCase) &&
                            (CurrentAuthor.AudiobookMonitorExisting ?? 0) <= 0)
                        {
                            CurrentAuthor.AudiobookMonitorExisting = 2;
                        }
                        else if (string.Equals(mediaType, "ebook", StringComparison.OrdinalIgnoreCase) &&
                                 (CurrentAuthor.EbookMonitorExisting ?? 0) <= 0)
                        {
                            CurrentAuthor.EbookMonitorExisting = 2;
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

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case nameof(IBookService.GetBooksByAuthor):
                        return Books;
                    case nameof(IBookService.GetBooks):
                        var ids = ((IEnumerable<int>)args[0]).ToHashSet();
                        return Books.Where(book => ids.Contains(book.Id)).ToList();
                    case nameof(IBookService.SetMonitoredForMediaType):
                        if (!IgnoreMonitoringUpdates)
                        {
                            var monitoredIds = ((IEnumerable<int>)args[0]).ToHashSet();
                            var mediaType = (string)args[1];
                            var monitored = (bool)args[2];
                            foreach (var book in Books.Where(book => monitoredIds.Contains(book.Id)))
                            {
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

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IAuthorLibraryService.AddAuthorAsync))
                {
                    if (Terminal != null)
                    {
                        return Task.FromException<Author>(Terminal);
                    }

                    return Task.FromResult(AddedAuthor);
                }

                throw new NotImplementedException($"Test proxy does not implement IAuthorLibraryService.{targetMethod?.Name}");
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
            IBookService bookService = null)
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

            return new ProcessPendingImportsCommandHandler(
                pendingImportService,
                authorLibraryService,
                authorService,
                bookService,
                commandQueue,
                eventAggregator,
                LogManager.GetCurrentClassLogger());
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
            Assert.That(author.AudiobookMonitorExisting, Is.EqualTo(searchAudiobook ? 2 : null));
            Assert.That(author.EbookMonitorExisting, Is.EqualTo(searchEbook ? 2 : null));
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
        public void should_repair_disabled_author_media_monitoring_before_exact_search()
        {
            var pendingImportService = new StubPendingAuthorImportService();
            var pending = Pending(152);
            pending.AudiobookStatus = PendingImportStatus.Pending;
            pending.EbookStatus = PendingImportStatus.NotRequested;
            pending.AudiobookMonitorExisting = 2;
            pending.AudiobookBooksToSearch = @"[""gr:1001""]";
            pendingImportService.DueResponses.Enqueue(new List<PendingAuthorImport> { pending });

            var author = new Author
            {
                Id = 42,
                Name = "Existing Author",
                AudiobookMonitorExisting = 0,
                AudiobookMonitorFuture = false
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
            var commandQueue = new RecordingCommandQueue();
            var handler = BuildHandler(
                pendingImportService,
                commandQueue,
                new RecordingEventAggregator(),
                author: author,
                bookService: bookService);

            handler.Execute(new ProcessPendingImportsCommand());

            Assert.That(author.AudiobookMonitorExisting, Is.EqualTo(2));
            Assert.That(book.IsMonitoredWithAuthor(), Is.True);
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
            pending.AudiobookMonitorExisting = 2;
            pending.AudiobookBooksToSearch = @"[""gr:1001""]";
            pendingImportService.DueResponses.Enqueue(new List<PendingAuthorImport> { pending });

            var author = new Author
            {
                Id = 42,
                Name = "Existing Author",
                AudiobookMonitorExisting = 2
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
        public void should_fail_terminally_when_requested_book_is_absent_from_imported_catalog()
        {
            var pendingImportService = new StubPendingAuthorImportService();
            var pending = Pending(155);
            pending.AudiobookStatus = PendingImportStatus.Pending;
            pending.EbookStatus = PendingImportStatus.NotRequested;
            pending.AudiobookBooksToSearch = @"[""gr:missing""]";
            pendingImportService.DueResponses.Enqueue(new List<PendingAuthorImport> { pending });

            var commandQueue = new RecordingCommandQueue();
            var eventAggregator = new RecordingEventAggregator();
            var handler = BuildHandler(
                pendingImportService,
                commandQueue,
                eventAggregator,
                bookService: DispatchProxy.Create<IBookService, BookServiceProxy>());

            handler.Execute(new ProcessPendingImportsCommand());

            Assert.That(pending.OverallStatus, Is.EqualTo(PendingImportStatus.Failed));
            Assert.That(pending.LastError, Does.Contain("was not present in the imported author catalog"));
            Assert.That(pendingImportService.DeletedIds, Does.Not.Contain(pending.Id));
            Assert.That(commandQueue.Pushed.OfType<BookSearchCommand>(), Is.Empty);
            Assert.That(eventAggregator.Events.OfType<PendingAuthorImportFailedEvent>().Count(), Is.EqualTo(1));
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
                AudiobookMonitorExisting = 2
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
