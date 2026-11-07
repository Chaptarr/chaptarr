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
        public void should_keep_bookshelf_default_all_monitoring_for_an_empty_options_object()
        {
            var request = STJson.Deserialize<BookshelfResource>(
                "{\"authors\":[{\"id\":42}],\"monitoringOptions\":{}}");
            var authorService = CreateAuthorService(new Author { Id = 42, Name = "Martha Wells" });
            var monitoredService = CreateRecordingMonitoredService();
            var controller = new BookshelfController(authorService, monitoredService);

            var result = controller.UpdateAll(request);
            var calls = GetMonitoredServiceProxy(monitoredService).Calls;

            Assert.That(result, Is.TypeOf<AcceptedResult>());
            Assert.That(calls, Has.Count.EqualTo(1));
            Assert.That(calls[0].Options.Monitor, Is.EqualTo(MonitorTypes.All));
            Assert.That(GetAuthorServiceProxy(authorService).GetAuthorsCallCount, Is.EqualTo(1));
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
        public void should_expand_split_legacy_ids_for_specific_book_monitoring()
        {
            var request = STJson.Deserialize<BookshelfResource>(
                "{\"authors\":[{\"id\":42}],\"monitoringOptions\":{\"booksToMonitor\":[\"123\"]}}");
            var authorService = CreateAuthorService(new Author { Id = 42, Name = "Martha Wells" });
            var monitoredService = CreateRecordingMonitoredService();
            var identityMap = CreateLegacyIdentityMapService();
            GetLegacyIdentityMapProxy(identityMap).ResolvedBookIds = new List<int> { 123, 124 };
            var controller = new BookshelfController(authorService, monitoredService, identityMap);

            var result = controller.UpdateAll(request);
            var call = GetMonitoredServiceProxy(monitoredService).Calls.Single();

            Assert.That(result, Is.TypeOf<AcceptedResult>());
            Assert.That(call.Options.Monitor, Is.EqualTo(MonitorTypes.SpecificBook));
            Assert.That(call.Options.BooksToMonitor, Is.EqualTo(new[] { "123", "124" }));
        }

        [Test]
        public void should_return_persisted_legacy_identity_mappings_for_requested_ids()
        {
            var identityMap = CreateLegacyIdentityMapService();
            GetLegacyIdentityMapProxy(identityMap).Mappings = new List<BookshelfLegacyIdentityMapEntry>
            {
                new BookshelfLegacyIdentityMapEntry
                {
                    Source = "BookshelfNG",
                    EntityType = "Book",
                    SourceId = 30,
                    TargetId = 30,
                    MediaType = (int)BookMediaType.Audiobook
                },
                new BookshelfLegacyIdentityMapEntry
                {
                    Source = "BookshelfNG",
                    EntityType = "Book",
                    SourceId = 30,
                    TargetId = 31,
                    MediaType = (int)BookMediaType.Ebook
                }
            };
            var controller = new BookshelfController(
                CreateAuthorService(new Author { Id = 42, Name = "Martha Wells" }),
                CreateRecordingMonitoredService(),
                identityMap);

            var result = controller.GetLegacyIdentities("Book", new List<int> { 30 });
            var mappings = ((OkObjectResult)result).Value as List<BookshelfLegacyIdentityResource>;

            Assert.That(mappings.Select(mapping => (mapping.SourceId, mapping.TargetId, mapping.MediaType)), Is.EqualTo(new[]
            {
                (30, 30, "audiobook"),
                (30, 31, "ebook")
            }));
        }

        [Test]
        public void should_return_author_metadata_identity_mappings_for_compatibility()
        {
            var identityMap = CreateLegacyIdentityMapService();
            GetLegacyIdentityMapProxy(identityMap).Mappings = new List<BookshelfLegacyIdentityMapEntry>
            {
                new BookshelfLegacyIdentityMapEntry
                {
                    Source = "BookshelfNG",
                    EntityType = "AuthorMetadata",
                    SourceId = 10,
                    TargetId = 20
                }
            };
            var controller = new BookshelfController(
                CreateAuthorService(new Author { Id = 42, Name = "Martha Wells" }),
                CreateRecordingMonitoredService(),
                identityMap);

            var result = controller.GetLegacyIdentities("AuthorMetadata", new List<int> { 10 });
            var mappings = ((OkObjectResult)result).Value as List<BookshelfLegacyIdentityResource>;

            Assert.That(mappings.Select(mapping => (mapping.EntityType, mapping.SourceId, mapping.TargetId)), Is.EqualTo(new[]
            {
                ("AuthorMetadata", 10, 20)
            }));
        }

        [TestCase("Series")]
        [TestCase("")]
        public void should_reject_unsupported_legacy_identity_entity_types(string entityType)
        {
            var controller = new BookshelfController(
                CreateAuthorService(new Author { Id = 42, Name = "Martha Wells" }),
                CreateRecordingMonitoredService(),
                CreateLegacyIdentityMapService());

            var result = controller.GetLegacyIdentities(entityType, new List<int> { 30 });

            Assert.That(result, Is.TypeOf<BadRequestObjectResult>());
        }

        [Test]
        public void should_reject_legacy_identity_requests_over_the_batch_limit()
        {
            var controller = new BookshelfController(
                CreateAuthorService(new Author { Id = 42, Name = "Martha Wells" }),
                CreateRecordingMonitoredService(),
                CreateLegacyIdentityMapService());

            var result = controller.GetLegacyIdentities("Book", Enumerable.Range(1, 501).ToList());

            Assert.That(result, Is.TypeOf<BadRequestObjectResult>());
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

        private static IBookshelfLegacyIdentityMapService CreateLegacyIdentityMapService()
        {
            return DispatchProxy.Create<IBookshelfLegacyIdentityMapService, LegacyIdentityMapServiceProxy>();
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

        private static LegacyIdentityMapServiceProxy GetLegacyIdentityMapProxy(IBookshelfLegacyIdentityMapService service)
        {
            return (LegacyIdentityMapServiceProxy)(object)service;
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

        private class LegacyIdentityMapServiceProxy : DispatchProxy
        {
            public List<int> ResolvedBookIds { get; set; } = new();
            public List<BookshelfLegacyIdentityMapEntry> Mappings { get; set; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    nameof(IBookshelfLegacyIdentityMapService.ResolveBookIds) => ResolvedBookIds,
                    nameof(IBookshelfLegacyIdentityMapService.GetMappings) => Mappings,
                    nameof(IBookshelfLegacyIdentityMapService.GetMappingsByTargetIds) => Mappings,
                    _ => throw new NotImplementedException($"Test proxy does not implement {targetMethod?.Name}")
                };
            }
        }
    }
}
