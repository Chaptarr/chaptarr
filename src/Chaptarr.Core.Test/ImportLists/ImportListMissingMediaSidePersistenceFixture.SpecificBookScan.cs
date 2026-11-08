using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test.Common;
using NUnit.Framework;
using Newtonsoft.Json;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Commands;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.ImportLists
{
    public partial class ImportListMissingMediaSidePersistenceFixture
    {
        [Test]
        public void named_list_policy_should_not_monitor_the_opposite_media_side_from_stale_legacy_intent(
            [Values(false, true)] bool postgres,
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType listedSide)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var stored = new StoredListLibrary(database);
                stored.Authors.Delete(stored.Author.Id);
                var opposite = listedSide == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook;
                var listed = CatalogBook(listedSide, "Listed Work", "123");
                ImportListSyncServiceSearchOnAddFixture.BuildMissingMediaSideService(stored.Author, listed, listedSide,
                    true, stored.AuthorService, stored.Books, stored.Editions, stored.Pending, stored.Roots, stored.Queue,
                    bothSides: true, reports: new() { stored.Report(listed, 0) }, search: false, configureList: list =>
                    {
                        list.ShouldMonitor = ImportListMonitorType.EntireAuthor;
                        list.MonitoringOptions.AudiobookMonitor = listedSide == BookMediaType.Audiobook ? MonitorTypes.SpecificBook : MonitorTypes.None;
                        list.MonitoringOptions.EbookMonitor = listedSide == BookMediaType.Ebook ? MonitorTypes.SpecificBook : MonitorTypes.None;
                        list.MonitoringOptions.AudiobookMonitorListedBooks = listedSide == BookMediaType.Audiobook;
                        list.MonitoringOptions.EbookMonitorListedBooks = listedSide == BookMediaType.Ebook;
                        list.MonitoringOptions.AudiobookMonitorNewItems = NewItemMonitorTypes.None;
                        list.MonitoringOptions.EbookMonitorNewItems = NewItemMonitorTypes.None;
                        list.MonitoringOptions.ResumePausedAuthors = false;
                    }).Execute(new ImportListSyncCommand(1));
                var request = stored.Pending.GetByProviderId(stored.Author.GoodreadsAuthorId);
                Assert.That(request, Is.Not.Null);
                Assert.Multiple(() =>
                {
                    Assert.That(request.HasAudiobook(), Is.True);
                    Assert.That(request.HasEbook(), Is.True);
                    Assert.That(JsonConvert.DeserializeObject<List<string>>(listedSide == BookMediaType.Audiobook
                        ? request.AudiobookBooksToMonitor : request.EbookBooksToMonitor), Is.EqualTo(new[] { "gr:123" }));
                    Assert.That(JsonConvert.DeserializeObject<List<string>>((listedSide == BookMediaType.Audiobook
                        ? request.EbookBooksToMonitor : request.AudiobookBooksToMonitor) ?? "[]"), Is.Empty);
                });
                stored.Process(request, new Author
                {
                    Name = stored.Author.Name, GoodreadsAuthorId = stored.Author.GoodreadsAuthorId,
                    Books = new() { listed, CatalogBook(listedSide, "Unlisted Work", "124"),
                        CatalogBook(opposite, "Listed Work", "123"), CatalogBook(opposite, "Unlisted Work", "124") },
                    Series = new()
                });
                var remaining = stored.Pending.GetByProviderId(stored.Author.GoodreadsAuthorId);
                Assert.That(remaining, Is.Null, remaining?.LastError);
                var author = stored.Authors.All().Single();
                var selected = stored.Books.GetBooksByAuthor(author.Id).Single(book => book.MediaType == listedSide && book.GoodreadsWorkId == "gr:123");
                var added = new BookAddedService(new NzbDrone.Common.Cache.CacheManager(), stored.Queue, stored.Books, stored.Logger);
                new AuthorScannedHandler(new BookMonitoredService(stored.AuthorService, stored.Books, stored.Logger),
                    stored.AuthorService, stored.Queue, added, stored.Events, stored.Logger, stored.Books)
                    .Handle(new AuthorScannedEvent(author));

                var saved = stored.Books.GetBooksByAuthor(author.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(saved, Has.Count.EqualTo(4));
                    Assert.That(saved.Where(book => book.IsMonitored()).Select(book => book.Id), Is.EqualTo(new[] { selected.Id }));
                    Assert.That(saved.Where(book => book.MediaType == opposite).All(book => !book.IsMonitored()), Is.True);
                    Assert.That(stored.BookRows.GetMissingBookSearchTargets(null, author.Id).Select(target => target.BookId),
                        Is.EqualTo(new[] { selected.Id }));
                    Assert.That(stored.Authors.Get(author.Id).AddOptions, Is.Null);
                    Assert.That(stored.Commands.OfType<BookSearchCommand>(), Is.Empty);
                    Assert.That(stored.Commands.OfType<MissingBookSearchCommand>(), Is.Empty);
                });
            });
        }

        [Test]
        public void specific_book_list_add_should_preserve_only_listed_monitoring_after_scan(
            [Values(false, true)] bool postgres,
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType side,
            [Values(false, true)] bool monitored,
            [Values(false, true)] bool search)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var stored = new StoredListLibrary(database);
                var author = AddSpecificBookListAuthor(stored, side, monitored, search);
                var before = stored.Books.GetBooksByAuthor(author.Id);
                var selected = before.Single(book => book.GoodreadsWorkId == "gr:123");
                var unlisted = before.Single(book => book.GoodreadsWorkId == "gr:124");
                Assert.Multiple(() =>
                {
                    Assert.That(before, Has.Count.EqualTo(2));
                    Assert.That(before.All(book => book.MediaType == side), Is.True);
                    Assert.That(selected.IsMonitored(), Is.True);
                    Assert.That(unlisted.IsMonitored(), Is.False);
                    Assert.That(author.AddOptions, Is.Not.Null);
                    Assert.That(author.AddOptions.Monitor, Is.Null);
                    Assert.That(author.AddOptions.AudiobookMonitor, Is.Null);
                    Assert.That(author.AddOptions.EbookMonitor, Is.Null);
                    Assert.That(author.AddOptions.SearchForMissingBooks, Is.EqualTo(search && monitored));
                });
                var added = new BookAddedService(new NzbDrone.Common.Cache.CacheManager(), stored.Queue, stored.Books, stored.Logger);
                var handler = new AuthorScannedHandler(new BookMonitoredService(stored.AuthorService, stored.Books, stored.Logger),
                    stored.AuthorService, stored.Queue, added, stored.Events, stored.Logger, stored.Books);

                handler.Handle(new AuthorScannedEvent(author));

                var saved = stored.Authors.Get(author.Id);
                var after = stored.Books.GetBooksByAuthor(author.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(after.Where(book => book.IsMonitored()).Select(book => book.Id), Is.EqualTo(new[] { selected.Id }));
                    Assert.That(side == BookMediaType.Audiobook ? saved.AudiobookMonitored : saved.EbookMonitored, Is.EqualTo(monitored));
                    Assert.That(side == BookMediaType.Audiobook ? saved.AudiobookMonitorNewItems : saved.EbookMonitorNewItems,
                        Is.EqualTo(NewItemMonitorTypes.None));
                    Assert.That(saved.AddOptions, Is.Null);
                    Assert.That(stored.Commands.OfType<BookSearchCommand>().SelectMany(command => command.BookIds),
                        Is.EqualTo(search && !monitored ? new[] { selected.Id } : Array.Empty<int>()));
                    Assert.That(stored.Commands.OfType<BookSearchCommand>().All(command => command.Trigger != CommandTrigger.Manual), Is.True);
                    Assert.That(stored.Commands.OfType<MissingBookSearchCommand>().Select(command => command.AuthorId),
                        Is.EqualTo(search && monitored ? new[] { author.Id } : Array.Empty<int>()));
                    Assert.That(stored.Commands.OfType<MissingBookSearchCommand>().Select(command => command.MediaType),
                        Is.EqualTo(search && monitored ? new[] { side == BookMediaType.Audiobook ? "audiobook" : "ebook" } : Array.Empty<string>()));
                    Assert.That(stored.BookRows.GetMissingBookSearchTargets(side, author.Id).Select(target => target.BookId),
                        Is.EqualTo(monitored ? new[] { selected.Id } : Array.Empty<int>()));
                });
                var commandCount = stored.Commands.Count;
                handler.Handle(new AuthorScannedEvent(stored.Authors.Get(author.Id)));
                Assert.That(stored.Commands.Count, Is.EqualTo(commandCount));
            });
        }

        [Test]
        public void specific_book_list_search_should_complete_without_a_post_scan_monitoring_rewrite(
            [Values(false, true)] bool postgres,
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType side)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var completed = new List<int>();
                var stored = new StoredListLibrary(database, message =>
                {
                    if (message is AuthorScanCompletedEvent scan) completed.Add(scan.Author.Id);
                });
                var author = AddSpecificBookListAuthor(stored, side, monitored: true, search: true);
                var monitoringCalls = 0;
                var realMonitoring = new BookMonitoredService(stored.AuthorService, stored.Books, stored.Logger);
                var monitoring = Proxy<IBookMonitoredService>((method, args) =>
                {
                    monitoringCalls++;
                    return method.Invoke(realMonitoring, args);
                });
                var added = new BookAddedService(new NzbDrone.Common.Cache.CacheManager(), stored.Queue, stored.Books, stored.Logger);
                var handler = new AuthorScannedHandler(monitoring, stored.AuthorService, stored.Queue, added,
                    stored.Events, stored.Logger, stored.Books);
                stored.Calls.Clear();

                handler.Handle(new AuthorScannedEvent(author));

                Assert.Multiple(() =>
                {
                    Assert.That(monitoringCalls, Is.Zero);
                    Assert.That(stored.Calls.GetValueOrDefault(nameof(IBookService.SetMonitored)), Is.Zero);
                    Assert.That(stored.Calls.GetValueOrDefault(nameof(IBookService.SetMonitoredForMediaType)), Is.Zero);
                    Assert.That(stored.Calls.GetValueOrDefault(nameof(IBookService.UpdateManyWithLifecycle)), Is.Zero);
                    Assert.That(stored.Commands.OfType<MissingBookSearchCommand>().Single().AuthorId, Is.EqualTo(author.Id));
                    Assert.That(stored.Authors.Get(author.Id).AddOptions, Is.Null);
                    Assert.That(completed, Is.EqualTo(new[] { author.Id }));
                    Assert.That(stored.Books.GetBooksByAuthor(author.Id).Where(book => book.IsMonitored())
                        .Select(book => book.GoodreadsWorkId), Is.EqualTo(new[] { "gr:123" }));
                });
            });
        }

        private static Author AddSpecificBookListAuthor(StoredListLibrary stored, BookMediaType side, bool monitored, bool search)
        {
            stored.Authors.Delete(stored.Author.Id);
            var listed = CatalogBook(side, "Listed Work", "123");
            var reports = new List<NzbDrone.Core.Parser.Model.ImportListItemInfo> { stored.Report(listed, 0) };
            ImportListSyncServiceSearchOnAddFixture.BuildMissingMediaSideService(stored.Author, listed, side, monitored,
                stored.AuthorService, stored.Books, stored.Editions, stored.Pending, stored.Roots, stored.Queue,
                reports: reports, search: search, configureList: list =>
                {
                    list.ShouldMonitor = ImportListMonitorType.SpecificBook;
                    list.MonitoringOptions.AudiobookMonitor = MonitorTypes.SpecificBook;
                    list.MonitoringOptions.EbookMonitor = MonitorTypes.SpecificBook;
                    list.MonitoringOptions.AudiobookMonitorNewItems = NewItemMonitorTypes.None;
                    list.MonitoringOptions.EbookMonitorNewItems = NewItemMonitorTypes.None;
                    list.MonitoringOptions.ResumePausedAuthors = false;
                }).Execute(new ImportListSyncCommand(1));
            var request = stored.Pending.GetByProviderId(stored.Author.GoodreadsAuthorId);
            Assert.That(request, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(stored.Authors.All(), Is.Empty);
                Assert.That(side == BookMediaType.Audiobook ? request.AudiobookMonitorExistingMode : request.EbookMonitorExistingMode,
                    Is.EqualTo(MonitorTypes.SpecificBook));
                Assert.That(JsonConvert.DeserializeObject<List<string>>(side == BookMediaType.Audiobook
                    ? request.AudiobookBooksToMonitor : request.EbookBooksToMonitor), Is.EqualTo(new[] { "gr:123" }));
                Assert.That(request.SearchForMissingBooks, Is.EqualTo(search && monitored));
            });
            stored.Process(request, new Author
            {
                Name = stored.Author.Name, GoodreadsAuthorId = stored.Author.GoodreadsAuthorId,
                Books = new List<Book> { listed, CatalogBook(side, "Unlisted Work", "124") },
                Series = new List<Series>()
            });
            var remaining = stored.Pending.GetByProviderId(stored.Author.GoodreadsAuthorId);
            Assert.That(remaining, Is.Null, remaining?.LastError);
            var author = stored.Authors.All().Single();
            Assert.That(author.AddOptions?.AwaitingMetadata, Is.False);
            Assert.That(stored.Commands.OfType<MissingBookSearchCommand>(), Is.Empty,
                "The author search must wait until its initial scan completes.");
            return author;
        }
    }
}
