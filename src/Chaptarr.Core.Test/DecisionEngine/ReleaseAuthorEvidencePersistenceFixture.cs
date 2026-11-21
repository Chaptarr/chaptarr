using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.DecisionEngine
{
    [TestFixture]
    public class ReleaseAuthorEvidencePersistenceFixture
    {
        [Test]
        public void stored_edition_and_linked_series_evidence_beats_an_omitted_qualification(
            [Values(false, true)] bool postgres,
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var logger = LogManager.GetCurrentClassLogger();
                var events = InterfaceProxy.Proxy<IEventAggregator>((_, _) => null);
                var authors = new AuthorRepository(database, events, logger);
                var books = new BookRepository(database, events);
                var editions = new EditionRepository(database, events);
                var links = new SeriesBookLinkRepository(database, events);
                var series = new SeriesRepository(database, events, links, logger);
                var author = authors.Insert(new Author
                {
                    Name = "Brandon Sanderson", CleanName = "brandonsanderson", TitleSlug = "brandon-sanderson",
                    Path = "fixture", Added = DateTime.UtcNow
                });
                var membership = series.Insert(new Series
                {
                    Title = "Mistborn, Era 1: Original Trilogy", TitleSlug = "mistborn",
                    GoodreadsSeriesId = "gr:40910", MediaType = media
                });
                var ids = new List<int>();
                foreach (var title in new[] { "The Final Empire", "Mistborn", "Mistborn: The Final Empire - Annotations" })
                {
                    var book = books.Insert(new Book
                    {
                        AuthorId = author.Id, Title = title, CleanTitle = title, TitleSlug = title,
                        Added = DateTime.UtcNow, MediaType = media, AnyEditionOk = true,
                        HardcoverBookId = $"hc:fixture-{ids.Count}"
                    });
                    editions.Insert(new Edition
                    {
                        BookId = book.Id, Title = title, ForeignEditionId = $"fixture-{book.Id}",
                        Subtitle = ids.Count == 2 ? "The Final Empire - Annotations" : null,
                        Monitored = true, ReadingFormatId = 3
                    });
                    ids.Add(book.Id);
                }

                links.Insert(new SeriesBookLink { SeriesId = membership.Id, BookId = ids[0], Position = "1" });
                var service = CreateBookService(database, authors, books, editions, events, logger);
                var catalog = service.GetBooksByAuthor(author.Id, onlyMonitoredEditions: true);
                Assert.That(catalog.Single(book => book.Id == ids[0]).SeriesLinks.Single().Series.Value.Title,
                    Is.EqualTo(membership.Title));
                var specification = new ReleaseTitleMatchSpecification(logger);
                foreach (var (title, expectedId) in new[]
                {
                    ("Brandon Sanderson - Mistborn: The Final Empire", ids[0]),
                    ("Brandon Sanderson - The Final Empire", ids[0]),
                    ("Brandon Sanderson - Mistborn: The Final Empire - Annotations", ids[2])
                })
                {
                    var release = new ReleaseInfo { Title = title };
                    var rss = ReleaseTitleMatchScorer.FindBestMatches(release, author.Name, catalog, null, catalog,
                        new ReleaseTitleMatchScorer.CatalogPreparation());
                    Assert.That(rss.Where(match => match.IsMatch).Select(match => match.Book.Id),
                        Is.EqualTo(new[] { expectedId }), $"{database.DatabaseType}; RSS; {title}");
                    foreach (var target in catalog)
                    {
                        var criteria = new BookSearchCriteria
                        {
                            Author = author, Books = new List<Book> { target }, AuthorCatalog = catalog
                        };
                        var remote = new RemoteBook { Author = author, Books = criteria.Books, Release = release };
                        Assert.That(specification.IsSatisfiedBy(remote, criteria).Accepted,
                            Is.EqualTo(target.Id == expectedId), $"{database.DatabaseType}; search; {title}; target={target.Id}");
                    }
                }
            });
        }

        [Test]
        public void individual_and_bulk_book_loads_require_author_evidence(
            [Values(false, true)] bool postgres,
            [Values(false, true)] bool anyEditionOk)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var logger = LogManager.GetCurrentClassLogger();
                var events = InterfaceProxy.Proxy<IEventAggregator>((_, _) => null);
                var authors = new AuthorRepository(database, events, logger);
                var books = new BookRepository(database, events);
                var editions = new EditionRepository(database, events);
                var author = authors.Insert(new Author
                {
                    Name = "Frank Herbert", CleanName = "frankherbert", TitleSlug = "frank-herbert",
                    Path = "fixture", Added = DateTime.UtcNow
                });
                var book = books.Insert(new Book
                {
                    AuthorId = author.Id, Title = "Dune", CleanTitle = "dune", TitleSlug = "dune",
                    Added = DateTime.UtcNow, MediaType = BookMediaType.Audiobook,
                    AnyEditionOk = anyEditionOk, SeriesName = "Dune Chronicles", SeriesPosition = "1"
                });
                foreach (var narrator in new[] { "George Guidall", "Scott Brick" })
                {
                    editions.Insert(new Edition
                    {
                        BookId = book.Id, Title = "Dune", Narrator = narrator, ReadingFormatId = 2,
                        ForeignEditionId = "fixture-" + narrator, Monitored = narrator == "George Guidall"
                    });
                }

                var service = CreateBookService(database, authors, books, editions, events, logger);
                var single = service.GetBook(book.Id);
                var bulk = service.GetBooksByAuthor(author.Id, onlyMonitoredEditions: true).Single();
                Assert.That(single.Editions, Has.Count.EqualTo(2));
                Assert.That(bulk.Editions, Has.Count.EqualTo(1));

                var specification = new ReleaseTitleMatchSpecification(logger);
                foreach (var loaded in new[] { single, bulk })
                {
                    foreach (var item in new[]
                    {
                        (Title: "Dune - Scott Brick [m4b]", Author: (string)null, Accept: false),
                        (Title: "Dune - George Guidall [m4b]", Author: (string)null, Accept: false),
                        (Title: "Dune Chronicles - Dune", Author: (string)null, Accept: false),
                        (Title: "Dune", Author: (string)null, Accept: false),
                        (Title: "Frank Herbert - Dune - Scott Brick [m4b]", Author: (string)null, Accept: true),
                        (Title: "Dune - Scott Brick [m4b]", Author: "Frank Herbert", Accept: true),
                        (Title: "Frank Herbert - Dune", Author: "Another Writer", Accept: false)
                    })
                    {
                        var criteria = new BookSearchCriteria
                        {
                            Author = author, Books = new List<Book> { loaded }, AuthorCatalog = new List<Book> { loaded }
                        };
                        var remote = new RemoteBook
                        {
                            Author = author, Books = criteria.Books,
                            Release = new ReleaseInfo { Title = item.Title, Author = item.Author }
                        };
                        Assert.That(specification.IsSatisfiedBy(remote, criteria).Accepted, Is.EqualTo(item.Accept),
                            $"{database.DatabaseType}; editions={loaded.Editions.Count}; title={item.Title}; author={item.Author}");
                    }
                }

                Assert.That(books.Get(book.Id).AnyEditionOk, Is.EqualTo(anyEditionOk));
                Assert.That(editions.All().Where(edition => edition.BookId == book.Id).Single(edition => edition.Monitored).Narrator, Is.EqualTo("George Guidall"));
            });
        }

        [Test]
        public void release_catalog_keeps_unmonitored_books_but_only_their_monitored_editions([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var logger = LogManager.GetCurrentClassLogger();
                var events = InterfaceProxy.Proxy<IEventAggregator>((_, _) => null);
                var authors = new AuthorRepository(database, events, logger);
                var books = new BookRepository(database, events);
                var editions = new EditionRepository(database, events);
                var author = authors.Insert(new Author
                {
                    Name = "Frank Herbert", CleanName = "frankherbert", TitleSlug = "frank-herbert",
                    Path = "fixture", Added = DateTime.UtcNow
                });
                foreach (var media in new[] { BookMediaType.Audiobook, BookMediaType.Ebook })
                {
                    var book = books.Insert(new Book
                    {
                        AuthorId = author.Id, Title = "Dune Messiah", CleanTitle = "dunemessiah", TitleSlug = "dune-messiah",
                        Added = DateTime.UtcNow, MediaType = media, AudiobookMonitored = false, EbookMonitored = false
                    });
                    foreach (var monitored in new[] { false, true })
                    {
                        editions.Insert(new Edition
                        {
                            BookId = book.Id, Title = monitored ? "Dune Messiah" : "Frank Herbert (Audio Cassette)",
                            ForeignEditionId = $"fixture-{media}-{monitored}", ReadingFormatId = 1, Monitored = monitored
                        });
                    }
                }

                var service = CreateBookService(database, authors, books, editions, events, logger);
                var catalog = service.GetBooksByAuthor(author.Id, onlyMonitoredEditions: true);
                Assert.That(catalog.Select(book => book.MediaType), Is.EquivalentTo(new[] { BookMediaType.Audiobook, BookMediaType.Ebook }));
                foreach (var book in catalog)
                {
                    Assert.That(book.AudiobookMonitored || book.EbookMonitored, Is.False);
                    Assert.That(book.Editions, Has.Count.EqualTo(1));
                    Assert.That(book.Editions.Single().Monitored, Is.True);
                    Assert.That(book.Editions.Single().Title, Is.EqualTo("Dune Messiah"));
                }
            });
        }

        private static BookService CreateBookService(IMainDatabase database, AuthorRepository authors, BookRepository books,
            EditionRepository editions, IEventAggregator events, Logger logger)
        {
            var authorService = InterfaceProxy.Proxy<IAuthorService>((method, args) =>
                method.Name == nameof(IAuthorService.GetAuthor) ? authors.Get((int)args[0]) : throw new NotImplementedException(method.Name));
            var files = InterfaceProxy.Proxy<IMediaFileService>((method, _) =>
                method.Name == nameof(IMediaFileService.GetFilesByBooks) ? new List<BookFile>() : throw new NotImplementedException(method.Name));
            return new BookService(books, new EditionService(editions, events, logger), events,
                authorService, files, null, new SeriesBookLinkRepository(database, events), null, logger);
        }
    }
}
