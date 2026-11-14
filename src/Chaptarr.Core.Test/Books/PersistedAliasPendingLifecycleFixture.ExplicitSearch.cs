using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Chaptarr.Api.V1.Commands;
using Chaptarr.Core.Test.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NLog;
using Newtonsoft.Json;
using NUnit.Framework;
using NzbDrone.Common.Composition;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Messaging.Commands;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Books
{
    public partial class PersistedAliasPendingLifecycleFixture
    {
        private static MonitoringConfig SearchConfig(BookMediaType media, string work, bool list, string edition = null) => new()
        {
            CreateAudiobook = media == BookMediaType.Audiobook, CreateEbook = media == BookMediaType.Ebook,
            RequestedBy = list ? "ImportList:Favorites" : "ThirdPartyApp",
            AudiobookBooksToMonitor = media == BookMediaType.Audiobook ? new() { work } : null,
            EbookBooksToMonitor = media == BookMediaType.Ebook ? new() { work } : null,
            AudiobookBooksToSearch = media == BookMediaType.Audiobook ? new() { work } : null,
            EbookBooksToSearch = media == BookMediaType.Ebook ? new() { work } : null,
            AudiobookNarrations = edition == null ? null : new() { new RequestedNarration
            { MediaType = media, WorkProviderId = work, EditionProviderId = edition, Search = true } }
        };

        [Test]
        public void explicit_narration_does_not_authorize_another_recording_of_the_same_work(
            [Values(false, true)] bool postgres, [Values(false, true)] bool listFirst)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var selected = h.AddBook(BookMediaType.Audiobook, "hc:7000", "hc:edition:3000");
                var listed = h.AddBook(BookMediaType.Audiobook, "hc:7000", "hc:edition:3001");
                var otherRecording = h.Editions.FindByBook(new[] { listed.Id }).Single();
                otherRecording.Narrator = "Other Reader";
                otherRecording.NarratorNames = new() { "Other Reader" };
                h.Editions.Update(otherRecording);
                var requests = new[] { SearchConfig(BookMediaType.Audiobook, "hc:7000", false, "hc:edition:3000"),
                    SearchConfig(BookMediaType.Audiobook, "hc:7000", true, "hc:edition:3001") };
                foreach (var config in listFirst ? requests.Reverse() : requests)
                    h.Pending.EnqueueAsync(h.Author.HardcoverAuthorId, config, config.RequestedBy).GetAwaiter().GetResult();
                h.Run();
                Assert.That(FetchQueued(h), Is.EqualTo(new[] { selected.Id }));
                Assert.That(h.Commands.OfType<BookSearchCommand>().Where(command => command.Trigger != CommandTrigger.Manual)
                    .SelectMany(command => command.BookIds), Is.EqualTo(new[] { listed.Id }));
            });
        }

        [Test]
        public void explicit_work_search_survives_a_list_narration_request_for_that_work(
            [Values(false, true)] bool postgres, [Values(false, true)] bool listFirst)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var book = h.AddBook(BookMediaType.Audiobook, "hc:7000", "hc:edition:3000");
                var requests = new[] { SearchConfig(BookMediaType.Audiobook, "hc:7000", false),
                    SearchConfig(BookMediaType.Audiobook, "hc:7000", true, "hc:edition:3000") };
                foreach (var config in listFirst ? requests.Reverse() : requests)
                    h.Pending.EnqueueAsync(h.Author.HardcoverAuthorId, config, config.RequestedBy).GetAwaiter().GetResult();
                h.Run();
                Assert.That(FetchQueued(h), Is.EqualTo(new[] { book.Id }));
                h.Run();
                Assert.That(h.Commands.OfType<BookSearchCommand>().Count(), Is.EqualTo(1));
            });
        }

        [Test]
        public void cancelled_request_does_not_keep_explicit_search_permission([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                h.AddBook();
                var id = h.Pending.EnqueueAsync(h.Author.HardcoverAuthorId,
                    SearchConfig(BookMediaType.Audiobook, "hc:698880", false), "UserInterface").GetAwaiter().GetResult();
                Assert.That(RequestedBookSearch.Read(h.Requests.Get(id).ExplicitBookSearches), Has.Count.EqualTo(1));
                h.Pending.Cancel(id);
                Assert.That(RequestedBookSearch.Read(h.Requests.Get(id).ExplicitBookSearches), Is.Empty);
                h.Run();
                Assert.That(h.Commands.OfType<BookSearchCommand>(), Is.Empty);
            });
        }

        private static List<int> FetchQueued(Harness h)
        {
            var fetched = new List<int>();
            var runner = RecordingBookSearch.Create(h.BookService, h.AuthorService, LogManager.GetCurrentClassLogger(),
                criteria => fetched.AddRange(criteria.Books.Select(book => book.Id)));
            foreach (var command in h.Commands.OfType<BookSearchCommand>()) runner.Execute(command);
            return fetched;
        }

        [Test]
        public void merged_requests_search_only_explicit_books_on_paused_formats(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media,
            [Values(false, true)] bool listFirst)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var selected = h.AddBook(media, "hc:7000", "hc:edition:3000");
                var listed = h.AddBook(media, "hc:7001", "hc:edition:3001");
                var otherSide = media == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook;
                var opposite = h.AddBook(otherSide, "hc:7000", "hc:edition:3000");
                var requests = new[] { SearchConfig(media, selected.HardcoverBookId, false), SearchConfig(media, listed.HardcoverBookId, true) };
                foreach (var config in listFirst ? requests.Reverse() : requests)
                    h.Pending.EnqueueAsync(h.Author.HardcoverAuthorId, config, config.RequestedBy).GetAwaiter().GetResult();
                h.Pending.EnqueueAsync(h.Author.HardcoverAuthorId, SearchConfig(otherSide, opposite.HardcoverBookId, true), "ImportList:Other").GetAwaiter().GetResult();
                var pending = h.Pending.GetByProviderId(h.Author.HardcoverAuthorId);
                Assert.That(RequestedBookSearch.Read(pending.ExplicitBookSearches).Single().MediaType, Is.EqualTo(media));
                h.Run();
                Assert.That(FetchQueued(h), Is.EqualTo(new[] { selected.Id }));
                Assert.That(h.Commands.OfType<BookSearchCommand>().Single(command => command.Trigger == CommandTrigger.Manual).BookIds,
                    Is.EqualTo(new[] { selected.Id }));
                Assert.That(h.BookService.GetBook(selected.Id).IsMonitored(), Is.True);
                Assert.That(h.BookService.GetBook(selected.Id).IsMonitoredWithAuthor(), Is.False);
                h.Run();
                Assert.That(h.Commands.OfType<BookSearchCommand>().Count(command => command.Trigger == CommandTrigger.Manual), Is.EqualTo(1));
            });
        }

        [Test]
        public void explicit_missing_work_waits_for_catalog_then_searches_once(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database) { WorkReady = true };
                var config = SearchConfig(media, "hc:7000", false);
                h.Pending.EnqueueAsync(h.Author.HardcoverAuthorId, config, "Seerr").GetAwaiter().GetResult();
                h.Run();
                Assert.That(h.Commands.OfType<BookSearchCommand>(), Is.Empty);
                var pending = h.Pending.GetByProviderId(h.Author.HardcoverAuthorId);
                Assert.That(pending, Is.Not.Null);
                Assert.That(RequestedBookSearch.Read(pending.ExplicitBookSearches), Has.Count.EqualTo(1));
                var book = h.AddBook(media, "hc:7000");
                h.Pending.RetryNow(pending.Id);
                h.Run();
                Assert.That(FetchQueued(h), Is.EqualTo(new[] { book.Id }));
                h.Run();
                Assert.That(h.Commands.OfType<BookSearchCommand>().ToList(), Has.Count.EqualTo(1));
            });
        }

        [Test]
        public void selected_search_api_honors_one_or_many_paused_books_without_resuming_the_author(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media,
            [Values(1, 3)] int count)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var books = Enumerable.Range(0, count).Select(i => h.AddBook(media, $"hc:{7000 + i}", $"hc:edition:{3000 + i}")).ToList();
                var context = new DefaultHttpContext();
                context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new
                    { name = "BookSearch", bookIds = books.Select(book => book.Id).ToList() })));
                var controller = new CommandController(Proxy<IManageCommandQueue>((method, args) => method.Name == nameof(IManageCommandQueue.Get)
                    ? new CommandModel { Id = (int)args[0], Name = "BookSearch", Body = h.Commands[(int)args[0] - 1] }
                    : method.Invoke(h.Queue, args)), null, new KnownTypes(new List<Type> { typeof(BookSearchCommand) }), LogManager.GetCurrentClassLogger())
                    { ControllerContext = new ControllerContext { HttpContext = context } };
                controller.StartCommand(new CommandResource { Name = "BookSearch" });
                Assert.That(FetchQueued(h), Is.EqualTo(books.Select(book => book.Id)));
                Assert.That((media == BookMediaType.Audiobook ? h.Authors.Get(h.Author.Id).AudiobookMonitored : h.Authors.Get(h.Author.Id).EbookMonitored), Is.False);
            });
        }

        [Test]
        public void automatic_progress_cannot_consume_a_new_explicit_request_for_the_same_target(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media,
            [Values(false, true)] bool narration)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var edition = narration ? "hc:edition:3000" : null;
                h.Pending.EnqueueAsync(h.Author.HardcoverAuthorId, SearchConfig(media, "hc:7000", true, edition), "ImportList:Favorites").GetAwaiter().GetResult();
                var previous = h.Pending.GetByProviderId(h.Author.HardcoverAuthorId);
                var progress = JsonConvert.DeserializeObject<PendingAuthorImport>(JsonConvert.SerializeObject(previous));
                if (media == BookMediaType.Audiobook) progress.AudiobookBooksToSearch = "[]";
                else progress.EbookBooksToSearch = "[]";
                progress.AudiobookNarrations = "[]";
                h.Pending.EnqueueAsync(h.Author.HardcoverAuthorId, SearchConfig(media, "hc:7000", false, edition), "UserInterface").GetAwaiter().GetResult();
                Assert.That(h.Pending.SaveProgress(progress, previous), Is.True);
                var saved = h.Pending.GetByProviderId(h.Author.HardcoverAuthorId);
                Assert.That(RequestedBookSearch.Read(saved.ExplicitBookSearches), Has.Count.EqualTo(1));
                Assert.That(JsonConvert.DeserializeObject<List<string>>(media == BookMediaType.Audiobook ? saved.AudiobookBooksToSearch : saved.EbookBooksToSearch), Is.EqualTo(new[] { "hc:7000" }));
                if (narration) Assert.That(JsonConvert.DeserializeObject<List<RequestedNarration>>(saved.AudiobookNarrations).Single().Search, Is.True);
                var book = h.AddBook(media, "hc:7000", edition ?? "hc:edition:3000");
                h.Run();
                Assert.That(FetchQueued(h), Is.EqualTo(new[] { book.Id }));
            });
        }

        [Test]
        public void legacy_null_permission_never_infers_explicit_search_from_the_row_origin([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                h.AddBook();
                var request = h.Request(new() { "hc:698880" });
                request.RequestedBy = "UserInterface";
                h.Requests.Update(request);
                h.Run();
                Assert.That(h.Commands.OfType<BookSearchCommand>().ToList(), Has.Count.EqualTo(1));
                Assert.That(FetchQueued(h), Is.Empty);
            });
        }
    }
}
