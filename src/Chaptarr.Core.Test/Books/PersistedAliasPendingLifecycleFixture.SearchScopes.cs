using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Chaptarr.Api.V1.Commands;
using Chaptarr.Api.V1.PendingImport;
using Chaptarr.Core.Test.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NLog;
using Newtonsoft.Json;
using NUnit.Framework;
using NzbDrone.Common.Composition;
using NzbDrone.Core.Books;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Messaging.Commands;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Books
{
    public partial class PersistedAliasPendingLifecycleFixture
    {
        [Test]
        public void broad_command_filters_paused_and_unmonitored_books_but_selections_search_once(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media,
            [Values(false, true)] bool paused, [Values(false, true)] bool monitored, [Values(false, true)] bool monitoredOnly)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                h.Author.AudiobookMonitored = h.Author.EbookMonitored = !paused;
                h.Authors.Update(h.Author);
                var books = Enumerable.Range(0, 2).Select(i => h.AddBook(media, $"hc:{7000 + i}", $"hc:edition:{3000 + i}")).ToList();
                foreach (var book in books) { book.SetMonitored(monitored); h.Books.Update(book); }
                var context = new DefaultHttpContext();
                context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new
                    { name = "BookSearch", bookIds = books.Select(book => book.Id).ToList(), monitoredOnly })));
                var controller = new CommandController(Proxy<IManageCommandQueue>((method, args) => method.Name == nameof(IManageCommandQueue.Get)
                    ? new CommandModel { Id = (int)args[0], Name = "BookSearch", Body = h.Commands[(int)args[0] - 1] }
                    : method.Invoke(h.Queue, args)), null, new KnownTypes(new List<Type> { typeof(BookSearchCommand) }), LogManager.GetCurrentClassLogger())
                    { ControllerContext = new ControllerContext { HttpContext = context } };
                controller.StartCommand(new CommandResource { Name = "BookSearch" });
                Assert.That(h.Commands.OfType<BookSearchCommand>().Single().MonitoredOnly, Is.EqualTo(monitoredOnly));
                Assert.That(FetchQueued(h), Is.EqualTo(monitoredOnly && (paused || !monitored) ? Array.Empty<int>() : books.Select(book => book.Id)));
                Assert.That(h.Authors.Get(h.Author.Id).AudiobookMonitored, Is.EqualTo(!paused));
                Assert.That(h.Authors.Get(h.Author.Id).EbookMonitored, Is.EqualTo(!paused));
            });
        }

        [Test]
        public void queued_search_on_add_respects_delay_unless_its_author_side_is_paused(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media,
            [Values(false, true)] bool paused)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                h.Author.AudiobookMonitored = h.Author.EbookMonitored = !paused;
                h.Authors.Update(h.Author);
                var book = h.AddBook(media, "hc:7000");
                h.Pending.EnqueueAsync(h.Author.HardcoverAuthorId, SearchConfig(media, "hc:7000", false), "UserInterface").GetAwaiter().GetResult();
                h.Run();
                var fetched = 0;
                var runner = RecordingBookSearch.Create(h.BookService, h.AuthorService, LogManager.GetCurrentClassLogger(), criteria =>
                {
                    fetched++;
                    RecordingBookSearch.AssertSearchOnAddDelay(criteria, paused);
                });
                foreach (var command in h.Commands.OfType<BookSearchCommand>()) runner.Execute(command);
                Assert.That(fetched, Is.EqualTo(1));
                h.Run();
                Assert.That(h.Commands.OfType<BookSearchCommand>().Count(), Is.EqualTo(1));
            });
        }

        [Test]
        public void generic_author_queue_search_missing_does_not_become_explicit_book_searches(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var config = new MediaTypeConfig { Monitor = false, BooksToMonitor = new() { null, "", " ", "hc:7000" } };
                var controller = new PendingAuthorImportController(null, h.Pending, h.AuthorService, null, null, null);
                var response = controller.QueueAuthor(new QueueAuthorRequest
                {
                    ProviderId = h.Author.HardcoverAuthorId, SearchForMissingBooks = true,
                    Audiobook = media == BookMediaType.Audiobook ? config : null,
                    Ebook = media == BookMediaType.Ebook ? config : null
                }).GetAwaiter().GetResult();
                Assert.That(response.Result, Is.TypeOf<OkObjectResult>());
                var pending = h.Pending.GetByProviderId(h.Author.HardcoverAuthorId);
                Assert.That(pending.SearchForMissingBooks, Is.True);
                Assert.That(RequestedBookSearch.Read(pending.ExplicitBookSearches), Is.Empty);
                Assert.That(media == BookMediaType.Audiobook ? pending.AudiobookBooksToSearch : pending.EbookBooksToSearch, Is.Null.Or.EqualTo("[]"));
                var explicitConfig = SearchConfig(media, "hc:7000", false);
                if (media == BookMediaType.Audiobook) explicitConfig.AudiobookBooksToSearch.AddRange(new[] { null, "", " " });
                else explicitConfig.EbookBooksToSearch.AddRange(new[] { null, "", " " });
                h.Pending.EnqueueAsync("hc:9002", explicitConfig, "UserInterface").GetAwaiter().GetResult();
                Assert.That(RequestedBookSearch.Read(h.Pending.GetByProviderId("hc:9002").ExplicitBookSearches).Select(request => request.WorkProviderId), Is.EqualTo(new[] { "hc:7000" }));
            });
        }
    }
}
