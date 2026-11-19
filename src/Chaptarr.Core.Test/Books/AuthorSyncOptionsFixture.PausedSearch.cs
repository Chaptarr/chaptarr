using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test.Common;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Messaging.Commands;

namespace Chaptarr.Core.Test.Books
{
    public partial class AuthorSyncOptionsFixture
    {
        [Test]
        public void exact_book_author_api_search_uses_the_same_delay_policy_ready_or_queued(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media,
            [Values(false, true)] bool queued, [Values(false, true)] bool paused)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var initial = h.Request("both", false);
                initial.AudiobookMonitored = initial.EbookMonitored = !paused;
                h.Import(initial);
                h.Scan();
                h.Commands.Clear();
                var request = NativeAuthorRequest(h.Request(media == BookMediaType.Audiobook ? "audiobook" : "ebook", false));
                request.AddOptions.Monitor = MonitorTypes.SpecificBook;
                request.AddOptions.MediaType = media;
                request.AddOptions.BooksToMonitor = new() { queued ? "hc:456790" : "hc:456789" };
                request.AudiobookMonitorExistingMode = request.EbookMonitorExistingMode = MonitorTypes.SpecificBook;
                request.AddOptions.SearchForMissingBooks = true;
                h.Controller.AddAuthor(request).GetAwaiter().GetResult();
                if (queued)
                {
                    Assert.That(h.Commands.OfType<BookSearchCommand>(), Is.Empty);
                    var pending = h.Pending.GetByProviderId("hc:123456");
                    Assert.That(pending, Is.Not.Null);
                    Assert.That(RequestedBookSearch.Read(pending.ExplicitBookSearches), Has.Count.EqualTo(1));
                    h.CatalogWorkCount = 2;
                    h.ProcessPending();
                }
                var fetched = new List<Book>();
                var runner = RecordingBookSearch.Create(h.Books, h.Authors, h.Logger, criteria =>
                {
                    RecordingBookSearch.AssertSearchOnAddDelay(criteria, paused);
                    fetched.AddRange(criteria.Books);
                });
                foreach (var command in h.Commands.OfType<BookSearchCommand>()) runner.Execute(command);
                Assert.That(fetched, Has.Count.EqualTo(1));
                Assert.That(fetched.Single().MediaType, Is.EqualTo(media));
                Assert.That(fetched.Single().HardcoverBookId, Is.EqualTo(queued ? "hc:456790" : "hc:456789"));
                Assert.That(h.Author.AudiobookMonitored, Is.EqualTo(!paused));
                Assert.That(h.Author.EbookMonitored, Is.EqualTo(!paused));
            });
        }

        [Test]
        public void add_author_search_missing_skips_the_paused_format_after_the_initial_scan(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType paused)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var request = NativeAuthorRequest(h.Request("both", false));
                request.AudiobookMonitored = paused != BookMediaType.Audiobook;
                request.EbookMonitored = paused != BookMediaType.Ebook;
                request.AddOptions.SearchForMissingBooks = true;
                h.Controller.AddAuthor(request).GetAwaiter().GetResult();
                Assert.That(h.Commands.OfType<MissingBookSearchCommand>(), Is.Empty);
                h.Scan();
                var fetched = new List<Book>();
                var runner = RecordingBookSearch.Create(h.Books, h.Authors, h.Logger, criteria => fetched.AddRange(criteria.Books));
                var commands = h.Commands.OfType<MissingBookSearchCommand>().ToList();
                Assert.That(commands, Has.Count.EqualTo(1));
                foreach (var command in commands) runner.Execute(command);
                Assert.That(fetched, Has.Count.EqualTo(1));
                Assert.That(fetched.Single().MediaType, Is.Not.EqualTo(paused));
                h.Scan();
                Assert.That(h.Commands.OfType<MissingBookSearchCommand>().Count(), Is.EqualTo(1));
            });
        }
    }
}
