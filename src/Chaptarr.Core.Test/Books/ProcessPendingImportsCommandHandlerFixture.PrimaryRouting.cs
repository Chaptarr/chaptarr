using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.BookInfo;

namespace Chaptarr.Core.Test.Books
{
    public partial class ProcessPendingImportsCommandHandlerFixture
    {
        [Test]
        public void pending_primary_routing_keeps_origin_author_demand_and_batches_destination_catalogs(
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media,
            [Values(1, 20)] int count)
        {
            var source = new Author { Id = 42, Name = "Reported Contributor", HardcoverAuthorId = "hc:2222", AddOptions = new AddAuthorOptions() };
            var selected = new Author { Id = 43, Name = "Selected Primary", HardcoverAuthorId = "hc:9001", AddOptions = new AddAuthorOptions() };
            var pending = Pending(205);
            pending.ProviderId = source.HardcoverAuthorId;
            pending.DiscoveredAuthorFolderPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "reported-contributor");
            pending.AudiobookStatus = media == BookMediaType.Audiobook ? PendingImportStatus.Pending : PendingImportStatus.NotRequested;
            pending.EbookStatus = media == BookMediaType.Ebook ? PendingImportStatus.Pending : PendingImportStatus.NotRequested;
            var ids = Enumerable.Range(0, count).Select(i => $"hc:{7000 + i}").ToList();
            if (media == BookMediaType.Audiobook)
            {
                pending.AudiobookBooksToMonitor = pending.AudiobookBooksToSearch = JsonConvert.SerializeObject(ids);
                pending.AudiobookMonitorExistingMode = MonitorTypes.All;
                pending.AudiobookMonitorNewItems = NewItemMonitorTypes.All;
            }
            else
            {
                pending.EbookBooksToMonitor = pending.EbookBooksToSearch = JsonConvert.SerializeObject(ids);
                pending.EbookMonitorExistingMode = MonitorTypes.All;
                pending.EbookMonitorNewItems = NewItemMonitorTypes.All;
            }
            pending.SearchForMissingBooks = true;
            var pendingService = new StubPendingAuthorImportService();
            pendingService.DueResponses.Enqueue(new List<PendingAuthorImport> { pending });
            var books = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var bookProxy = (BookServiceProxy)books;
            bookProxy.Books = ids.Select((id, index) => new Book { Id = 101 + index, AuthorId = selected.Id, Author = selected,
                HardcoverBookId = id, MediaType = media }).Append(new Book { Id = 999, AuthorId = selected.Id, Author = selected,
                    HardcoverBookId = ids[0], MediaType = media == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook }).ToList();
            var info = DispatchProxy.Create<IProvideBookInfo, BookInfoProxy>();
            ((BookInfoProxy)info).Work = (id, side) => Tuple.Create(selected.HardcoverAuthorId,
                new Book { HardcoverBookId = id, MediaType = side, Author = selected }, new List<Author> { source, selected });
            var library = DispatchProxy.Create<IAuthorLibraryService, AuthorLibraryProxy>();
            var imports = (AuthorLibraryProxy)library;
            imports.Add = (id, config) =>
            {
                if (id == source.HardcoverAuthorId)
                {
                    Assert.That(media == BookMediaType.Audiobook ? config.AudiobookMonitorExistingMode : config.EbookMonitorExistingMode, Is.EqualTo(MonitorTypes.All));
                    Assert.That(media == BookMediaType.Audiobook ? config.AudiobookMonitorNewItems : config.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.All));
                    Assert.That(config.SearchForMissingBooks, Is.True);
                    return source;
                }
                Assert.That(id, Is.EqualTo(selected.HardcoverAuthorId));
                Assert.That(media == BookMediaType.Audiobook ? config.AudiobookMonitorExistingMode : config.EbookMonitorExistingMode, Is.EqualTo(MonitorTypes.SpecificBook));
                Assert.That(media == BookMediaType.Audiobook ? config.AudiobookMonitorNewItems : config.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
                Assert.That(media == BookMediaType.Audiobook ? config.AudiobookBooksToMonitor : config.EbookBooksToMonitor, Is.EquivalentTo(ids));
                Assert.That(config.SearchForMissingBooks, Is.False);
                Assert.That(config.CreateAudiobook, Is.EqualTo(media == BookMediaType.Audiobook));
                Assert.That(config.CreateEbook, Is.EqualTo(media == BookMediaType.Ebook));
                return selected;
            };
            var queue = new RecordingCommandQueue();
            var events = new RecordingEventAggregator();
            var handler = new ProcessPendingImportsCommandHandler(pendingService, library, books, info, queue, events, LogManager.GetCurrentClassLogger());
            handler.Execute(new ProcessPendingImportsCommand());
            Assert.That(imports.ProviderIds, Is.EqualTo(new[] { source.HardcoverAuthorId }));
            Assert.That(pendingService.QueuedRequests.Single().ProviderId, Is.EqualTo(selected.HardcoverAuthorId));
            handler.Execute(new ProcessPendingImportsCommand());

            Assert.Multiple(() =>
            {
                Assert.That(imports.ProviderIds, Is.EqualTo(new[] { source.HardcoverAuthorId, selected.HardcoverAuthorId }));
                Assert.That(((BookInfoProxy)info).RequestedWorkIds, Is.EquivalentTo(ids));
                Assert.That(bookProxy.CatalogLoads, Is.EqualTo(2));
                Assert.That(pendingService.DeletedIds, Does.Contain(pending.Id), pending.LastError);
                Assert.That(queue.Pushed.OfType<MissingBookSearchCommand>().Select(command => command.AuthorId), Is.EqualTo(new[] { source.Id }));
                Assert.That(queue.Pushed.OfType<BookSearchCommand>().SelectMany(command => command.BookIds), Is.EquivalentTo(Enumerable.Range(101, count)));
                Assert.That(queue.Pushed.OfType<RefreshAuthorCommand>().Select(command => command.AuthorId), Is.EquivalentTo(new[] { source.Id, selected.Id }));
                Assert.That(events.Events.OfType<PendingAuthorImportSucceededEvent>().Select(message => message.ImportedAuthor.Id), Is.EqualTo(new[] { source.Id, selected.Id }));
                Assert.That(events.Events.OfType<PendingAuthorImportSucceededEvent>().Single(message => message.ImportedAuthor.Id == source.Id).PendingImport.DiscoveredAuthorFolderPath,
                    Is.EqualTo(pending.DiscoveredAuthorFolderPath));
                Assert.That(events.Events.OfType<PendingAuthorImportSucceededEvent>().Single(message => message.ImportedAuthor.Id == selected.Id).PendingImport.DiscoveredAuthorFolderPath, Is.Null);
                Assert.That(bookProxy.Books.Single(book => book.Id == 999).IsMonitored(), Is.False);
                Assert.That(bookProxy.Books.Where(book => book.Id != 999).All(book => book.Author.Id == selected.Id), Is.True);
            });
        }

        [TestCase("wrong-work")]
        [TestCase("opposite-media")]
        [TestCase("conflicting-primary")]
        public void pending_primary_routing_retains_unproved_requested_work(string difference)
        {
            var pending = Pending(205);
            pending.EbookBooksToMonitor = @"[""hc:7000""]";
            var pendingService = new StubPendingAuthorImportService();
            pendingService.DueResponses.Enqueue(new List<PendingAuthorImport> { pending });
            var info = DispatchProxy.Create<IProvideBookInfo, BookInfoProxy>();
            var selected = new Author { HardcoverAuthorId = "hc:9001" };
            ((BookInfoProxy)info).Work = (id, side) => Tuple.Create("hc:9001", new Book
            {
                HardcoverBookId = difference == "wrong-work" ? "hc:7001" : id,
                MediaType = difference == "opposite-media" ? BookMediaType.Audiobook : side,
                Author = difference == "conflicting-primary" ? new Author { HardcoverAuthorId = "hc:2222" } : selected
            }, new List<Author> { selected });
            var queue = new RecordingCommandQueue();
            BuildHandler(pendingService, queue, new RecordingEventAggregator(), bookInfo: info).Execute(new ProcessPendingImportsCommand());
            Assert.That(pendingService.DeletedIds, Is.Empty);
            Assert.That(pendingService.RetryReasons.Count, Is.EqualTo(1));
            Assert.That(queue.Pushed.OfType<BookSearchCommand>(), Is.Empty);
        }

        [TestCase("work-lookup-outage")]
        [TestCase("author-not-ready")]
        [TestCase("author-terminal")]
        [TestCase("catalog-outage")]
        public void pending_primary_failure_before_healthy_group_does_not_block_healthy_work(string failure)
        {
            var source = new Author { Id = 42, HardcoverAuthorId = "hc:2222" };
            var failed = new Author { Id = 43, HardcoverAuthorId = "hc:9002" };
            var healthy = new Author { Id = 44, HardcoverAuthorId = "hc:9001" };
            var pending = Pending(205);
            pending.ProviderId = source.HardcoverAuthorId;
            pending.EbookBooksToMonitor = pending.EbookBooksToSearch = @"[""hc:7000"",""hc:7001""]";
            var pendingService = new StubPendingAuthorImportService();
            pendingService.DueResponses.Enqueue(new List<PendingAuthorImport> { pending });
            var info = DispatchProxy.Create<IProvideBookInfo, BookInfoProxy>();
            ((BookInfoProxy)info).Work = (id, side) =>
            {
                if (id == "hc:7000" && failure == "work-lookup-outage") throw new BookInfoException("Injected unavailable lookup");
                var primary = id == "hc:7000" ? failed : healthy;
                return Tuple.Create(primary.HardcoverAuthorId, new Book { HardcoverBookId = id, MediaType = side, Author = primary }, new List<Author> { primary });
            };
            var library = DispatchProxy.Create<IAuthorLibraryService, AuthorLibraryProxy>();
            ((AuthorLibraryProxy)library).Add = (id, config) =>
            {
                if (id == source.HardcoverAuthorId) return source;
                if (id == failed.HardcoverAuthorId)
                {
                    if (failure == "author-not-ready") throw new AuthorNotFoundException(id);
                    if (failure == "author-terminal") throw new AuthorTerminalException("author_no_primary_works", id, id, "Injected terminal", false);
                    return failed;
                }
                return healthy;
            };
            var local = new Book { Id = 101, AuthorId = healthy.Id, Author = healthy, HardcoverBookId = "hc:7001", MediaType = BookMediaType.Ebook };
            var books = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)books).Books.Add(local);
            var observedBooks = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IBookService>((method, args) =>
            {
                if (failure == "catalog-outage" && method.Name == nameof(IBookService.GetBooksByAuthor) && (int)args[0] == failed.Id)
                    throw new InvalidOperationException("Injected catalog failure");
                return method.Invoke(books, args);
            });
            var queue = new RecordingCommandQueue();
            var handler = new ProcessPendingImportsCommandHandler(pendingService, library, observedBooks, info, queue, new RecordingEventAggregator(), LogManager.GetCurrentClassLogger());
            handler.Execute(new ProcessPendingImportsCommand());
            while (pendingService.DueResponses.Count > 0) handler.Execute(new ProcessPendingImportsCommand());
            Assert.That(queue.Pushed.OfType<BookSearchCommand>().SelectMany(command => command.BookIds), Is.EqualTo(new[] { local.Id }));
            Assert.That(local.IsMonitored(), Is.True);
            Assert.That(pending.EbookBooksToSearch, Is.EqualTo(failure == "work-lookup-outage" ? @"[""hc:7000""]" : "[]"));
            Assert.That(pending.OverallStatus, Is.EqualTo(failure == "work-lookup-outage" ? PendingImportStatus.Retrying : PendingImportStatus.Succeeded));
            Assert.That(pendingService.DeletedIds.Contains(pending.Id), Is.EqualTo(failure != "work-lookup-outage"));
            if (failure != "work-lookup-outage")
                Assert.That(pendingService.QueuedRequests.Single(request => request.ProviderId == failed.HardcoverAuthorId).OverallStatus,
                    Is.EqualTo(failure == "author-terminal" ? PendingImportStatus.Failed : PendingImportStatus.Retrying));
        }
    }
}
