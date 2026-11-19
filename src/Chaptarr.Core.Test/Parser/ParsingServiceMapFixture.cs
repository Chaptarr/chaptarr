using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NzbDrone.Common.Cache;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Parser
{
    [TestFixture]
    public class ParsingServiceMapFixture
    {
        private class ThrowingProxy<T> : DispatchProxy where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"Test proxy does not implement {typeof(T).Name}.{targetMethod?.Name}");
            }
        }

        private class DelegateProxy<T> : DispatchProxy where T : class
        {
            public Func<MethodInfo, object[], object> Handler { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (Handler != null)
                {
                    return Handler(targetMethod, args);
                }

                throw new NotImplementedException($"Test proxy does not implement {typeof(T).Name}.{targetMethod?.Name}");
            }
        }

        private static T CreateProxy<T>(Func<MethodInfo, object[], object> handler)
            where T : class
        {
            var proxy = DispatchProxy.Create<T, DelegateProxy<T>>();
            ((DelegateProxy<T>)(object)proxy).Handler = handler;
            return proxy;
        }

        [Test]
        public void should_not_throw_when_mapping_with_invalid_author_and_book_ids()
        {
            var service = new ParsingService(
                DispatchProxy.Create<IAuthorService, ThrowingProxy<IAuthorService>>(),
                DispatchProxy.Create<IBookService, ThrowingProxy<IBookService>>(),
                DispatchProxy.Create<IEditionService, ThrowingProxy<IEditionService>>(),
                DispatchProxy.Create<IMediaFileService, ThrowingProxy<IMediaFileService>>(),
                LogManager.GetCurrentClassLogger());

            var parsedBookInfo = new ParsedBookInfo
            {
                AuthorName = "Travis Beacham",
                BookTitle = "Impact Winter"
            };

            var remoteBook = service.Map(parsedBookInfo, authorId: 0, bookIds: new[] { 0, -1 });

            Assert.That(remoteBook, Is.Not.Null);
            Assert.That(remoteBook.Author, Is.Null);
            Assert.That(remoteBook.Books, Is.Not.Null);
            Assert.That(remoteBook.Books, Is.Empty);
        }


        [TestCase("Brian Herbert", "Kevin J Anderson, Brian Herbert", true)]
        [TestCase("Jennifer L. Armentrout", "Jennifer Armentrout", true)]
        [TestCase("Jennifer L. Armentrout", "Jennifer Q. Armentrout", false)]
        [TestCase("Robert Louis Stevenson", "Robert Stevenson", true)]
        [TestCase("Robert Louis Stevenson", "Stevenson, Robert", true)]
        [TestCase("Лев Н. Толстой", "Лев Толстой", true)]
        [TestCase("Jane Smith", "Pat Smith, Jane Q", false)]
        [TestCase("がとう", "か\u3099とう", true)]
        [TestCase("がとう", "かとう", false)]
        public void should_use_shared_author_evidence_for_search_context(string stored, string supplied, bool matches)
        {
            var lookupCalls = 0;
            var service = new ParsingService(
                CreateProxy<IAuthorService>((method, args) =>
                {
                    Assert.That(method.ReturnType, Is.EqualTo(typeof(Author)));
                    Assert.That(args, Has.Length.EqualTo(1));
                    Assert.That(args[0], Is.TypeOf<string>());
                    lookupCalls++;
                    return null;
                }),
                DispatchProxy.Create<IBookService, ThrowingProxy<IBookService>>(),
                DispatchProxy.Create<IEditionService, ThrowingProxy<IEditionService>>(),
                DispatchProxy.Create<IMediaFileService, ThrowingProxy<IMediaFileService>>(),
                LogManager.GetCurrentClassLogger());

            var author = new Author
            {
                Id = 38,
                Name = stored,
                CleanName = stored.CleanAuthorName()
            };

            var book = new Book
            {
                Id = 501,
                AuthorId = author.Id,
                Title = "House Harkonnen",
                CleanTitle = "househarkonnen"
            };

            var parsedBookInfo = new ParsedBookInfo
            {
                AuthorName = supplied,
                BookTitle = "House Harkonnen"
            };

            var criteria = new NzbDrone.Core.IndexerSearch.Definitions.BookSearchCriteria
            {
                Author = author,
                Books = new System.Collections.Generic.List<Book> { book },
                AuthorCatalog = new System.Collections.Generic.List<Book> { book }
            };

            var remoteBook = service.Map(parsedBookInfo, criteria);

            Assert.That(remoteBook.Author, Is.EqualTo(matches ? author : null));
            Assert.That(remoteBook.Books, Has.Count.EqualTo(matches ? 1 : 0));
            if (matches)
            {
                Assert.That(remoteBook.Books[0], Is.SameAs(book));
                Assert.That(lookupCalls, Is.Zero);
            }
        }

        [Test]
        public void should_scope_title_lookup_to_the_parsed_media_enum()
        {
            var author = new Author
            {
                Id = 1235,
                Name = "Che Yeun",
                CleanName = "cheyeun"
            };

            var audiobookBook = new Book
            {
                Id = 10,
                AuthorId = author.Id,
                Title = "Tailbone",
                CleanTitle = "tailbone",
                MediaType = BookMediaType.Audiobook,
                AudiobookMonitored = true,
                EbookMonitored = false
            };

            var ebookBook = new Book
            {
                Id = 11,
                AuthorId = author.Id,
                Title = "Tailbone",
                CleanTitle = "tailbone",
                MediaType = BookMediaType.Ebook,
                AudiobookMonitored = false,
                EbookMonitored = true
            };

            var authorService = CreateProxy<IAuthorService>((method, args) =>
            {
                if (method.Name == nameof(IAuthorService.FindByName) && (string)args[0] == "Che Yeun")
                {
                    return author;
                }

                throw new NotImplementedException($"Unhandled {nameof(IAuthorService)}.{method.Name}");
            });

            var bookService = CreateProxy<IBookService>((method, args) =>
            {
                if (method.Name == nameof(IBookService.GetBooksByAuthor))
                {
                    Assert.That(args[1], Is.True);
                    return new List<Book> { audiobookBook, ebookBook };
                }

                throw new NotImplementedException($"Unhandled {nameof(IBookService)}.{method.Name}");
            });

            var editionService = DispatchProxy.Create<IEditionService, ThrowingProxy<IEditionService>>();

            var service = new ParsingService(
                authorService,
                bookService,
                editionService,
                DispatchProxy.Create<IMediaFileService, ThrowingProxy<IMediaFileService>>(),
                LogManager.GetCurrentClassLogger());

            var parsedBookInfo = new ParsedBookInfo
            {
                AuthorName = "Che Yeun",
                BookTitle = "Tailbone",
                Quality = new QualityModel(Quality.EPUB, new Revision(1))
            };

            var remoteBook = service.Map(parsedBookInfo);

            Assert.That(remoteBook.Author, Is.SameAs(author));
            Assert.That(remoteBook.Books, Has.Count.EqualTo(1));
            Assert.That(remoteBook.Books[0], Is.SameAs(ebookBook));
            Assert.That(remoteBook.Books[0].EbookMonitored, Is.True);
        }

        [Test]
        public void whole_inverted_author_must_not_be_replaced_by_a_mononym_fragment()
        {
            var whole = new Author { Id = 11, Name = "John Smith" };
            var fragment = new Author { Id = 12, Name = "John" };
            var harness = CreateCandidateParser(new[] { whole, fragment });

            var remote = harness.Parser.Map(new ParsedBookInfo { AuthorName = "Smith, John", BookTitle = "Quiet River" });

            Assert.Multiple(() =>
            {
                Assert.That(remote.Author, Is.SameAs(whole));
                Assert.That(remote.Books, Has.Count.EqualTo(1));
                Assert.That(harness.Calls, Is.EqualTo(new[] { (nameof(IAuthorService.FindByName), "Smith, John") }));
                Assert.That(harness.CatalogReads(), Is.EqualTo(1));
                Assert.That(harness.AuthorReads(), Is.Zero);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void complete_credit_candidates_must_remain_ambiguous_in_either_order(bool reverse)
        {
            var authors = new[] { new Author { Id = 11, Name = "Ann Smith" }, new Author { Id = 12, Name = "Pat Smith" } };
            var ordered = reverse ? authors.Reverse().ToArray() : authors;
            var harness = CreateCandidateParser(ordered);

            var remote = harness.Parser.Map(new ParsedBookInfo { AuthorName = "Ann and Pat Smith", BookTitle = "Quiet River" });

            Assert.Multiple(() =>
            {
                Assert.That(remote.Author, Is.Null);
                Assert.That(remote.Books, Is.Empty);
                Assert.That(harness.Calls, Is.EqualTo(new[] { (nameof(IAuthorService.FindByName), "Ann and Pat Smith") }));
                Assert.That(harness.CatalogReads(), Is.EqualTo(1));
                Assert.That(harness.AuthorReads(), Is.Zero);
            });
        }

        [TestCase("Other Writer, Alex Reed")]
        [TestCase("Other Writer; Alex Reed")]
        [TestCase("Other Writer & Alex Reed")]
        [TestCase("Other Writer ＆ Alex Reed")]
        public void contributor_fields_must_reach_the_service_once_without_fragment_lookups(string field)
        {
            var author = new Author { Id = 11, Name = "Alex Reed" };
            var harness = CreateCandidateParser(new[] { author });

            var remote = harness.Parser.Map(new ParsedBookInfo { AuthorName = field, BookTitle = "Quiet River" });

            Assert.Multiple(() =>
            {
                Assert.That(remote.Author, Is.SameAs(author));
                Assert.That(remote.Books, Has.Count.EqualTo(1));
                Assert.That(harness.Calls, Is.EqualTo(new[] { (nameof(IAuthorService.FindByName), field) }));
                Assert.That(harness.CatalogReads(), Is.EqualTo(1));
                Assert.That(harness.AuthorReads(), Is.Zero);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void duplicate_exact_database_names_must_stay_ambiguous(bool reverse)
        {
            var authors = new[] { new Author { Id = 11, Name = "Alex Reed" }, new Author { Id = 12, Name = "Alex Reed" } };
            var ordered = reverse ? authors.Reverse().ToArray() : authors;
            var harness = CreateCandidateParser(ordered);

            var remote = harness.Parser.Map(new ParsedBookInfo { AuthorName = "Alex Reed", BookTitle = "Quiet River" });

            Assert.That(remote.Author, Is.Null);
            Assert.That(remote.Books, Is.Empty);
        }

        [Test]
        public void whole_author_credit_must_use_one_canonical_lookup()
        {
            var author = new Author { Id = 11, Name = "Alex Reed" };
            var harness = CreateCandidateParser(new[] { author });

            var remote = harness.Parser.Map(new ParsedBookInfo { AuthorName = "Alex Reed", BookTitle = "Quiet River" });

            Assert.That(remote.Author, Is.SameAs(author));
            Assert.That(harness.Calls, Is.EqualTo(new[] { (nameof(IAuthorService.FindByName), "Alex Reed") }));
            Assert.That(harness.CatalogReads(), Is.EqualTo(1));
            Assert.That(harness.AuthorReads(), Is.Zero);
        }

        private static (ParsingService Parser, List<(string Method, string Value)> Calls, Func<int> CatalogReads, Func<int> AuthorReads)
            CreateCandidateParser(IEnumerable<Author> library)
        {
            var authors = library.ToList();
            var catalogReads = 0;
            var authorReads = 0;
            var calls = new List<(string Method, string Value)>();
            var repository = CreateProxy<IAuthorRepository>((method, args) =>
            {
                switch (method.Name)
                {
                    case nameof(IAuthorRepository.All):
                        catalogReads++;
                        return authors;
                    case "FindByName":
                        var exact = authors.Where(author => author.Name.CleanAuthorName() == (string)args[0]).ToList();
                        return exact.Count == 1 ? exact[0] : null;
                    case nameof(IAuthorRepository.Get):
                        authorReads++;
                        return authors.Single(author => author.Id == (int)args[0]);
                    default:
                        throw new NotSupportedException(method.Name);
                }
            });
            var subject = new AuthorService(repository, CreateProxy<IEventAggregator>((_, _) => null),
                authorPathBuilder: null, rootFolderService: null, commandQueueManager: null, cacheManager: new CacheManager(),
                bookRepository: null, mediaFileService: null, logger: LogManager.GetCurrentClassLogger(),
                importListExclusionService: null, deleteMediaFiles: new Lazy<IDeleteMediaFiles>(() => null),
                bookService: new Lazy<IBookService>(() => null),
                mainDatabase: CreateProxy<IMainDatabase>((method, _) => throw new NotSupportedException(method.Name)));
            var authorService = CreateProxy<IAuthorService>((method, args) =>
            {
                calls.Add((method.Name, (string)args[0]));
                return method.Invoke(subject, args);
            });
            var book = new Book { Id = 101, AuthorId = authors[0].Id, Title = "Quiet River", CleanTitle = "quietriver" };
            var bookService = CreateProxy<IBookService>((method, args) => method.Name switch
            {
                nameof(IBookService.GetBooksByAuthor) or nameof(IBookService.GetBooksByAuthorId) or "GetCandidates" =>
                    (int)args[0] == book.AuthorId ? new List<Book> { book } : new List<Book>(),
                "FindByTitle" or "FindByTitleInexact" =>
                    (int)args[0] == book.AuthorId && string.Equals((string)args[1], book.Title, StringComparison.OrdinalIgnoreCase) ? book : null,
                _ => throw new NotSupportedException(method.Name)
            });
            var editions = CreateProxy<IEditionService>((method, _) => method.Name switch
            {
                "FindByTitle" or "FindByTitleInexact" => null,
                "GetCandidates" or nameof(IEditionService.GetEditionsByAuthor) => new List<Edition>(),
                _ => throw new NotSupportedException(method.Name)
            });
            var parser = new ParsingService(authorService, bookService, editions, null, LogManager.GetCurrentClassLogger());
            return (parser, calls, () => catalogReads, () => authorReads);
        }
    }
}
