using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NLog;
using NUnit.Framework;
using Chaptarr.Core.Test.Common;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications.RssSync;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Aggregation;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Jobs;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Delay;
using NzbDrone.Core.Qualities;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class PendingReleaseMonitoringLifecycleFixture
    {
        private sealed class Observation { public int PendingReads; public int BookReads; public int Deletes; public int Events; public int AuthorReads; public int TitleBatches; }
        private static PendingReleaseService Service(IMainDatabase database, Observation seen, bool failBookRead = false, Author legacyAuthor = null)
        {
            var events = Proxy<IEventAggregator>((method, args) => { seen.Events++; return null; });
            var native = new PendingReleaseRepository(database, events);
            var repository = Proxy<IPendingReleaseRepository>((method, args) =>
            {
                if (method.Name == "All") seen.PendingReads++;
                if (method.Name is "Delete" or "DeleteMany") seen.Deletes++;
                return method.Invoke(native, args);
            });
            var books = new BookRepository(database, events);
            var authors = new AuthorRepository(database, events, LogManager.GetCurrentClassLogger());
            var bookService = Proxy<IBookService>((method, args) =>
            {
                Assert.That(method.Name, Is.EqualTo(nameof(IBookService.GetExistingBooks)));
                seen.BookReads++;
                if (failBookRead) throw new InvalidOperationException("Book lookup unavailable");
                return books.FindExisting((IEnumerable<int>)args[0]).ToList();
            });
            return new PendingReleaseService(Proxy<IIndexerStatusService>((method, args) => new List<IndexerStatus>()), repository,
                Proxy<IAuthorService>((method, args) =>
                {
                    Assert.That(method.Name, Is.EqualTo(nameof(IAuthorService.GetExistingAuthors)));
                    seen.AuthorReads++;
                    if (legacyAuthor == null) return authors.FindExisting((IEnumerable<int>)args[0]).ToList();
                    Assert.That((IEnumerable<int>)args[0], Is.EqualTo(new[] { legacyAuthor.Id }));
                    return new List<Author> { legacyAuthor };
                }),
                Proxy<IParsingService>((method, args) =>
                {
                    if (legacyAuthor == null) throw new InvalidOperationException("Unexpected title remap");
                    Assert.That(method.Name, Is.EqualTo(nameof(IParsingService.GetBooks)));
                    Assert.That(args[1], Is.SameAs(legacyAuthor));
                    seen.TitleBatches++;
                    return ((IReadOnlyList<ParsedBookInfo>)args[0]).Select(_ => new List<Book>()).ToList();
                }),
                Proxy<IDelayProfileService>((method, args) => new List<DelayProfile>()), null, null,
                Proxy<ICustomFormatCalculationService>((method, args) => new List<CustomFormat>()),
                Proxy<IRemoteBookAggregationService>((method, args) => null), null, null, events, LogManager.GetCurrentClassLogger(), bookService);
        }
        private static (Author Author, Book X, Book Y, Book Other, PendingReleaseRepository Pending, BookRepository Books) Seed(IMainDatabase database, BookMediaType media)
        {
            var events = Proxy<IEventAggregator>((method, args) => null);
            var author = new AuthorRepository(database, events, LogManager.GetCurrentClassLogger()).Insert(new Author
            {
                Name = "Test Writer", CleanName = "testwriter", SortName = "testwriter", TitleSlug = "test-writer", Added = DateTime.UtcNow,
                Path = "/test-writer", AudiobookMonitored = true, EbookMonitored = true
            });
            var books = new BookRepository(database, events);
            Book Insert(string slug, BookMediaType type)
            {
                var book = new Book { AuthorId = author.Id, Title = "Paper Lanterns", CleanTitle = "paperlanterns", TitleSlug = slug,
                    Added = DateTime.UtcNow, MediaType = type, HardcoverBookId = "hc:shared-work", BaseBookId = "hc:shared-work" };
                book.SetMonitored(true);
                return books.Insert(book);
            }
            return (author, Insert("copy-x", media), Insert("copy-y", media), Insert("other-side", media == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook), new PendingReleaseRepository(database, events), books);
        }
        private static PendingRelease Insert(PendingReleaseRepository repository, Author author, BookMediaType media, params int[] ids)
        {
            var quality = media == BookMediaType.Audiobook ? Quality.M4B : Quality.EPUB;
            return repository.Insert(new PendingRelease
            {
                AuthorId = author.Id, Title = "Test Writer - Paper Lanterns", Added = DateTime.UtcNow, Reason = PendingReleaseReason.Delay,
                Release = new ReleaseInfo { Title = "Test Writer - Paper Lanterns", IndexerId = 7, PublishDate = new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc), DownloadProtocol = DownloadProtocol.Usenet },
                ParsedBookInfo = new ParsedBookInfo { AuthorName = author.Name, BookTitle = "Paper Lanterns", Quality = new QualityModel(quality) },
                AdditionalInfo = new PendingReleaseAdditionalInfo { BookIds = ids.ToList(), ReleaseSource = ReleaseSourceType.InteractiveSearch }
            });
        }
        [TestCase(false, BookMediaType.Audiobook, 1)] [TestCase(true, BookMediaType.Audiobook, 1)]
        [TestCase(false, BookMediaType.Ebook, 1)] [TestCase(true, BookMediaType.Ebook, 1)]
        [TestCase(false, BookMediaType.Audiobook, 25)] [TestCase(true, BookMediaType.Audiobook, 25)]
        [TestCase(false, BookMediaType.Ebook, 25)] [TestCase(true, BookMediaType.Ebook, 25)]
        public void rss_pending_replay_should_cancel_only_unmonitored_saved_contexts(bool postgres, BookMediaType media, int count)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var data = Seed(database, media);
                var canceledBookIds = new List<int>();
                for (var index = 0; index < count; index++)
                {
                    var target = index == 0 ? data.X : data.Books.Insert(new Book
                    {
                        AuthorId = data.Author.Id, Title = "Paper Lanterns", CleanTitle = "paperlanterns", TitleSlug = "copy-x-" + index,
                        Added = DateTime.UtcNow, MediaType = media, HardcoverBookId = "hc:shared-work", BaseBookId = "hc:shared-work",
                        AudiobookMonitored = media == BookMediaType.Audiobook, EbookMonitored = media == BookMediaType.Ebook
                    });
                    canceledBookIds.Add(target.Id);
                    Insert(data.Pending, data.Author, media, target.Id);
                }
                var y = Insert(data.Pending, data.Author, media, data.Y.Id);
                var other = Insert(data.Pending, data.Author, data.Other.MediaType, data.Other.Id);
                data.Books.SetMonitored(canceledBookIds, false);
                var seen = new Observation(); var timer = Stopwatch.StartNew();
                var reports = Service(database, seen).GetPending();
                timer.Stop();
                Assert.Multiple(() =>
                {
                    Assert.That(data.Pending.All().Select(row => row.Id), Is.EquivalentTo(new[] { y.Id, other.Id }));
                    Assert.That(reports.Count, Is.EqualTo(2));
                    Assert.That(data.Pending.Get(y.Id).AdditionalInfo.BookIds, Is.EqualTo(new[] { data.Y.Id }));
                    Assert.That(data.Pending.Get(other.Id).AdditionalInfo.BookIds, Is.EqualTo(new[] { data.Other.Id }));
                    Assert.That(seen.PendingReads, Is.EqualTo(1)); Assert.That(seen.BookReads, Is.EqualTo(1));
                    Assert.That(seen.Deletes, Is.EqualTo(1)); Assert.That(seen.Events, Is.EqualTo(1));
                });
                TestContext.Progress.WriteLine($"C20_PENDING database={database.DatabaseType} media={media} canceled={count} pendingReads={seen.PendingReads} bookReads={seen.BookReads} deleteActions={seen.Deletes} events={seen.Events} elapsedMs={timer.Elapsed.TotalMilliseconds:F3}");
            });
        }
        [TestCase(false, BookMediaType.Audiobook)] [TestCase(true, BookMediaType.Audiobook)]
        [TestCase(false, BookMediaType.Ebook)] [TestCase(true, BookMediaType.Ebook)]
        public void a_rejection_for_x_should_preserve_y_and_other_media_with_the_same_report(bool postgres, BookMediaType media)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var data = Seed(database, media);
                var x = Insert(data.Pending, data.Author, media, data.X.Id);
                var y = Insert(data.Pending, data.Author, media, data.Y.Id);
                var other = Insert(data.Pending, data.Author, data.Other.MediaType, data.Other.Id);
                data.X.SetMonitored(false);
                var remote = new RemoteBook { Author = data.Author, Books = new List<Book> { data.X }, Release = x.Release, ParsedBookInfo = x.ParsedBookInfo };
                var spec = new MonitoredBookSpecification(LogManager.GetCurrentClassLogger());
                var rejected = spec.IsSatisfiedBy(remote, null); Assert.That(rejected.Accepted, Is.False);
                Service(database, new Observation()).Handle(new RssSyncCompleteEvent(new ProcessedDecisions(new(), new(), new() { new DownloadDecision(remote, new Rejection(rejected.Reason)) })));
                Assert.That(data.Pending.All().Select(row => row.Id), Is.EquivalentTo(new[] { y.Id, other.Id }));
            });
        }
        [TestCase(false)] [TestCase(true)]
        public void unproved_legacy_context_should_remain_durable_after_one_scoped_check(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var data = Seed(database, BookMediaType.Audiobook); data.Books.SetMonitored(new[] { data.X.Id }, false);
                var row = Insert(data.Pending, data.Author, BookMediaType.Audiobook, Array.Empty<int>());
                var seen = new Observation();
                var reports = Service(database, seen, legacyAuthor: data.Author).GetPending();
                Assert.That(reports.Count, Is.EqualTo(1));
                Assert.That(data.Pending.Get(row.Id).AdditionalInfo.BookIds, Is.EqualTo(Array.Empty<int>()));
                Assert.That(reports.Single().PendingContext.Id, Is.EqualTo(row.Id));
                Assert.That(reports.Single().PendingContext.AuthorId, Is.EqualTo(data.Author.Id));
                Assert.That(seen.PendingReads, Is.EqualTo(1));
                Assert.That(seen.AuthorReads, Is.EqualTo(1));
                Assert.That(seen.TitleBatches, Is.EqualTo(1));
                Assert.That(seen.BookReads + seen.Deletes + seen.Events, Is.Zero);
            });
        }

        [TestCase(false)] [TestCase(true)]
        public void ordinary_incomplete_captured_context_should_cancel_without_title_remap(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var data = Seed(database, BookMediaType.Audiobook); data.Books.SetMonitored(new[] { data.X.Id }, false);
                Insert(data.Pending, data.Author, BookMediaType.Audiobook, data.X.Id, 99999);
                var seen = new Observation();
                Assert.That(Service(database, seen).GetPending(), Is.Empty);
                Assert.That(data.Pending.All(), Is.Empty);
                Assert.That(seen.PendingReads, Is.EqualTo(1));
                Assert.That(seen.BookReads, Is.EqualTo(1));
                Assert.That(seen.Deletes, Is.EqualTo(1));
                Assert.That(seen.Events, Is.EqualTo(1));
                Assert.That(seen.AuthorReads + seen.TitleBatches, Is.Zero);
            });
        }
        [TestCase(false)] [TestCase(true)]
        public void a_failed_target_read_should_preserve_every_pending_row(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var data = Seed(database, BookMediaType.Audiobook); data.Books.SetMonitored(new[] { data.X.Id }, false);
                Insert(data.Pending, data.Author, BookMediaType.Audiobook, data.X.Id);
                var seen = new Observation();
                Assert.Throws<InvalidOperationException>(() => Service(database, seen, true).GetPending());
                Assert.That(data.Pending.All(), Has.Count.EqualTo(1)); Assert.That(seen.Deletes, Is.Zero); Assert.That(seen.Events, Is.Zero);
            });
        }
        [TestCase(false)] [TestCase(true)]
        public void an_unresolved_rejected_report_should_not_delete_proven_target_contexts(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var data = Seed(database, BookMediaType.Audiobook); var row = Insert(data.Pending, data.Author, BookMediaType.Audiobook, data.Y.Id);
                Service(database, new Observation()).Handle(new RssSyncCompleteEvent(new ProcessedDecisions(new(), new(), new() { new DownloadDecision(new RemoteBook { Release = row.Release }, new Rejection("Unable to identify a target")) })));
                Assert.That(data.Pending.All(), Has.Count.EqualTo(1));
            });
        }
        [TestCase(false, BookMediaType.Audiobook)] [TestCase(true, BookMediaType.Audiobook)]
        [TestCase(false, BookMediaType.Ebook)] [TestCase(true, BookMediaType.Ebook)]
        public void a_mixed_monitoring_collection_should_retain_existing_whole_context_rejection(bool postgres, BookMediaType media)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var data = Seed(database, media);
                var collection = Insert(data.Pending, data.Author, media, data.X.Id, data.Y.Id);
                var independentY = Insert(data.Pending, data.Author, media, data.Y.Id);
                data.X.SetMonitored(false); data.Books.SetMonitored(new[] { data.X.Id }, false);
                var remote = new RemoteBook { Author = data.Author, Books = new List<Book> { data.X, data.Y }, Release = collection.Release, ParsedBookInfo = collection.ParsedBookInfo };
                Assert.That(new MonitoredBookSpecification(LogManager.GetCurrentClassLogger()).IsSatisfiedBy(remote, null).Accepted, Is.False);
                Assert.That(Service(database, new Observation()).GetPending().Count, Is.EqualTo(1));
                Assert.That(data.Pending.All().Select(row => row.Id), Is.EqualTo(new[] { independentY.Id }));
                Assert.That(data.Pending.Get(independentY.Id).AdditionalInfo.BookIds, Is.EqualTo(new[] { data.Y.Id }));
            });
        }

    }
}
