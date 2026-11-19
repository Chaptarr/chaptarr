using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test.Common;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Commands;
using NUnit.Framework;

namespace Chaptarr.Core.Test.ImportLists
{
    public partial class ImportListMissingMediaSidePersistenceFixture
    {
        [TestCase(false, BookMediaType.Audiobook)]
        [TestCase(false, BookMediaType.Ebook)]
        [TestCase(true, BookMediaType.Audiobook)]
        [TestCase(true, BookMediaType.Ebook)]
        public void rejected_list_sync_search_followed_by_scan_must_not_bypass_author_pause(bool postgres, BookMediaType side)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var stored = new StoredListLibrary(database);
                var book = CatalogBook(side, "Dune", "123");
                book.SetMonitored(false);
                stored.Save(book);
                stored.RejectBookSearches = true;
                stored.Sync(book, side, new() { stored.Report(book, 0) });
                Assert.That(stored.Books.GetBook(book.Id).AddOptions?.SearchForNewBook, Is.True,
                    "The failed automatic enqueue must leave real persisted retry intent.");
                stored.RejectBookSearches = false;
                new AuthorScannedHandler(new BookMonitoredService(stored.AuthorService, stored.Books, stored.Logger),
                    stored.AuthorService, stored.Queue, new BookAddedService(new CacheManager(), stored.Queue, stored.Books, stored.Logger),
                    stored.Events, stored.Logger, stored.Books).Handle(new AuthorScannedEvent(stored.Authors.Get(stored.Author.Id)));
                var searches = stored.Commands.OfType<BookSearchCommand>().ToList();
                Assert.That(searches, Has.Count.EqualTo(1));
                Assert.That(searches.Single().Trigger, Is.Not.EqualTo(CommandTrigger.Manual));
                var fetched = new List<int>();
                var runner = RecordingBookSearch.Create(stored.Books, stored.AuthorService, stored.Logger,
                    criteria => fetched.AddRange(criteria.Books.Select(item => item.Id)));
                searches.ForEach(runner.Execute);
                Assert.That(fetched, Is.Empty);
                Assert.That(stored.Books.GetBook(book.Id).IsMonitored(), Is.True);
                Assert.That(stored.Books.GetBook(book.Id).IsMonitoredWithAuthor(), Is.False);
            });
        }

        [TestCase(false, BookMediaType.Audiobook)]
        [TestCase(false, BookMediaType.Ebook)]
        [TestCase(true, BookMediaType.Audiobook)]
        [TestCase(true, BookMediaType.Ebook)]
        public void missing_batch_observes_author_pause_between_books_but_explicit_single_search_still_runs(bool postgres, BookMediaType side)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var stored = new StoredListLibrary(database);
                var author = stored.Authors.Get(stored.Author.Id);
                author.AudiobookMonitored = true;
                author.EbookMonitored = true;
                stored.AuthorService.UpdateAuthor(author);
                var first = CatalogBook(side, "Dune", "123");
                var second = CatalogBook(side, "Dune Messiah", "124");
                first.Editions.Single().Monitored = true;
                second.Editions.Single().Monitored = true;
                first.SetMonitored(true);
                second.SetMonitored(true);
                stored.Save(first);
                stored.Save(second);
                var fetched = new List<int>();
                var runner = RecordingBookSearch.Create(stored.Books, stored.AuthorService, stored.Logger, criteria =>
                {
                    fetched.AddRange(criteria.Books.Select(book => book.Id));
                    var saved = stored.Authors.Get(author.Id);
                    if (side == BookMediaType.Audiobook) saved.AudiobookMonitored = false;
                    else saved.EbookMonitored = false;
                    stored.AuthorService.UpdateAuthor(saved);
                });
                runner.Execute(new MissingBookSearchCommand(author.Id) { MediaType = side == BookMediaType.Audiobook ? "audiobook" : "ebook" });
                Assert.That(fetched, Is.EqualTo(new[] { first.Id }));
                runner.Execute(new BookSearchCommand(new() { second.Id }) { Trigger = CommandTrigger.Manual });
                Assert.That(fetched, Is.EqualTo(new[] { first.Id, second.Id }));
                Assert.That(stored.Books.GetBook(second.Id).IsMonitoredWithAuthor(), Is.False);
            });
        }

    }
}
