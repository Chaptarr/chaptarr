using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Chaptarr.Api.V1.Bookshelf;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Books;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class BookshelfControllerFixture
    {
        [Test]
        public void should_treat_an_empty_monitoring_object_as_no_command()
        {
            var request = STJson.Deserialize<BookshelfResource>(
                "{\"authors\":[{\"id\":42}],\"monitoringOptions\":{}}");
            var authorService = CreateAuthorService(new Author { Id = 42, Name = "Martha Wells" });
            var monitoredService = CreateRecordingMonitoredService();
            var controller = new BookshelfController(authorService, monitoredService);

            var result = controller.UpdateAll(request);

            Assert.That(result, Is.TypeOf<AcceptedResult>());
            Assert.That(GetMonitoredServiceProxy(monitoredService).Calls, Is.Empty);
            Assert.That(GetAuthorServiceProxy(authorService).GetAuthorsCallCount, Is.Zero);
        }

        [Test]
        public void should_pass_the_selected_media_type_to_book_monitoring()
        {
            var request = STJson.Deserialize<BookshelfResource>(
                "{\"authors\":[{\"id\":42}],\"monitoringOptions\":{\"monitor\":\"existing\",\"mediaType\":\"ebook\"}}");
            var authorService = CreateAuthorService(new Author { Id = 42, Name = "Martha Wells" });
            var monitoredService = CreateRecordingMonitoredService();
            var controller = new BookshelfController(authorService, monitoredService);

            var result = controller.UpdateAll(request);
            var call = GetMonitoredServiceProxy(monitoredService).Calls.Single();

            Assert.That(result, Is.TypeOf<AcceptedResult>());
            Assert.That(call.Author.Id, Is.EqualTo(42));
            Assert.That(call.Options.Monitor, Is.EqualTo(MonitorTypes.Existing));
            Assert.That(call.Options.MediaType, Is.EqualTo(BookMediaType.Ebook));
        }

        [TestCase("{\"authors\":[{\"id\":42,\"monitored\":false}],\"monitoringOptions\":{}}")]
        [TestCase("{\"authors\":[{\"id\":42}],\"monitorNewItems\":\"new\"}")]
        public void should_reject_legacy_author_level_controls(string json)
        {
            var request = STJson.Deserialize<BookshelfResource>(json);
            var authorService = CreateAuthorService(new Author { Id = 42, Name = "Martha Wells" });
            var monitoredService = CreateRecordingMonitoredService();
            var controller = new BookshelfController(authorService, monitoredService);

            var result = controller.UpdateAll(request);

            Assert.That(result, Is.TypeOf<BadRequestObjectResult>());
            Assert.That(GetMonitoredServiceProxy(monitoredService).Calls, Is.Empty);
            Assert.That(GetAuthorServiceProxy(authorService).GetAuthorsCallCount, Is.Zero);
        }

        [Test]
        public void should_preserve_specific_book_requests_without_an_explicit_monitor_enum()
        {
            var request = STJson.Deserialize<BookshelfResource>(
                "{\"authors\":[{\"id\":42}],\"monitoringOptions\":{\"booksToMonitor\":[\"123\"]}}");
            var authorService = CreateAuthorService(new Author { Id = 42, Name = "Martha Wells" });
            var monitoredService = CreateRecordingMonitoredService();
            var controller = new BookshelfController(authorService, monitoredService);

            var result = controller.UpdateAll(request);
            var call = GetMonitoredServiceProxy(monitoredService).Calls.Single();

            Assert.That(result, Is.TypeOf<AcceptedResult>());
            Assert.That(call.Options.Monitor, Is.EqualTo(MonitorTypes.SpecificBook));
            Assert.That(call.Options.BooksToMonitor, Is.EqualTo(new[] { "123" }));
        }

        [TestCase("{\"authors\":[{\"id\":42}],\"monitoringOptions\":{\"monitor\":\"specificBook\"}}")]
        [TestCase("{\"authors\":[{\"id\":42}],\"monitoringOptions\":{\"monitor\":\"specificBook\",\"booksToMonitor\":null}}")]
        [TestCase("{\"authors\":[{\"id\":42}],\"monitoringOptions\":{\"monitor\":\"specificBook\",\"booksToMonitor\":[]}}")]
        public void should_reject_specific_book_monitoring_without_a_book_id(string json)
        {
            var request = STJson.Deserialize<BookshelfResource>(json);
            var authorService = CreateAuthorService(new Author { Id = 42, Name = "Martha Wells" });
            var monitoredService = CreateRecordingMonitoredService();
            var controller = new BookshelfController(authorService, monitoredService);

            var result = controller.UpdateAll(request);

            Assert.That(result, Is.TypeOf<BadRequestObjectResult>());
            Assert.That(((BadRequestObjectResult)result).Value, Is.EqualTo("Specific-book monitoring requires at least one book ID."));
            Assert.That(GetMonitoredServiceProxy(monitoredService).Calls, Is.Empty);
            Assert.That(GetAuthorServiceProxy(authorService).GetAuthorsCallCount, Is.Zero);
        }

        [Test]
        public void should_only_change_books_for_the_selected_media_type()
        {
            var author = new Author { Id = 42, Name = "Martha Wells" };
            var audiobook = new Book
            {
                Id = 1,
                AuthorId = author.Id,
                MediaType = BookMediaType.Audiobook,
                AudiobookMonitored = false
            };
            var ebook = new Book
            {
                Id = 2,
                AuthorId = author.Id,
                MediaType = BookMediaType.Ebook,
                EbookMonitored = false
            };
            var authorService = CreateAuthorService(author);
            var bookService = CreateBookService(audiobook, ebook);
            var subject = new BookMonitoredService(authorService, bookService, LogManager.GetCurrentClassLogger());

            subject.SetBookMonitoredStatus(author, new MonitoringOptions
            {
                Monitor = MonitorTypes.All,
                MediaType = BookMediaType.Ebook
            });

            Assert.That(audiobook.AudiobookMonitored, Is.False);
            Assert.That(ebook.EbookMonitored, Is.True);
            Assert.That(GetBookServiceProxy(bookService).UpdatedBooks.Select(book => book.Id), Is.EqualTo(new[] { ebook.Id }));
        }

        [Test]
        public void should_keep_the_legacy_future_action_date_based_without_changing_missing_semantics()
        {
            var now = DateTime.UtcNow;
            var author = new Author { Id = 42, Name = "Martha Wells" };
            var withFile = BuildAudiobook(1, author, now.AddYears(-1));
            var released = BuildAudiobook(2, author, now.AddDays(-1));
            var future = BuildAudiobook(3, author, now.AddDays(1));
            var undated = BuildAudiobook(4, author, null);
            var authorService = CreateAuthorService(author);
            var bookService = CreateBookService(withFile, released, future, undated);
            GetBookServiceProxy(bookService).BooksWithFiles.Add(withFile);
            var subject = new BookMonitoredService(authorService, bookService, LogManager.GetCurrentClassLogger());

            subject.SetBookMonitoredStatus(author, new MonitoringOptions
            {
                Monitor = MonitorTypes.Future,
                MediaType = BookMediaType.Audiobook
            });

            Assert.Multiple(() =>
            {
                Assert.That(withFile.AudiobookMonitored, Is.False);
                Assert.That(released.AudiobookMonitored, Is.False);
                Assert.That(future.AudiobookMonitored, Is.True);
                Assert.That(undated.AudiobookMonitored, Is.True);
            });
        }

        [Test]
        public void missing_should_monitor_every_fileless_current_book_regardless_of_release_date()
        {
            var now = DateTime.UtcNow;
            var author = new Author { Id = 42, Name = "Martha Wells" };
            var withFile = BuildAudiobook(1, author, now.AddYears(-1));
            var released = BuildAudiobook(2, author, now.AddDays(-1), monitored: false);
            var future = BuildAudiobook(3, author, now.AddDays(1), monitored: false);
            var undated = BuildAudiobook(4, author, null, monitored: false);
            var authorService = CreateAuthorService(author);
            var bookService = CreateBookService(withFile, released, future, undated);
            GetBookServiceProxy(bookService).BooksWithFiles.Add(withFile);
            var subject = new BookMonitoredService(authorService, bookService, LogManager.GetCurrentClassLogger());

            subject.SetBookMonitoredStatus(author, new MonitoringOptions
            {
                Monitor = MonitorTypes.Missing,
                MediaType = BookMediaType.Audiobook
            });

            Assert.Multiple(() =>
            {
                Assert.That(withFile.AudiobookMonitored, Is.False);
                Assert.That(released.AudiobookMonitored, Is.True);
                Assert.That(future.AudiobookMonitored, Is.True);
                Assert.That(undated.AudiobookMonitored, Is.True);
            });
        }

        private static Book BuildAudiobook(int id, Author author, DateTime? releaseDate, bool monitored = true)
        {
            return new Book
            {
                Id = id,
                AuthorId = author.Id,
                MediaType = BookMediaType.Audiobook,
                AudiobookMonitored = monitored,
                ReleaseDate = releaseDate
            };
        }

        private static IAuthorService CreateAuthorService(params Author[] authors)
        {
            var service = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            GetAuthorServiceProxy(service).Authors = authors.ToList();
            return service;
        }

        private static IBookService CreateBookService(params Book[] books)
        {
            var service = DispatchProxy.Create<IBookService, BookServiceProxy>();
            GetBookServiceProxy(service).Books = books.ToList();
            return service;
        }

        private static IBookMonitoredService CreateRecordingMonitoredService()
        {
            return DispatchProxy.Create<IBookMonitoredService, BookMonitoredServiceProxy>();
        }

        private static AuthorServiceProxy GetAuthorServiceProxy(IAuthorService service)
        {
            return (AuthorServiceProxy)(object)service;
        }

        private static BookServiceProxy GetBookServiceProxy(IBookService service)
        {
            return (BookServiceProxy)(object)service;
        }

        private static BookMonitoredServiceProxy GetMonitoredServiceProxy(IBookMonitoredService service)
        {
            return (BookMonitoredServiceProxy)(object)service;
        }

        private class AuthorServiceProxy : DispatchProxy
        {
            public List<Author> Authors { get; set; } = new();
            public int GetAuthorsCallCount { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    nameof(IAuthorService.GetAuthors) => GetAuthors((IEnumerable<int>)args[0]),
                    nameof(IAuthorService.UpdateAuthor) => args[0],
                    _ => throw new NotImplementedException($"Test proxy does not implement {targetMethod?.Name}")
                };
            }

            private List<Author> GetAuthors(IEnumerable<int> authorIds)
            {
                GetAuthorsCallCount++;
                var ids = authorIds.ToHashSet();
                return Authors.Where(author => ids.Contains(author.Id)).ToList();
            }
        }

        private class BookServiceProxy : DispatchProxy
        {
            public List<Book> Books { get; set; } = new();
            public List<Book> BooksWithFiles { get; } = new();
            public List<Book> UpdatedBooks { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case nameof(IBookService.GetBooksByAuthor):
                        var authorId = (int)args[0];
                        return Books.Where(book => book.AuthorId == authorId).ToList();
                    case nameof(IBookService.GetAuthorBooksWithFiles):
                        return BooksWithFiles;
                    case nameof(IBookService.UpdateBook):
                        var book = (Book)args[0];
                        UpdatedBooks.Add(book);
                        return book;
                    case nameof(IBookService.UpdateManyWithLifecycle):
                        UpdatedBooks.AddRange((List<Book>)args[0]);
                        return null;
                    default:
                        throw new NotImplementedException($"Test proxy does not implement {targetMethod?.Name}");
                }
            }
        }

        private class BookMonitoredServiceProxy : DispatchProxy
        {
            public List<(Author Author, MonitoringOptions Options)> Calls { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IBookMonitoredService.SetBookMonitoredStatus))
                {
                    Calls.Add(((Author)args[0], (MonitoringOptions)args[1]));
                    return null;
                }

                throw new NotImplementedException($"Test proxy does not implement {targetMethod?.Name}");
            }
        }
    }
}
