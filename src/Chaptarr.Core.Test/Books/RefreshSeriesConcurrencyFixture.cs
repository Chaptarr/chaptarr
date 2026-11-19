using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class RefreshSeriesConcurrencyFixture
    {
        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void shared_series_reconciles_both_authors_without_losing_links(bool postgres, bool firstAuthorLeaves)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var log = LogManager.GetCurrentClassLogger();
                var events = InterfaceProxy.Proxy<IEventAggregator>((_, _) => null);
                var authorRows = new AuthorRepository(database, events, log);
                foreach (var id in new[] { 1, 2 })
                {
                    authorRows.Insert(new Author { Name = $"Author {id}", CleanName = $"author{id}", TitleSlug = $"author-{id}",
                        HardcoverAuthorId = $"hc:{id}", Path = $"/audiobooks/author-{id}", Overview = "", Added = DateTime.UtcNow });
                }
                var bookRows = new BookRepository(database, events);
                var books = new[] { 1, 2 }.Select(id => bookRows.Insert(new Book
                {
                    AuthorId = id, Title = $"Book {id}", CleanTitle = $"book{id}", TitleSlug = $"book-{id}",
                    HardcoverBookId = $"hc:{id}", MediaType = BookMediaType.Audiobook, Added = DateTime.UtcNow
                })).ToArray();
                var links = new SeriesBookLinkRepository(database, events);
                var seriesRows = new SeriesRepository(database, events, links, log);
                var linkService = new SeriesBookLinkService(links);
                var seriesService = new SeriesService(seriesRows, log);
                using var bothDiscovered = new Barrier(2);
                var synchronize = false;
                var catalog = InterfaceProxy.Proxy<IBookService>((method, args) =>
                {
                    Assert.That(method.Name, Is.EqualTo(nameof(IBookService.GetBooksByAuthor)));
                    if (synchronize) Assert.That(bothDiscovered.SignalAndWait(TimeSpan.FromSeconds(10)), Is.True);
                    return books.Where(book => book.AuthorId == (int)args[0]).ToList();
                });
                var subject = new RefreshSeriesService(catalog, seriesService, linkService,
                    new RefreshSeriesBookLinkService(linkService, log), log);

                List<Series> Snapshot(Book book, string id) => new()
                {
                    new Series
                    {
                        Title = id, GoodreadsSeriesId = id, MediaType = book.MediaType,
                        LinkItems = new List<SeriesBookLink>
                        {
                            new SeriesBookLink { Book = book, BookId = book.Id, Position = "1", IsPrimary = true }
                        }
                    }
                };
                void Refresh(Book book, string id) => subject.RefreshSeriesInfo(book.AuthorId,
                    Snapshot(book, id), new Author { Id = book.AuthorId }, false, false, null);

                if (firstAuthorLeaves) Refresh(books[0], "gr:100");
                synchronize = true;
                var first = Task.Run(() => Refresh(books[0], firstAuthorLeaves ? "gr:200" : "gr:100"));
                var second = Task.Run(() => Refresh(books[1], "gr:100"));
                Assert.That(Task.WaitAll(new[] { first, second }, TimeSpan.FromSeconds(20)), Is.True);

                var shared = seriesRows.FindById("gr:100", BookMediaType.Audiobook);
                Assert.That(shared, Is.Not.Null);
                Assert.That(links.GetLinksBySeries(shared.Id).Select(link => link.BookId),
                    Is.EquivalentTo(firstAuthorLeaves ? new[] { books[1].Id } : books.Select(book => book.Id)));
                var all = seriesRows.All().ToList();
                Assert.That(all.Count(series => series.GoodreadsSeriesId == "gr:100"), Is.EqualTo(1));
                Assert.That(links.All().All(link => all.Any(series => series.Id == link.SeriesId)), Is.True, "No orphan links.");
                Assert.That(seriesRows.DeleteIfUnlinked(shared.Id), Is.False, "Guarded deletion must retain linked series.");
                links.DeleteMany(links.GetLinksBySeries(shared.Id));
                Assert.That(seriesRows.DeleteIfUnlinked(shared.Id), Is.True, "Empty series may be deleted.");
            });
        }
    }
}
