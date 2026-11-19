using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1.Books;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Messaging.Commands;

namespace Chaptarr.Core.Test.Api
{
    public partial class BookApiIdentityFixture
    {
        [Test]
        public void immediate_search_on_add_respects_delay_unless_its_author_side_is_paused(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media,
            [Values(false, true)] bool paused)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                h.Author.AudiobookMonitored = h.Author.EbookMonitored = !paused;
                h.Authors.Update(h.Author);
                var book = h.AddBook(media);
                var result = h.ImportController().ImportBook(new BookImportResource
                {
                    ForeignAuthorId = h.Author.HardcoverAuthorId, ForeignBookId = book.HardcoverBookId,
                    MediaType = media == BookMediaType.Audiobook ? "audiobook" : "ebook",
                    AuthorMonitoring = new BookImportAuthorMonitoring { Monitor = "specificBook", SearchForMissing = true }
                }).GetAwaiter().GetResult();
                Assert.That(result, Is.TypeOf<Microsoft.AspNetCore.Mvc.CreatedResult>());
                var fetched = 0;
                var runner = RecordingBookSearch.Create(h.BookService, h.AuthorService, LogManager.GetCurrentClassLogger(), criteria =>
                {
                    fetched++;
                    RecordingBookSearch.AssertSearchOnAddDelay(criteria, paused);
                });
                foreach (var command in h.Commands.OfType<BookSearchCommand>()) runner.Execute(command);
                Assert.That(fetched, Is.EqualTo(1));
                Assert.That(h.Authors.Get(h.Author.Id).AudiobookMonitored, Is.EqualTo(!paused));
                Assert.That(h.Authors.Get(h.Author.Id).EbookMonitored, Is.EqualTo(!paused));
            });
        }

        [Test]
        public void wanted_edition_search_is_explicit_and_leaves_author_paused(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media,
            [Values(false, true)] bool search, [Values(false, true)] bool newChoice)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var book = h.AddBook(media);
                var edition = h.EditionService.GetEditionsByBook(book.Id).Single();
                if (newChoice)
                {
                    edition = h.Editions.Insert(new Edition
                    {
                        BookId = book.Id, Title = book.Title, ForeignEditionId = "hc:edition:2002", HardcoverEditionId = "2002",
                        ReadingFormatId = media == BookMediaType.Audiobook ? EditionReadingFormat.Audiobook : EditionReadingFormat.Ebook,
                        Narrator = "Requested Reader"
                    });
                }
                if (!newChoice)
                {
                    book.AnyEditionOk = false;
                    h.BookService.UpdateBook(book);
                }
                var controller = h.ImportController();
                var request = new AddWantedEditionRequest { EditionId = edition.Id, SearchForNewBook = search };
                Microsoft.AspNetCore.Mvc.ActionResult<BookResource> result = null;
                if (media == BookMediaType.Ebook)
                    Assert.Throws<FluentValidation.ValidationException>(() => controller.AddWantedEdition(book.Id, request));
                else
                {
                    result = controller.AddWantedEdition(book.Id, request);
                    Assert.That(result.Value, Is.Not.Null, result.Result?.ToString());
                    Assert.That(result.Value.Id == book.Id, Is.EqualTo(!newChoice));
                    Assert.That(result.Value.ExistingCopy, Is.EqualTo(!newChoice));
                }
                var shouldSearch = search && newChoice && media == BookMediaType.Audiobook;
                var searches = h.Commands.OfType<BookSearchCommand>().ToList();
                Assert.That(searches, Has.Count.EqualTo(shouldSearch ? 1 : 0));
                var fetched = new List<int>();
                var runner = RecordingBookSearch.Create(h.BookService, h.AuthorService, LogManager.GetCurrentClassLogger(),
                    criteria => fetched.AddRange(criteria.Books.Select(target => target.Id)));
                foreach (var command in searches)
                {
                    Assert.That(command.Trigger, Is.EqualTo(CommandTrigger.Manual));
                    runner.Execute(command);
                }
                Assert.That(fetched, Is.EqualTo(shouldSearch ? new[] { result.Value.Id } : new int[0]));
                Assert.That(h.Authors.Get(h.Author.Id).AudiobookMonitored, Is.False);
                Assert.That(h.Authors.Get(h.Author.Id).EbookMonitored, Is.False);
            });
        }
    }
}
