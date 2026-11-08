using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MediaFiles.Events;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class BookAddedServiceMonitoringFixture
    {
        [Test]
        public void newly_added_search_should_respect_each_books_media_side_author_gate()
        {
            var author = new Author
            {
                Id = 7,
                AudiobookMonitored = false,
                EbookMonitored = true,
                AddOptions = null
            };
            var audiobook = BuildBook(11, BookMediaType.Audiobook, author, monitored: true);
            var ebook = BuildBook(12, BookMediaType.Ebook, author, monitored: true);
            var subject = BuildSubject(new List<Book> { audiobook, ebook }, out var queue, out _);

            subject.Handle(new BookInfoRefreshedEvent(author, new List<Book> { audiobook, ebook }, new List<Book>(), new List<Book>()));
            subject.SearchForRecentlyAdded(author.Id);

            var command = queue.Pushed.OfType<BookSearchCommand>().Single();
            Assert.That(command.BookIds, Is.EqualTo(new[] { ebook.Id }));
        }

        [Test]
        public void saved_book_search_flags_should_queue_each_exact_target_as_an_automatic_search()
        {
            var author = new Author
            {
                Id = 7,
                AudiobookMonitored = false,
                EbookMonitored = true
            };
            var audiobook = BuildBook(11, BookMediaType.Audiobook, author, monitored: true);
            var ebook = BuildBook(12, BookMediaType.Ebook, author, monitored: true);
            audiobook.AddOptions.SearchForNewBook = true;
            ebook.AddOptions.SearchForNewBook = true;
            var books = new List<Book> { audiobook, ebook };
            var subject = BuildSubject(books, out var queue, out var bookService);

            subject.SearchForRecentlyAdded(author.Id);

            var commands = queue.Pushed.OfType<BookSearchCommand>().ToList();
            Assert.That(commands.Select(command => command.BookIds.Single()), Is.EquivalentTo(new[] { audiobook.Id, ebook.Id }));
            Assert.That(commands.All(command => command.Trigger != CommandTrigger.Manual), Is.True);
            Assert.That(author.AudiobookMonitored, Is.False);
            Assert.That(author.EbookMonitored, Is.True);
            Assert.That(bookService.AddOptionsUpdated, Is.EqualTo(books));
            Assert.That(books.All(book => !book.AddOptions.SearchForNewBook), Is.True);
        }

        [Test]
        public void saved_search_intent_and_cached_new_rows_should_remain_author_gated()
        {
            var author = new Author
            {
                Id = 7,
                AudiobookMonitored = false,
                EbookMonitored = true,
                AddOptions = null
            };
            var requestedAudiobook = BuildBook(11, BookMediaType.Audiobook, author, monitored: true);
            requestedAudiobook.AddOptions.SearchForNewBook = true;
            var newlyAddedEbook = BuildBook(12, BookMediaType.Ebook, author, monitored: true);
            var books = new List<Book> { requestedAudiobook, newlyAddedEbook };
            var subject = BuildSubject(books, out var queue, out _);

            subject.Handle(new BookInfoRefreshedEvent(author, new List<Book> { newlyAddedEbook }, new List<Book>(), new List<Book>()));
            subject.SearchForRecentlyAdded(author.Id);

            var commands = queue.Pushed.OfType<BookSearchCommand>().ToList();
            Assert.Multiple(() =>
            {
                Assert.That(commands, Has.Count.EqualTo(2));
                Assert.That(commands.All(command => command.Trigger != CommandTrigger.Manual), Is.True);
                Assert.That(commands.Single(command => command.BookIds.Contains(requestedAudiobook.Id)).BookIds,
                    Is.EqualTo(new[] { requestedAudiobook.Id }));
                Assert.That(commands.Single(command => command.BookIds.Contains(newlyAddedEbook.Id)).BookIds,
                    Is.EqualTo(new[] { newlyAddedEbook.Id }));
                Assert.That(author.AudiobookMonitored, Is.False);
                Assert.That(requestedAudiobook.IsMonitored(), Is.True);
                Assert.That(requestedAudiobook.IsMonitoredWithAuthor(), Is.False);
            });
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void rejected_queue_push_should_preserve_unqueued_search_intent(int rejectedPush)
        {
            var author = new Author { Id = 7, AudiobookMonitored = false };
            var books = new[] { 11, 12, 13 }.Select(id => BuildBook(id, BookMediaType.Audiobook, author, true)).ToList();
            books.ForEach(book => book.AddOptions.SearchForNewBook = true);
            var subject = BuildSubject(books, out var queue, out var bookService);
            queue.RejectPush = rejectedPush;

            Assert.DoesNotThrow(() => subject.SearchForRecentlyAdded(author.Id));

            var accepted = books.Where((book, index) => index != rejectedPush - 1).ToList();
            Assert.Multiple(() =>
            {
                Assert.That(bookService.AddOptionsUpdated, Is.EqualTo(accepted));
                Assert.That(accepted.All(book => !book.AddOptions.SearchForNewBook), Is.True);
                Assert.That(books[rejectedPush - 1].AddOptions.SearchForNewBook, Is.True);
                Assert.That(bookService.AddOptionsWriteCount, Is.EqualTo(1));
            });

            queue.RejectPush = 0;
            subject.SearchForRecentlyAdded(author.Id);
            Assert.That(queue.Pushed.OfType<BookSearchCommand>().Select(command => command.BookIds.Single()),
                Is.EqualTo(accepted.Select(book => book.Id).Concat(new[] { books[rejectedPush - 1].Id })),
                "Retry must queue only the still-pending requests.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void rejected_exact_search_should_still_finish_automatic_search_and_scan_completion(bool skipped)
        {
            var author = new Author { Id = 7, Name = "Test author", AudiobookMonitored = false, EbookMonitored = true };
            var requested = BuildBook(11, BookMediaType.Audiobook, author, true);
            requested.AddOptions.SearchForNewBook = true;
            var automatic = BuildBook(12, BookMediaType.Ebook, author, true);
            using var factory = new LogFactory();
            var target = new MemoryTarget { Layout = "${level}|${message}" };
            var logging = new LoggingConfiguration();
            logging.AddRule(LogLevel.Warn, LogLevel.Fatal, target);
            factory.Configuration = logging;
            var subject = BuildSubject(new List<Book> { requested, automatic }, out var queue, out var bookService,
                factory.GetLogger(nameof(BookAddedService)));
            subject.Handle(new BookInfoRefreshedEvent(author, new List<Book> { automatic }, new List<Book>(), new List<Book>()));
            queue.RejectPush = 1;
            var events = new RecordingEventAggregator();
            var handler = new AuthorScannedHandler(null, null, queue, subject, events, LogManager.GetCurrentClassLogger(), Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IBookService>((_, _) => throw new NotSupportedException()));

            Assert.DoesNotThrow(() =>
            {
                if (skipped)
                {
                    handler.Handle(new AuthorScanSkippedEvent(author, AuthorScanSkippedReason.NoFolderEvidence));
                }
                else
                {
                    handler.Handle(new AuthorScannedEvent(author));
                }
            });

            Assert.That(queue.Pushed.OfType<BookSearchCommand>().Single().BookIds, Is.EqualTo(new[] { automatic.Id }));
            Assert.That(queue.Pushed.Single().Trigger, Is.EqualTo(CommandTrigger.Unspecified));
            Assert.That(requested.AddOptions.SearchForNewBook, Is.True);
            Assert.That(bookService.AddOptionsWriteCount, Is.Zero);
            Assert.That(events.Published.OfType<AuthorScanCompletedEvent>().Single().Author, Is.SameAs(author));
            Assert.That(target.Logs, Has.Some.StartWith("Warn|").And.Contain("1").And.Contain("search"));

            queue.RejectPush = 0;
            subject.SearchForRecentlyAdded(author.Id);
            Assert.That(queue.Pushed.OfType<BookSearchCommand>().SelectMany(command => command.BookIds),
                Is.EqualTo(new[] { automatic.Id, requested.Id }));
        }

        [Test]
        public void rejected_automatic_batch_should_keep_its_cache_until_the_existing_retry_succeeds()
        {
            var author = new Author { Id = 7, EbookMonitored = true };
            var books = new[] { 11, 12 }.Select(id => BuildBook(id, BookMediaType.Ebook, author, true)).ToList();
            var requested = BuildBook(13, BookMediaType.Ebook, author, true);
            requested.AddOptions.SearchForNewBook = true;
            var allBooks = books.Concat(new[] { requested }).ToList();
            var subject = BuildSubject(allBooks, out var queue, out var bookService);
            subject.Handle(new BookInfoRefreshedEvent(author, allBooks, new List<Book>(), new List<Book>()));
            queue.RejectPush = 2;

            Assert.DoesNotThrow(() => subject.SearchForRecentlyAdded(author.Id));

            Assert.That(queue.Pushed.OfType<BookSearchCommand>().Single().BookIds, Is.EqualTo(new[] { requested.Id }));
            Assert.That(requested.AddOptions.SearchForNewBook, Is.False);
            Assert.That(bookService.AddOptionsWriteCount, Is.EqualTo(1));
            queue.RejectPush = 0;
            subject.SearchForRecentlyAdded(author.Id);
            Assert.That(queue.Pushed.OfType<BookSearchCommand>().Last().BookIds, Is.EqualTo(books.Select(book => book.Id)));
            subject.SearchForRecentlyAdded(author.Id);
            Assert.That(queue.Pushed, Has.Count.EqualTo(2));
        }

        [Test]
        public void a_large_partial_search_batch_should_load_once_and_save_accepted_flags_once()
        {
            var author = new Author { Id = 7, AudiobookMonitored = false, EbookMonitored = true };
            var requested = Enumerable.Range(1, 200).Select(id => BuildBook(id, BookMediaType.Audiobook, author, true)).ToList();
            requested.ForEach(book => book.AddOptions.SearchForNewBook = true);
            var automatic = BuildBook(201, BookMediaType.Ebook, author, true);
            var subject = BuildSubject(requested.Concat(new[] { automatic }).ToList(), out var queue, out var bookService);
            subject.Handle(new BookInfoRefreshedEvent(author, new List<Book> { automatic }, new List<Book>(), new List<Book>()));
            queue.RejectPush = 101;

            subject.SearchForRecentlyAdded(author.Id);

            Assert.That(bookService.CatalogLoads, Is.EqualTo(1));
            Assert.That(bookService.AddOptionsWriteCount, Is.EqualTo(1));
            Assert.That(bookService.AddOptionsUpdated, Has.Count.EqualTo(199));
            Assert.That(requested.Single(book => book.Id == 101).AddOptions.SearchForNewBook, Is.True);
            Assert.That(queue.Pushed, Has.Count.EqualTo(200));
            Assert.That(queue.Pushed.Count(command => command.Trigger == CommandTrigger.Manual), Is.Zero);
            Assert.That(queue.Pushed.OfType<BookSearchCommand>().Last().BookIds, Is.EqualTo(new[] { automatic.Id }));
        }

        private sealed class RecordingEventAggregator : IEventAggregator
        {
            public List<IEvent> Published { get; } = new();

            public void PublishEvent<TEvent>(TEvent message)
                where TEvent : class, IEvent
            {
                Published.Add(message);
            }
        }

        private static Book BuildBook(int id, BookMediaType mediaType, Author author, bool monitored)
        {
            return new Book
            {
                Id = id,
                Author = author,
                AuthorId = author.Id,
                MediaType = mediaType,
                AudiobookMonitored = mediaType == BookMediaType.Audiobook && monitored,
                EbookMonitored = mediaType == BookMediaType.Ebook && monitored,
                ReleaseDate = DateTime.UtcNow.AddDays(-1)
            };
        }

        private static BookAddedService BuildSubject(List<Book> books, out RecordingCommandQueue queue, out BookServiceProxy bookService, Logger logger = null)
        {
            var service = DispatchProxy.Create<IBookService, BookServiceProxy>();
            bookService = (BookServiceProxy)(object)service;
            bookService.Books = books;
            queue = new RecordingCommandQueue();

            return new BookAddedService(
                new CacheManager(),
                queue,
                service,
                logger ?? LogManager.GetLogger(nameof(BookAddedServiceMonitoringFixture)));
        }

        public class BookServiceProxy : DispatchProxy
        {
            public List<Book> Books { get; set; } = new();
            public List<Book> AddOptionsUpdated { get; private set; } = new();
            public int AddOptionsWriteCount { get; private set; }
            public int CatalogLoads { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IBookService.GetBooksByAuthor))
                {
                    CatalogLoads++;
                    return Books;
                }

                if (targetMethod?.Name == nameof(IBookService.SetAddOptions))
                {
                    AddOptionsWriteCount++;
                    AddOptionsUpdated = ((IEnumerable<Book>)args[0]).ToList();
                    return null;
                }

                throw new NotImplementedException(targetMethod?.Name);
            }
        }

        private sealed class RecordingCommandQueue : IManageCommandQueue
        {
            public List<Command> Pushed { get; } = new();
            public int RejectPush { get; set; }
            private int _pushCount;

            public CommandModel Push<TCommand>(TCommand command, CommandPriority priority = CommandPriority.Normal, CommandTrigger trigger = CommandTrigger.Unspecified)
                where TCommand : Command
            {
                if (++_pushCount == RejectPush)
                {
                    throw new InvalidOperationException("Command queue is full");
                }

                if (trigger != CommandTrigger.Unspecified)
                {
                    command.Trigger = trigger;
                }

                Pushed.Add(command);
                return new CommandModel { Name = command.Name, Body = command, Priority = priority, Trigger = trigger };
            }

            public List<CommandModel> PushMany<TCommand>(List<TCommand> commands) where TCommand : Command => throw new NotImplementedException();
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
            public bool CancelIfQueued(int id) => throw new NotImplementedException();
            public void Pause(int id) => throw new NotImplementedException();
            public void Resume(int id) => throw new NotImplementedException();
            public void CleanCommands() => throw new NotImplementedException();
            public CancellationToken GetCancellationToken(int commandId) => throw new NotImplementedException();
            public void RegisterCancellationToken(int commandId, CancellationTokenSource cancellationTokenSource) => throw new NotImplementedException();
            public void UnregisterCancellationToken(int commandId) => throw new NotImplementedException();
        }
    }
}
