using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Chaptarr.Api.V1.Queue;
using Chaptarr.Core.Test.Common;
using Chaptarr.Http.ErrorManagement;
using Dapper;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Core.Blocklisting;
using NzbDrone.Common.Crypto;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Aggregation;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Housekeeping.Housekeepers;
using NzbDrone.Core.Jobs;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Delay;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Queue;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Queue
{
    [TestFixture]
    public class QueuePendingActionsFixture
    {
        public sealed class Observed
        {
            public int Reads;
            public int Deletes;
            public int Events;
        }

        public static PendingReleaseService Service(IMainDatabase database, Author author, List<Book> books, Observed observed)
        {
            var events = Proxy<IEventAggregator>((method, args) => { observed.Events++; return null; });
            var native = new PendingReleaseRepository(database, events);
            var repository = Proxy<IPendingReleaseRepository>((method, args) =>
            {
                if (method.Name is "All" or "WithoutFallback" or "AllByAuthorId") observed.Reads++;
                if (method.Name is "DeleteMany" or "Delete") observed.Deletes++;
                return method.Invoke(native, args);
            });
            var authors = Proxy<IAuthorService>((method, args) => new List<Author> { author });
            var bookService = Proxy<IBookService>((method, args) => books.Where(book => ((IEnumerable<int>)args[0]).Contains(book.Id)).ToList());
            var parsing = Proxy<IParsingService>((method, args) => new List<List<Book>>(((IReadOnlyList<ParsedBookInfo>)args[0]).Select(_ => new List<Book>())));
            var delays = Proxy<IDelayProfileService>((method, args) => method.Name == "BestForTags"
                ? new DelayProfile { EnableUsenet = true, PreferredProtocol = DownloadProtocol.Usenet }
                : new List<DelayProfile> { new() { EnableUsenet = true, PreferredProtocol = DownloadProtocol.Usenet } });
            var config = Proxy<IConfigService>((method, args) => method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null);
            return new PendingReleaseService(null, repository, authors, parsing, delays,
                Proxy<ITaskManager>((method, args) => DateTime.UtcNow.AddMinutes(5)), config,
                Proxy<ICustomFormatCalculationService>((method, args) => new List<CustomFormat>()),
                Proxy<IRemoteBookAggregationService>((method, args) => null), null,
                Proxy<IIndexerFactory>((method, args) => null), events, LogManager.GetCurrentClassLogger(), bookService);
        }

        public static PendingRelease Insert(PendingReleaseRepository repository, Author author, string title, PendingReleaseReason reason, params int[] bookIds)
        {
            return repository.Insert(new PendingRelease
            {
                AuthorId = author.Id, Title = title, Reason = reason, Added = DateTime.UtcNow,
                Release = new ReleaseInfo { Title = title, Indexer = "Test Indexer", IndexerId = 7, PublishDate = DateTime.UtcNow.AddHours(-1), DownloadProtocol = DownloadProtocol.Usenet },
                ParsedBookInfo = new ParsedBookInfo { ReleaseTitle = title, AuthorName = author.Name, BookTitle = "Paper Lanterns", Quality = new QualityModel(Quality.M4B) },
                AdditionalInfo = new PendingReleaseAdditionalInfo { BookIds = bookIds.ToList() }
            });
        }

        private static QualityProfileService Profiles()
        {
            var repository = Proxy<IProfileRepository>((method, args) => new List<QualityProfile>
            {
                new() { Id = 1, Items = new List<QualityProfileQualityItem> { new() { Quality = Quality.M4B, Allowed = true } } }
            });
            return new QualityProfileService(repository, null, null, null, null, null, LogManager.GetCurrentClassLogger());
        }

        private static (Author Author, List<Book> Books, PendingReleaseRepository Repository) CollidingRows(IMainDatabase database, bool fallback)
        {
            var author = new Author { Id = 38, Name = "Test Writer" };
            var books = new[] { 15551, 5675, 503 }.Select(id => new Book
            {
                Id = id, AuthorId = author.Id, Title = "Paper Lanterns", MediaType = BookMediaType.Audiobook, Editions = new List<Edition>()
            }).ToList();
            var repository = new PendingReleaseRepository(database, Proxy<IEventAggregator>((method, args) => null));
            var first = Insert(repository, author, "first-target", fallback ? PendingReleaseReason.Fallback : PendingReleaseReason.Delay, books[0].Id);
            var second = Insert(repository, author, "second-target", PendingReleaseReason.Delay, books[1].Id);
            Assert.That(HashConverter.GetHashInt31($"pending-{first.Id}-book{books[0].Id}"),
                Is.EqualTo(HashConverter.GetHashInt31($"pending-{second.Id}-book{books[1].Id}")));
            return (author, books, repository);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void bulk_removal_should_ignore_unselected_duplicate_visible_ids(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var (author, books, native) = CollidingRows(database, false);
                Insert(native, author, "selected-target", PendingReleaseReason.Delay, books[2].Id);
                var service = Service(database, author, books, new Observed());
                var queue = service.GetPendingQueue();
                Assert.That(queue.Count(item => item.Id == HashConverter.GetHashInt31("pending-1-book15551")), Is.EqualTo(2));
                var selected = queue.Single(item => item.Book.Id == books[2].Id);
                var controller = new QueueController(global::Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<global::NzbDrone.Core.Books.ISeriesBookLinkService>((_, _) => throw new global::System.InvalidOperationException("Series batch loading was not expected in this fixture")), Proxy<IDownloadImportModeResolver>((method, args) => false), null, null, service, Profiles(), Proxy<ITrackedDownloadService>((method, args) => null), null, null, null, null, null);
                controller.RemoveMany(new QueueBulkResource { Ids = new List<int> { selected.Id } });
                Assert.That(native.All().Select(row => row.Title), Is.EquivalentTo(new[] { "first-target", "second-target" }));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void removal_should_use_the_selected_book_instead_of_a_hidden_colliding_target(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var (author, books, native) = CollidingRows(database, true);
                var service = Service(database, author, books, new Observed());
                var visible = service.GetPendingQueue().Single();
                Assert.That(visible.Book.Id, Is.EqualTo(books[1].Id));
                var controller = new QueueController(global::Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<global::NzbDrone.Core.Books.ISeriesBookLinkService>((_, _) => throw new global::System.InvalidOperationException("Series batch loading was not expected in this fixture")), Proxy<IDownloadImportModeResolver>((method, args) => false), null, null, service, Profiles(), null, null, null, null, null, null);
                controller.RemoveAction(visible.Id);
                Assert.That(native.All().Select(row => row.Title), Is.EqualTo(new[] { "first-target" }));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void removal_should_use_the_selected_unresolved_row_instead_of_a_hidden_colliding_row(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var author = new Author { Id = 38, Name = "Test Writer" };
                var native = new PendingReleaseRepository(database, Proxy<IEventAggregator>((method, args) => null));
                void AdvanceSequence(int nextId)
                {
                    using var connection = database.OpenConnection();
                    if (postgres)
                    {
                        connection.Execute("SELECT setval(pg_get_serial_sequence('\"PendingReleases\"', 'Id'), @NextId, false)", new { NextId = nextId });
                    }
                    else
                    {
                        var updated = connection.Execute("UPDATE sqlite_sequence SET seq = @PreviousId WHERE name = 'PendingReleases'", new { PreviousId = nextId - 1 });
                        if (updated == 0) connection.Execute("INSERT INTO sqlite_sequence(name, seq) VALUES ('PendingReleases', @PreviousId)", new { PreviousId = nextId - 1 });
                    }
                }

                AdvanceSequence(3336);
                var hidden = Insert(native, author, "hidden-unresolved", PendingReleaseReason.Fallback);
                AdvanceSequence(12181);
                var selected = Insert(native, author, "selected-unresolved", PendingReleaseReason.Delay);
                Assert.That(hidden.Id, Is.EqualTo(3336));
                Assert.That(selected.Id, Is.EqualTo(12181));

                Assert.That(HashConverter.GetHashInt31("pending-3336-book0"), Is.EqualTo(HashConverter.GetHashInt31("pending-12181-book0")));
                var observed = new Observed();
                var service = Service(database, author, new List<Book>(), observed);
                var visible = service.GetPendingQueue().Single();
                Assert.That(visible.Book, Is.Null);
                var controller = new QueueController(global::Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<global::NzbDrone.Core.Books.ISeriesBookLinkService>((_, _) => throw new global::System.InvalidOperationException("Series batch loading was not expected in this fixture")), Proxy<IDownloadImportModeResolver>((method, args) => false), null, null, service, Profiles(), null, null, null, null, null, null);
                controller.RemoveAction(visible.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(native.All().Select(row => row.Id), Is.EqualTo(new[] { 3336 }));
                    Assert.That(observed.Deletes, Is.EqualTo(1));
                    Assert.That(observed.Events, Is.EqualTo(1));
                });
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void cleanup_should_warn_once_when_an_orphan_is_deleted_and_preserve_healthy_rows(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var events = Proxy<IEventAggregator>((method, args) => null);
                var author = new AuthorRepository(database, events, LogManager.GetCurrentClassLogger()).Insert(new Author { Name = "Test Writer", CleanName = "testwriter", SortName = "testwriter", SortNameLastFirst = "Writer, Test", TitleSlug = "test-writer", Added = DateTime.UtcNow, Path = "/test-writer" });
                var native = new PendingReleaseRepository(database, events);
                var healthy = Insert(native, author, "healthy", PendingReleaseReason.Delay);
                var orphan = Insert(native, new Author { Id = 999999, Name = "Missing Writer" }, "orphan", PendingReleaseReason.Delay);
                using var factory = new LogFactory();
                var target = new MemoryTarget { Layout = "${level}|${message}" };
                var config = new LoggingConfiguration();
                config.AddRule(LogLevel.Warn, LogLevel.Fatal, target);
                factory.Configuration = config;
                var logger = factory.GetLogger("orphan-cleanup");
                var constructor = typeof(CleanupOrphanedPendingReleases).GetConstructors().Single();
                var arguments = constructor.GetParameters().Length == 1 ? new object[] { database } : new object[] { database, logger };
                var cleanup = (CleanupOrphanedPendingReleases)constructor.Invoke(arguments);
                cleanup.Clean();
                Assert.That(target.Logs.Count, Is.EqualTo(1));
                Assert.That(target.Logs.Single(), Does.Contain(orphan.Id.ToString()));
                cleanup.Clean();
                Assert.That(target.Logs.Count, Is.EqualTo(1));
                Assert.That(native.All().Select(row => row.Id), Is.EqualTo(new[] { healthy.Id }));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void a_later_blocklist_failure_should_keep_earlier_successful_removals(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var author = new Author { Id = 38, Name = "Test Writer" };
                var books = new[] { 501, 502 }.Select(id => new Book
                {
                    Id = id, AuthorId = author.Id, Title = "Paper Lanterns", HardcoverBookId = "hc:" + id, MediaType = BookMediaType.Audiobook, Editions = new List<Edition>()
                }).ToList();
                var events = Proxy<IEventAggregator>((method, args) => null);
                var pending = new PendingReleaseRepository(database, events);
                Insert(pending, author, "first-target", PendingReleaseReason.Delay, books[0].Id);
                Insert(pending, author, "second-target", PendingReleaseReason.Delay, books[1].Id);
                var service = Service(database, author, books, new Observed());
                var ids = service.GetPendingQueue().OrderBy(item => item.Book.Id).Select(item => item.Id).ToList();
                var blocklist = new BlocklistRepository(database, events);
                var nativeBlocklist = new BlocklistService(blocklist, null, null, LogManager.GetCurrentClassLogger());
                var calls = 0;
                var failingBlocklist = Proxy<IBlocklistService>((method, args) =>
                {
                    calls++;
                    if (calls == 2) throw new InvalidOperationException("Blocklist write unavailable");
                    return method.Invoke(nativeBlocklist, args);
                });
                var controller = new QueueController(global::Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<global::NzbDrone.Core.Books.ISeriesBookLinkService>((_, _) => throw new global::System.InvalidOperationException("Series batch loading was not expected in this fixture")), Proxy<IDownloadImportModeResolver>((method, args) => false), null, null, service, Profiles(), null, null, null, null, failingBlocklist, null);
                Assert.Throws<InvalidOperationException>(() => controller.RemoveMany(new QueueBulkResource { Ids = ids }, blocklist: true));
                Assert.Multiple(() =>
                {
                    Assert.That(calls, Is.EqualTo(2));
                    Assert.That(blocklist.All().Count, Is.EqualTo(1));
                    Assert.That(pending.All().Select(row => row.Title), Is.EqualTo(new[] { "second-target" }));
                });
            });
        }

        [TestCase(false, 1)]
        [TestCase(false, 25)]
        [TestCase(true, 1)]
        [TestCase(true, 25)]
        public void bulk_removal_should_batch_reads_and_delete_all_candidates_for_only_the_selected_targets(bool postgres, int count)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var author = new Author { Id = 38, Name = "Test Writer" };
                var books = Enumerable.Range(501, count + 2).Select(id => new Book
                {
                    Id = id, AuthorId = author.Id, Title = "Paper Lanterns", MediaType = BookMediaType.Audiobook, Editions = new List<Edition>()
                }).ToList();
                books.Last().MediaType = BookMediaType.Ebook;
                var native = new PendingReleaseRepository(database, Proxy<IEventAggregator>((method, args) => null));
                foreach (var book in books.Take(count))
                {
                    Insert(native, author, "visible-" + book.Id, PendingReleaseReason.Delay, book.Id);
                    Insert(native, author, "hidden-" + book.Id, PendingReleaseReason.Delay, book.Id);
                    Insert(native, author, "fallback-" + book.Id, PendingReleaseReason.Fallback, book.Id);
                }

                var copy = Insert(native, author, "independent-copy", PendingReleaseReason.Delay, books[count].Id);
                var ebook = Insert(native, author, "other-media", PendingReleaseReason.Delay, books.Last().Id);
                var unknown = Insert(native, author, "unresolved", PendingReleaseReason.Delay);
                var observed = new Observed();
                var service = Service(database, author, books, observed);
                var ids = service.GetPendingQueue().Where(row => row.Book != null && books.Take(count).Any(book => book.Id == row.Book.Id)).Select(row => row.Id).ToList();
                observed.Reads = 0;
                observed.Deletes = 0;
                var controller = new QueueController(global::Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<global::NzbDrone.Core.Books.ISeriesBookLinkService>((_, _) => throw new global::System.InvalidOperationException("Series batch loading was not expected in this fixture")), Proxy<IDownloadImportModeResolver>((method, args) => false), null, Proxy<IQueueService>((method, args) => null), service, Profiles(),
                    Proxy<ITrackedDownloadService>((method, args) => null), null, null, null,
                    Proxy<IBlocklistService>((method, args) => throw new AssertionException("No blocklist was requested")), null);
                var timer = Stopwatch.StartNew();
                controller.RemoveMany(new QueueBulkResource { Ids = ids.Concat(ids).ToList() });
                var remaining = native.All().Select(row => row.Id).ToList();
                TestContext.Progress.WriteLine($"PENDING_BULK database={database.DatabaseType} selected={count} pendingReads={observed.Reads} deleteActions={observed.Deletes} remaining={remaining.Count} elapsedMs={timer.Elapsed.TotalMilliseconds:F3}");
                Assert.Multiple(() =>
                {
                    Assert.That(remaining, Is.EquivalentTo(new[] { copy.Id, ebook.Id, unknown.Id }));
                    Assert.That(observed.Reads, Is.EqualTo(2));
                    Assert.That(observed.Deletes, Is.EqualTo(1));
                });
            });
        }

        [TestCase(false, false, 0)]
        [TestCase(false, true, 0)]
        [TestCase(true, false, 0)]
        [TestCase(true, true, 0)]
        [TestCase(false, false, 1)]
        [TestCase(false, true, 1)]
        [TestCase(true, false, 1)]
        [TestCase(true, true, 1)]
        [TestCase(false, false, 2)]
        [TestCase(false, true, 2)]
        [TestCase(true, false, 2)]
        [TestCase(true, true, 2)]
        public void pending_grab_should_return_a_client_error_without_downloading_unresolved_rows(bool postgres, bool bulk, int targetState)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var author = new Author { Id = 38, Name = "Test Writer" };
                var books = new List<Book> { new() { Id = 501, AuthorId = author.Id, MediaType = BookMediaType.Audiobook, Editions = new List<Edition>() } };
                var native = new PendingReleaseRepository(database, Proxy<IEventAggregator>((method, args) => null));
                Insert(native, author, "Test Writer - Paper Lanterns [M4B]", PendingReleaseReason.Delay, targetState == 2 ? new[] { 501 } : Array.Empty<int>());
                var service = Service(database, author, books, new Observed());
                var id = targetState == 0 ? -1 : service.GetPendingQueue().Single().Id;
                var downloads = 0;
                var downloader = Proxy<IDownloadService>((method, args) =>
                {
                    downloads++;
                    if (((RemoteBook)args[0]).Books.Count == 0) throw new InvalidOperationException("Download target is empty");
                    return Task.CompletedTask;
                });
                var controller = CreateGrabController(service, downloader);
                Exception error = null;
                try
                {
                    if (bulk) controller.Grab(new QueueBulkResource { Ids = new List<int> { id } }).GetAwaiter().GetResult();
                    else controller.Grab(id).GetAwaiter().GetResult();
                }
                catch (Exception e) { error = e; }

                var status = 200;
                if (error != null)
                {
                    var context = new DefaultHttpContext();
                    context.Response.Body = new MemoryStream();
                    context.Features.Set<IExceptionHandlerPathFeature>(new ExceptionHandlerFeature { Error = error, Path = "/api/v1/queue/grab" });
                    new ChaptarrErrorPipeline(LogManager.GetCurrentClassLogger()).HandleException(context).GetAwaiter().GetResult();
                    status = context.Response.StatusCode;
                }

                Assert.Multiple(() =>
                {
                    Assert.That(status, Is.EqualTo(targetState == 0 ? 404 : targetState == 1 ? 400 : 200));
                    Assert.That(downloads, Is.EqualTo(targetState == 2 ? 1 : 0));
                    Assert.That(native.All().Count, Is.EqualTo(1));
                });
            });
        }

        internal static QueueActionController CreateGrabController(IPendingReleaseService pending, IDownloadService downloader)
        {
            var constructor = typeof(QueueActionController).GetConstructors().Single();
            object processor = constructor.GetParameters()[1].ParameterType == typeof(IDownloadService)
                ? downloader : new ProcessDownloadDecisions(downloader, null, pending, new Chaptarr.Core.Test.Download.EmptyQueueSpecification(), LogManager.GetCurrentClassLogger());
            return (QueueActionController)constructor.Invoke(new object[] { pending, processor });
        }

        private static int StatusFor(Exception error)
        {
            if (error == null) return 200;
            var context = new DefaultHttpContext(); context.Response.Body = new MemoryStream();
            context.Features.Set<IExceptionHandlerPathFeature>(new ExceptionHandlerFeature { Error = error, Path = "/api/v1/queue" });
            new ChaptarrErrorPipeline(LogManager.GetCurrentClassLogger()).HandleException(context).GetAwaiter().GetResult();
            return context.Response.StatusCode;
        }

        [TestCase(false, false)] [TestCase(true, false)]
        [TestCase(false, true)] [TestCase(true, true)]
        public void selected_visible_hash_collision_should_be_a_conflict_without_download_or_removal(bool postgres, bool bulk)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var (author, books, repository) = CollidingRows(database, false);
                var service = Service(database, author, books, new Observed());
                var id = service.GetPendingQueue().First().Id;
                var downloads = 0;
                var grab = CreateGrabController(service, Proxy<IDownloadService>((method, args) => { downloads++; return Task.CompletedTask; }));
                Exception failure = null;
                try
                {
                    if (bulk) grab.Grab(new QueueBulkResource { Ids = new() { id } }).GetAwaiter().GetResult();
                    else grab.Grab(id).GetAwaiter().GetResult();
                }
                catch (Exception error) { failure = error; }
                Assert.That(StatusFor(failure), Is.EqualTo(409));
                Assert.That(downloads, Is.Zero);
                var controller = new QueueController(global::Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<global::NzbDrone.Core.Books.ISeriesBookLinkService>((_, _) => throw new global::System.InvalidOperationException("Series batch loading was not expected in this fixture")), Proxy<IDownloadImportModeResolver>((method, args) => false), null, null, service, Profiles(), null, null, null, null, null, null);
                failure = null;
                try
                {
                    if (bulk) controller.RemoveMany(new QueueBulkResource { Ids = new() { id } });
                    else controller.RemoveAction(id);
                }
                catch (Exception error) { failure = error; }
                Assert.That(StatusFor(failure), Is.EqualTo(409));
                Assert.That(repository.All(), Has.Count.EqualTo(2));
            });
        }

        [TestCase(false, false)] [TestCase(true, false)]
        [TestCase(false, true)] [TestCase(true, true)]
        public void manual_grab_should_refuse_an_incomplete_required_collection(bool postgres, bool bulk)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var author = new Author { Id = 38, Name = "Test Writer" };
                var book = new Book { Id = 501, AuthorId = author.Id, MediaType = BookMediaType.Audiobook, Editions = new() };
                var repository = new PendingReleaseRepository(database, Proxy<IEventAggregator>((method, args) => null));
                Insert(repository, author, "required", PendingReleaseReason.Delay, book.Id, 999);
                var service = Service(database, author, new() { book }, new Observed());
                var id = service.GetPendingQueue().Single().Id;
                var downloads = 0;
                var controller = CreateGrabController(service, Proxy<IDownloadService>((method, args) => { downloads++; return Task.CompletedTask; }));
                Exception failure = null;
                try
                {
                    if (bulk) controller.Grab(new QueueBulkResource { Ids = new() { id } }).GetAwaiter().GetResult();
                    else controller.Grab(id).GetAwaiter().GetResult();
                }
                catch (Exception error) { failure = error; }
                Assert.That(StatusFor(failure), Is.EqualTo(400));
                Assert.That(downloads, Is.Zero);
                Assert.That(repository.All().Single().AdditionalInfo.BookIds, Is.EqualTo(new[] { book.Id, 999 }));
            });
        }

        [TestCase(false)] [TestCase(true)]
        public void bulk_grab_should_validate_all_selected_rows_before_any_download(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var author = new Author { Id = 38, Name = "Test Writer" };
                var book = new Book { Id = 501, AuthorId = author.Id, MediaType = BookMediaType.Audiobook, Editions = new() };
                var repository = new PendingReleaseRepository(database, Proxy<IEventAggregator>((method, args) => null));
                Insert(repository, author, "known", PendingReleaseReason.Delay, book.Id);
                Insert(repository, author, "unknown", PendingReleaseReason.Delay);
                var service = Service(database, author, new() { book }, new Observed());
                var ids = service.GetPendingQueue().OrderByDescending(row => row.Book != null).Select(row => row.Id).ToList();
                var downloads = 0;
                var controller = CreateGrabController(service, Proxy<IDownloadService>((method, args) => { downloads++; return Task.CompletedTask; }));
                Assert.ThrowsAsync<Chaptarr.Http.REST.BadRequestException>(async () => await controller.Grab(new QueueBulkResource { Ids = ids }));
                Assert.That(downloads, Is.Zero);
                Assert.That(repository.All(), Has.Count.EqualTo(2));
            });
        }

        [TestCase(false, BookMediaType.Audiobook, false)] [TestCase(true, BookMediaType.Audiobook, false)]
        [TestCase(false, BookMediaType.Ebook, false)] [TestCase(true, BookMediaType.Ebook, false)]
        [TestCase(false, BookMediaType.Audiobook, true)] [TestCase(true, BookMediaType.Audiobook, true)]
        [TestCase(false, BookMediaType.Ebook, true)] [TestCase(true, BookMediaType.Ebook, true)]
        public void manual_selected_alternatives_should_share_one_dispatch_without_adding_unselected_copies(bool postgres, BookMediaType media, bool onlyFirst)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var author = new Author { Id = 38, Name = "Test Writer" };
                var books = new[] { 501, 502, 503 }.Select(id => new Book
                { Id = id, AuthorId = author.Id, MediaType = media, HardcoverBookId = "hc:8200", Editions = new() }).ToList();
                var repository = new PendingReleaseRepository(database, Proxy<IEventAggregator>((method, args) => null));
                foreach (var book in books)
                {
                    var row = Insert(repository, author, "one-release", PendingReleaseReason.Delay, book.Id);
                    row.Release.Guid = "same-physical-release";
                    row.ParsedBookInfo.Quality = new QualityModel(media == BookMediaType.Audiobook ? Quality.M4B : Quality.EPUB);
                    row.AdditionalInfo.AlternativeCopies = true; row.AdditionalInfo.TargetEditionIds[book.Id] = book.Id + 1000;
                    repository.Update(row);
                }
                var service = Service(database, author, books, new Observed());
                var selected = service.GetPendingQueue().Where(row => row.Book.Id == 501 || !onlyFirst && row.Book.Id == 502).Select(row => row.Id).ToList();
                var sent = new List<RemoteBook>();
                var controller = CreateGrabController(service, Proxy<IDownloadService>((method, args) => { sent.Add((RemoteBook)args[0]); return Task.CompletedTask; }));
                controller.Grab(new QueueBulkResource { Ids = selected }).GetAwaiter().GetResult();
                Assert.That(sent, Has.Count.EqualTo(1));
                Assert.That(sent.Single().Books.Select(book => book.Id), Is.EquivalentTo(onlyFirst ? new[] { 501 } : new[] { 501, 502 }));
                Assert.That(sent.Single().TargetEditionIds.Keys, Is.EquivalentTo(onlyFirst ? new[] { 501 } : new[] { 501, 502 }));
                Assert.That(repository.All(), Has.Count.EqualTo(3));
            });
        }

        [TestCase(false, BookMediaType.Audiobook, false)] [TestCase(true, BookMediaType.Audiobook, false)]
        [TestCase(false, BookMediaType.Ebook, false)] [TestCase(true, BookMediaType.Ebook, false)]
        [TestCase(false, BookMediaType.Audiobook, true)] [TestCase(true, BookMediaType.Audiobook, true)]
        [TestCase(false, BookMediaType.Ebook, true)] [TestCase(true, BookMediaType.Ebook, true)]
        public void manual_grab_should_preserve_the_original_captured_group_without_adding_an_independent_capture(bool postgres, BookMediaType media, bool bulk)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var author = new Author { Id = 38, Name = "Test Writer" };
                var books = new[] { 501, 502, 503 }.Select(id => new Book
                { Id = id, AuthorId = author.Id, MediaType = media, HardcoverBookId = "hc:8200", Editions = new() }).ToList();
                var repository = new PendingReleaseRepository(database, Proxy<IEventAggregator>((method, args) => null));
                var original = Insert(repository, author, "original group", PendingReleaseReason.Delay, 501, 502);
                var independent = Insert(repository, author, "independent capture", PendingReleaseReason.Delay, 503);
                foreach (var row in new[] { original, independent })
                {
                    row.Release.Guid = "same-physical-release";
                    row.ParsedBookInfo.Quality = new QualityModel(media == BookMediaType.Audiobook ? Quality.M4B : Quality.EPUB);
                    row.AdditionalInfo.AlternativeCopies = true;
                    row.AdditionalInfo.TargetEditionIds = row.AdditionalInfo.BookIds.ToDictionary(id => id, id => id + 1000);
                    row.AdditionalInfo.TargetCustomFormatScores = row.AdditionalInfo.BookIds.ToDictionary(id => id, id => id * 100);
                    repository.Update(row);
                }

                var service = Service(database, author, books, new Observed());
                var queue = service.GetPendingQueue();
                var selected = queue.Where(row => row.PendingReleaseId == original.Id).OrderBy(row => row.Book.Id).ToList();
                var sent = new List<RemoteBook>();
                var controller = CreateGrabController(service, Proxy<IDownloadService>((method, args) =>
                { sent.Add((RemoteBook)args[0]); return Task.CompletedTask; }));
                if (bulk) controller.Grab(new QueueBulkResource { Ids = selected.Select(row => row.Id).ToList() }).GetAwaiter().GetResult();
                else controller.Grab(selected[0].Id).GetAwaiter().GetResult();

                Assert.Multiple(() =>
                {
                    Assert.That(selected.Select(row => row.RemoteBook.Books.Single().Id), Is.EqualTo(new[] { 501, 502 }));
                    Assert.That(selected.All(row => row.TargetBookIds.SequenceEqual(new[] { 501, 502 })), Is.True);
                    Assert.That(sent, Has.Count.EqualTo(1));
                    Assert.That(sent.Single().Books.Select(book => book.Id), Is.EqualTo(new[] { 501, 502 }));
                    Assert.That(sent.Single().TargetEditionIds, Is.EquivalentTo(new Dictionary<int, int> { [501] = 1501, [502] = 1502 }));
                    Assert.That(sent.Single().TargetCustomFormatScores, Is.EquivalentTo(new Dictionary<int, int> { [501] = 0, [502] = 0 }));
                    Assert.That(sent.Single().DownloadAllowed, Is.False);
                    Assert.That(sent.Single().Books.All(book => book.MediaType == media), Is.True);
                    Assert.That(repository.All().Select(row => row.Id), Is.EquivalentTo(new[] { original.Id, independent.Id }));
                    Assert.That(repository.Get(original.Id).AdditionalInfo.TargetCustomFormatScores.Values, Is.EquivalentTo(new[] { 50100, 50200 }));
                });
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void manual_bulk_grab_should_preserve_distinct_selected_releases_for_the_same_book(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var author = new Author { Id = 38, Name = "Test Writer" };
                var book = new Book { Id = 501, AuthorId = author.Id, MediaType = BookMediaType.Audiobook };
                var repository = new PendingReleaseRepository(database, Proxy<IEventAggregator>((method, args) => null));
                foreach (var guid in new[] { "first-physical-release", "second-physical-release" })
                {
                    var row = Insert(repository, author, guid, PendingReleaseReason.Delay, book.Id);
                    row.Release.Guid = guid; repository.Update(row);
                }
                var selected = repository.All().Select(row => new NzbDrone.Core.Queue.Queue
                {
                    Id = row.Id, PendingReleaseId = row.Id, Book = book, TargetBookIds = row.AdditionalInfo.BookIds,
                    RemoteBook = new RemoteBook { Author = author, Books = new() { book }, Release = row.Release, ParsedBookInfo = row.ParsedBookInfo }
                }).ToList();
                var pending = Proxy<IPendingReleaseService>((method, args) => method.Name switch
                {
                    nameof(IPendingReleaseService.GetPendingQueue) => selected,
                    nameof(IPendingReleaseService.FindPendingQueueItem) => selected.SingleOrDefault(row => row.Id == (int)args[0]),
                    _ => throw new NotImplementedException(method.Name)
                });
                var sent = new List<string>();
                var controller = CreateGrabController(pending, Proxy<IDownloadService>((method, args) =>
                { sent.Add(((RemoteBook)args[0]).Release.Guid); return Task.CompletedTask; }));
                controller.Grab(new QueueBulkResource { Ids = selected.Select(row => row.Id).ToList() }).GetAwaiter().GetResult();
                Assert.That(sent, Is.EquivalentTo(new[] { "first-physical-release", "second-physical-release" }));
                Assert.That(repository.All(), Has.Count.EqualTo(2));
            });
        }

        [TestCase(false, BookMediaType.Audiobook, false, false)] [TestCase(true, BookMediaType.Audiobook, false, false)]
        [TestCase(false, BookMediaType.Ebook, false, false)] [TestCase(true, BookMediaType.Ebook, false, false)]
        [TestCase(false, BookMediaType.Audiobook, true, false)] [TestCase(true, BookMediaType.Audiobook, true, false)]
        [TestCase(false, BookMediaType.Ebook, true, false)] [TestCase(true, BookMediaType.Ebook, true, false)]
        [TestCase(false, BookMediaType.Audiobook, false, true)] [TestCase(true, BookMediaType.Audiobook, false, true)]
        [TestCase(false, BookMediaType.Ebook, false, true)] [TestCase(true, BookMediaType.Ebook, false, true)]
        [TestCase(false, BookMediaType.Audiobook, true, true)] [TestCase(true, BookMediaType.Audiobook, true, true)]
        [TestCase(false, BookMediaType.Ebook, true, true)] [TestCase(true, BookMediaType.Ebook, true, true)]
        public void bulk_grab_should_coalesce_only_compatible_explicitly_selected_captures(bool postgres, BookMediaType media, bool differentGuid, bool selectBothOriginalViews)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var author = new Author { Id = 38, Name = "Test Writer" };
                var books = new[] { 501, 502, 503 }.Select(id => new Book
                { Id = id, AuthorId = author.Id, MediaType = media, HardcoverBookId = "hc:8200", Editions = new() }).ToList();
                var repository = new PendingReleaseRepository(database, Proxy<IEventAggregator>((method, args) => null));
                var original = Insert(repository, author, "original group", PendingReleaseReason.Delay, 501, 502);
                var independent = Insert(repository, author, "explicit capture", PendingReleaseReason.Delay, 503);
                foreach (var row in new[] { original, independent })
                {
                    row.Release.Guid = row.Id == independent.Id && differentGuid ? "different-physical-release" : "same-physical-release";
                    row.ParsedBookInfo.Quality = new QualityModel(media == BookMediaType.Audiobook ? Quality.M4B : Quality.EPUB);
                    row.AdditionalInfo.AlternativeCopies = true;
                    row.AdditionalInfo.TargetEditionIds = row.AdditionalInfo.BookIds.ToDictionary(id => id, id => id + 1000);
                    repository.Update(row);
                }

                var observed = new Observed();
                var service = Service(database, author, books, observed);
                var selected = service.GetPendingQueue().Where(row => row.Book.Id != 502 || selectBothOriginalViews).Select(row => row.Id).ToList();
                var sent = new List<RemoteBook>();
                var controller = CreateGrabController(service, Proxy<IDownloadService>((method, args) =>
                { sent.Add((RemoteBook)args[0]); return Task.CompletedTask; }));
                controller.Grab(new QueueBulkResource { Ids = selected }).GetAwaiter().GetResult();

                Assert.Multiple(() =>
                {
                    Assert.That(sent, Has.Count.EqualTo(differentGuid ? 2 : 1));
                    Assert.That(sent.SelectMany(remote => remote.Books).Select(book => book.Id), Is.EquivalentTo(new[] { 501, 502, 503 }));
                    Assert.That(sent.All(remote => remote.TargetEditionIds.Keys.ToHashSet().SetEquals(remote.Books.Select(book => book.Id))), Is.True);
                    Assert.That(sent.All(remote => remote.TargetCustomFormatScores.Keys.ToHashSet().SetEquals(remote.Books.Select(book => book.Id))), Is.True);
                    Assert.That(sent.All(remote => remote.DownloadAllowed == false && remote.Books.All(book => book.MediaType == media)), Is.True);
                    Assert.That(repository.Get(original.Id).AdditionalInfo.BookIds, Is.EqualTo(new[] { 501, 502 }));
                    Assert.That(repository.Get(independent.Id).AdditionalInfo.BookIds, Is.EqualTo(new[] { 503 }));
                    Assert.That(repository.All(), Has.Count.EqualTo(2));
                    Assert.That(observed.Reads, Is.EqualTo(2));
                    if (differentGuid) Assert.That(sent.Select(remote => remote.Release.Guid), Is.EquivalentTo(new[] { "same-physical-release", "different-physical-release" }));
                    else Assert.That(sent.Single().TargetEditionIds, Is.EquivalentTo(new Dictionary<int, int> { [501] = 1501, [502] = 1502, [503] = 1503 }));
                });
            });
        }
    }
}
