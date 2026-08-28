using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Messaging.Commands;

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
        public void explicit_book_search_flag_should_not_bypass_the_media_side_author_gate()
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

            var command = queue.Pushed.OfType<BookSearchCommand>().Single();
            Assert.That(command.BookIds, Is.EqualTo(new[] { ebook.Id }));
            Assert.That(bookService.AddOptionsUpdated, Is.EqualTo(books));
            Assert.That(books.All(book => !book.AddOptions.SearchForNewBook), Is.True);
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

        private static BookAddedService BuildSubject(List<Book> books, out RecordingCommandQueue queue, out BookServiceProxy bookService)
        {
            var service = DispatchProxy.Create<IBookService, BookServiceProxy>();
            bookService = (BookServiceProxy)(object)service;
            bookService.Books = books;
            queue = new RecordingCommandQueue();

            return new BookAddedService(
                new CacheManager(),
                queue,
                service,
                LogManager.GetLogger(nameof(BookAddedServiceMonitoringFixture)));
        }

        public class BookServiceProxy : DispatchProxy
        {
            public List<Book> Books { get; set; } = new();
            public List<Book> AddOptionsUpdated { get; private set; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IBookService.GetBooksByAuthor))
                {
                    return Books;
                }

                if (targetMethod?.Name == nameof(IBookService.SetAddOptions))
                {
                    AddOptionsUpdated = ((IEnumerable<Book>)args[0]).ToList();
                    return null;
                }

                throw new NotImplementedException(targetMethod?.Name);
            }
        }

        private sealed class RecordingCommandQueue : IManageCommandQueue
        {
            public List<Command> Pushed { get; } = new();

            public CommandModel Push<TCommand>(TCommand command, CommandPriority priority = CommandPriority.Normal, CommandTrigger trigger = CommandTrigger.Unspecified)
                where TCommand : Command
            {
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
            public void Pause(int id) => throw new NotImplementedException();
            public void Resume(int id) => throw new NotImplementedException();
            public void CleanCommands() => throw new NotImplementedException();
            public CancellationToken GetCancellationToken(int commandId) => throw new NotImplementedException();
            public void RegisterCancellationToken(int commandId, CancellationTokenSource cancellationTokenSource) => throw new NotImplementedException();
            public void UnregisterCancellationToken(int commandId) => throw new NotImplementedException();
        }
    }
}
