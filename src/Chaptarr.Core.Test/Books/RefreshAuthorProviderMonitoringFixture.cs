using Chaptarr.Core.Test.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class RefreshAuthorProviderMonitoringFixture
    {
        public class Recorder : DispatchProxy
        {
            public Func<MethodInfo, object[], object> Handler { get; set; }

            protected override object Invoke(MethodInfo method, object[] args) => Handler(method, args);
        }

        private sealed class Refresh : RefreshAuthorService
        {
            private readonly List<Book> _local;
            private readonly bool _refreshBooks;
            public SortedChildren Result { get; private set; }

            public Refresh(List<Book> local, IEventAggregator events, IRefreshBookService refreshBooks = null)
                : base(MetadataHealthGateStub.Ready(), authorInfo: null, authorService: null, bookService: ReadService<IBookService>(local), editionService: ReadService<IEditionService>(local),
                    metadataProfileService: null, refreshBookService: refreshBooks, refreshSeriesService: null,
                    eventAggregator: events, commandQueueManager: null, mediaFileService: ReadService<IMediaFileService>(local),
                    historyRepository: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoHistoryRepositoryCalls(), rootFolderService: null, checkIfAuthorShouldBeRefreshed: null,
                    monitorNewBookService: new MonitorNewBookService(LogManager.GetCurrentClassLogger()),
                    configService: null, importListExclusionService: null, syncMetadataService: null,
                    syncQueueService: null, rootFolderSettingsResolver: null, logger: LogManager.GetCurrentClassLogger(), bookRepository: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoBookRepositoryCalls(), authorRepository: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoPendingAuthorSetup())
            {
                _local = local;
                _refreshBooks = refreshBooks != null;
            }

            public void Run(Author author, List<Book> remote) => SortChildren(author, remote, null, true, false, null);

            public void MatchWithoutSnapshot(List<Book> local, Book remote) => GetMatchingExistingChildren(local, remote, CreateContext());

            private static T ReadService<T>(List<Book> local)
                where T : class
            {
                var proxy = DispatchProxy.Create<T, Recorder>();
                ((Recorder)(object)proxy).Handler = (method, args) => method.Name switch
                {
                    nameof(IBookService.GetBooksForRefresh) => local.ToList(),
                    nameof(IBookService.GetBooksByAuthorId) => new List<Book>(), // Edition rehoming has separate fixtures.
                    nameof(IEditionService.GetEditionsByBook) => local.SelectMany(book => book.Editions).ToList(),
                    nameof(IMediaFileService.GetFilesByBooks) => local.SelectMany(book => book.BookFiles).ToList(),
                    _ => throw new NotImplementedException(method.Name)
                };
                return proxy;
            }

            protected override void AddChildren(List<Book> added)
            {
                var nextId = _local.Select(x => x.Id).DefaultIfEmpty().Max() + 1;
                foreach (var book in added)
                {
                    book.Id = nextId++;
                    book.BookFiles = new List<BookFile>();
                }
            }

            protected override bool RefreshChildren(SortedChildren children, List<Book> remote, Author data,
                bool force, bool tags, DateTime? last, RefreshContext context)
            {
                Result = children;
                return _refreshBooks && base.RefreshChildren(children, remote, data, force, tags, last, context);
            }
        }

        private static Book DeadOrAlive(Author author, int localId, BookMediaType mediaType, bool hardcover, bool monitored)
        {
            return new Book
            {
                Id = localId,
                Title = "Dead or Alive",
                TitleSlug = hardcover ? "dead-or-alive-hc" : "dead-or-alive-gr",
                Author = author,
                AuthorId = author.Id,
                MediaType = mediaType,
                BaseBookId = hardcover ? "hc:578022" : "gr:12502746",
                HardcoverBookId = hardcover ? "hc:578022" : null,
                RemoteProviderIds = hardcover
                    ? new HashSet<string> { "hc:578022", "gr:12502746" }
                    : new HashSet<string> { "gr:12502746" },
                AudiobookMonitored = mediaType == BookMediaType.Audiobook && monitored,
                EbookMonitored = mediaType == BookMediaType.Ebook && monitored,
                AnyEditionOk = true,
                ReleaseDate = new DateTime(2010, 1, 1),
                BookFiles = new List<BookFile>(),
                Editions = new List<Edition>
                {
                    new Edition { Id = 599 + localId, BookId = localId, Title = "Dead or Alive", ForeignEditionId = "gr:19053463" }
                }
            };
        }

        [Test]
        public void repeated_refresh_should_preserve_rows_and_settings_without_creating_replacements(
            [Values(BookMediaType.Ebook, BookMediaType.Audiobook)] BookMediaType mediaType,
            [Values] bool monitored, [Values] bool reverseOrder, [Values] bool samePocketKey, [Values] bool mixedSelections)
        {
            var author = new Author
            {
                Id = 100,
                Name = "Tom Clancy",
                AudiobookMonitored = true,
                EbookMonitored = true,
                AudiobookMonitorNewItems = monitored ? NewItemMonitorTypes.None : NewItemMonitorTypes.All,
                EbookMonitorNewItems = monitored ? NewItemMonitorTypes.None : NewItemMonitorTypes.All,
                AddOptions = null
            };
            var local = new List<Book>
            {
                DeadOrAlive(author, 1, mediaType, !samePocketKey, monitored),
                DeadOrAlive(author, 2, mediaType, false, mixedSelections ? !monitored : monitored)
            };
            local[0].BookFiles.Add(new BookFile { Id = 500, EditionId = 600 });
            local[0].LastInfoSync = DateTime.UtcNow;
            local[1].LastInfoSync = DateTime.UtcNow;
            local[1].Editions.Add(new Edition { Id = 700, BookId = 2, ForeignEditionId = "fixture-extra-edition" });
            var originalRows = local.ToList();
            var originalSelections = local.ToDictionary(book => book.Id, book => book.IsMonitored());
            var originalSyncTimes = local.Select(book => book.LastInfoSync).ToList();
            var remote = new List<Book>
            {
                DeadOrAlive(author, 0, mediaType, !samePocketKey, false),
                DeadOrAlive(author, 0, mediaType, false, false)
            };
            // The real server has distinct edition sets; this additional fixture edition keeps
            // the two pockets distinct without inventing another real provider identifier.
            remote[1].Editions.Add(new Edition { ForeignEditionId = "fixture-extra-edition" });
            if (reverseOrder)
            {
                remote.Reverse();
            }

            for (var pass = 0; pass < 2; pass++)
            {
                BookInfoRefreshedEvent published = null;
                var events = DispatchProxy.Create<IEventAggregator, Recorder>();
                ((Recorder)(object)events).Handler = (method, args) =>
                {
                    published = (BookInfoRefreshedEvent)args[0];
                    return null;
                };
                var refresh = new Refresh(local, events);
                refresh.Run(author, remote);
                local = refresh.Result.Future;

                var books = DispatchProxy.Create<IBookService, Recorder>();
                ((Recorder)(object)books).Handler = (method, args) => local;
                var queue = DispatchProxy.Create<IManageCommandQueue, Recorder>();
                var searches = new List<BookSearchCommand>();
                ((Recorder)(object)queue).Handler = (method, args) =>
                {
                    searches.Add((BookSearchCommand)args[0]);
                    return null;
                };
                var added = new BookAddedService(new CacheManager(), queue, books, LogManager.GetCurrentClassLogger());
                added.Handle(published);
                added.SearchForRecentlyAdded(author.Id);

                Assert.Multiple(() =>
                {
                    Assert.That(local, Has.Count.EqualTo(2));
                    Assert.That(local, Is.EquivalentTo(originalRows).Using(BookReferenceComparer.Instance));
                    Assert.That(local.Select(book => book.Id), Is.EquivalentTo(new[] { 1, 2 }));
                    Assert.That(local.OrderBy(book => book.Id).Select(book => book.LastInfoSync), Is.EqualTo(originalSyncTimes));
                    Assert.That(refresh.Result.Added, Is.Empty);
                    Assert.That(refresh.Result.Merged, Is.Empty);
                    Assert.That(refresh.Result.Deleted, Is.Empty);
                    Assert.That(local.All(x => x.IsMonitored() == originalSelections[x.Id]), Is.True);
                    Assert.That(local.Any(x => x.BookFiles.Any()), Is.True);
                    Assert.That(published.Added, Is.Empty);
                    Assert.That(searches, Is.Empty);
                });
            }
        }

        [Test]
        public void genuine_merge_should_not_rewrite_survivor_settings(
            [Values(BookMediaType.Ebook, BookMediaType.Audiobook)] BookMediaType mediaType,
            [Values] bool ownedHasPocketKey, [Values] bool monitored)
        {
            var author = new Author { Id = 100, Name = "Tom Clancy" };
            var owned = DeadOrAlive(author, 1, mediaType, ownedHasPocketKey, monitored);
            owned.LastInfoSync = DateTime.UtcNow;
            owned.BookFiles.Add(new BookFile { Id = 500, EditionId = 600 });
            var unmonitored = DeadOrAlive(author, 2, mediaType, !ownedHasPocketKey, !monitored);
            var events = DispatchProxy.Create<IEventAggregator, Recorder>();
            ((Recorder)(object)events).Handler = (method, args) => null;
            var refresh = new Refresh(new List<Book> { owned, unmonitored }, events);

            refresh.Run(author, new List<Book> { DeadOrAlive(author, 0, mediaType, true, false) });

            Assert.That(refresh.Result.Merged, Has.Count.EqualTo(1));
            Assert.That(refresh.Result.Merged[0].Item1, Is.SameAs(unmonitored));
            Assert.That(refresh.Result.Merged[0].Item2, Is.SameAs(owned));
            Assert.That(refresh.Result.Added, Is.Empty);
            Assert.That(refresh.Result.Deleted, Is.Empty);
            Assert.That(owned.IsMonitored(), Is.EqualTo(monitored));
            Assert.That(unmonitored.IsMonitored(), Is.EqualTo(!monitored));
            Assert.That(owned.LastInfoSync, Is.Not.Null);
        }

        private static Refresh CreateRefresh(List<Book> local, IRefreshBookService refreshBooks = null)
        {
            var events = DispatchProxy.Create<IEventAggregator, Recorder>();
            ((Recorder)(object)events).Handler = (method, args) => null;
            return new Refresh(local, events, refreshBooks);
        }

        [Test]
        public void disjoint_server_pockets_should_reuse_damaged_local_rows(
            [Values(BookMediaType.Ebook, BookMediaType.Audiobook)] BookMediaType mediaType,
            [Values] bool contestedHasFiles, [Values] bool reverseOrder, [Values] bool monitored)
        {
            var author = new Author { Id = 100, Name = "Tom Clancy", AddOptions = null };
            var single = DeadOrAlive(author, 1, mediaType, false, monitored);
            var contested = DeadOrAlive(author, 2, mediaType, true, !monitored);
            contested.BaseBookId = single.BaseBookId;
            var owned = contestedHasFiles ? contested : single;
            owned.BookFiles.Add(new BookFile { Id = 500, EditionId = 599 + owned.Id });
            var remote = new List<Book>
            {
                DeadOrAlive(author, 0, mediaType, false, false),
                DeadOrAlive(author, 0, mediaType, true, false)
            };
            remote[1].RemoteProviderIds.Remove("gr:12502746");
            remote[1].Editions[0].ForeignEditionId = "fixture-hc-edition";
            if (reverseOrder)
            {
                remote.Reverse();
            }

            var refreshedPockets = new Dictionary<int, Book>();
            var child = DispatchProxy.Create<IRefreshBookService, Recorder>();
            ((Recorder)(object)child).Handler = (method, args) =>
            {
                foreach (var book in (List<Book>)args[0])
                {
                    refreshedPockets.Add(book.Id, BookIdentity.FindMatchingRemotePocket((List<Book>)args[1], book));
                }

                return false;
            };
            var refresh = CreateRefresh(new List<Book> { single, contested }, child);
            refresh.Run(author, remote);

            Assert.Multiple(() =>
            {
                Assert.That(refresh.Result.Added, Is.Empty);
                Assert.That(refresh.Result.Merged, Is.Empty);
                Assert.That(refresh.Result.Deleted, Is.Empty);
                Assert.That(single.IsMonitored(), Is.EqualTo(monitored));
                Assert.That(contested.IsMonitored(), Is.EqualTo(!monitored));
                Assert.That(owned.BookFiles, Has.Count.EqualTo(1));
                Assert.That(refreshedPockets[single.Id].BaseBookId, Is.EqualTo("gr:12502746"));
                Assert.That(refreshedPockets[contested.Id].BaseBookId, Is.EqualTo("hc:578022"),
                    "Child refresh must use the same pocket as author reconciliation.");
            });
        }

        [Test]
        public void consolidation_should_preserve_owned_or_marked_copies(
            [Values(BookMediaType.Ebook, BookMediaType.Audiobook)] BookMediaType mediaType,
            [Values("files", "manual", "strict", "edition", "automatic-copy")] string protection)
        {
            var author = new Author { Id = 100, Name = "Tom Clancy" };
            var owned = DeadOrAlive(author, 1, mediaType, true, false);
            owned.BookFiles.Add(new BookFile { Id = 500, EditionId = 600, Path = "owned.m4b" });
            var copy = DeadOrAlive(author, 2, mediaType, false, true);
            switch (protection)
            {
                case "files": copy.BookFiles.Add(new BookFile { Id = 501, EditionId = 601, Path = "copy.mp3" }); break;
                case "manual": copy.AddOptions.AddType = BookAddType.Manual; break;
                case "strict": copy.AnyEditionOk = false; break;
                case "edition": copy.Editions[0].ManualAdd = true; break;
                case "automatic-copy": copy.UnitKeyHash = "fixture-copy"; break;
            }

            var refresh = CreateRefresh(new List<Book> { owned, copy });
            refresh.Run(author, new List<Book> { DeadOrAlive(author, 0, mediaType, true, false) });

            Assert.That(refresh.Result.Merged, Is.Empty);
            Assert.That(refresh.Result.Added, Is.Empty);
            Assert.That(owned.IsMonitored(), Is.False);
            Assert.That(copy.IsMonitored(), Is.True);
            Assert.That(owned.BookFiles.Single().EditionId, Is.EqualTo(600));
            if (protection == "files")
            {
                Assert.That(copy.BookFiles.Single().EditionId, Is.EqualTo(601));
            }
        }

        [Test]
        public void ambiguous_edition_match_should_remain_reserved_until_consumption_makes_it_unique(
            [Values(BookMediaType.Ebook, BookMediaType.Audiobook)] BookMediaType mediaType)
        {
            var author = new Author { Id = 100, Name = "Fixture author", AddOptions = null };
            var local = new List<Book> { DeadOrAlive(author, 1, mediaType, false, false), DeadOrAlive(author, 2, mediaType, false, true) };
            var remote = new List<Book> { DeadOrAlive(author, 0, mediaType, false, false), DeadOrAlive(author, 0, mediaType, false, false) };
            foreach (var book in local.Concat(remote))
            {
                book.RemoteProviderIds.Clear();
                book.BaseBookId = "az:B000000001";
                book.ASIN = "B000000001";
                book.Editions[0].ForeignEditionId = "az:B000000001";
            }

            remote[1].BaseBookId = "az:B000000002";
            Assert.That(BookIdentity.FindWorkFirstMatchResult(local, remote[1]).Disposition,
                Is.EqualTo(WorkFirstMatchDisposition.EditionAmbiguous));
            Assert.That(RefreshAuthorService.IsSameRemotePocketCopySet(local, remote[1]), Is.False);

            var refresh = CreateRefresh(local);
            refresh.Run(author, remote);

            Assert.That(refresh.Result.Added, Is.Empty);
            Assert.That(refresh.Result.Merged, Is.Empty);
            Assert.That(refresh.Result.Deleted, Is.Empty);
            Assert.That(local[0].IsMonitored(), Is.False);
            Assert.That(local[1].IsMonitored(), Is.True);
        }

        [Test]
        public void matching_should_not_rebuild_an_index_without_the_remote_snapshot()
        {
            var author = new Author { Id = 100, Name = "Tom Clancy" };
            var book = DeadOrAlive(author, 1, BookMediaType.Ebook, false, false);
            var local = new List<Book> { book };
            var refresh = CreateRefresh(local);
            Assert.Throws<InvalidOperationException>(() => refresh.MatchWithoutSnapshot(local, book));
            refresh.Run(author, new List<Book> { book });
            Assert.Throws<InvalidOperationException>(() => refresh.MatchWithoutSnapshot(local.ToList(), book));
        }

        [TestCase(NewItemMonitorTypes.All, true, false)]
        [TestCase(NewItemMonitorTypes.None, false, false)]
        [TestCase(NewItemMonitorTypes.All, true, true)]
        public void genuinely_new_work_or_media_side_should_still_follow_its_own_policy(
            NewItemMonitorTypes policy, bool expected, bool newMediaSide)
        {
            var author = new Author
            {
                Id = 100, Name = "Tom Clancy", EbookMonitored = true,
                EbookMonitorNewItems = policy, AddOptions = null
            };
            var local = DeadOrAlive(author, 1,
                newMediaSide ? BookMediaType.Audiobook : BookMediaType.Ebook, true, false);
            var remote = DeadOrAlive(author, 0, BookMediaType.Ebook, true, false);
            if (!newMediaSide)
            {
                // Same title and edition alias are not proof of the same work. This ID is fixture-only.
                remote.HardcoverBookId = "hc:999999999";
                remote.RemoteProviderIds = new HashSet<string> { remote.HardcoverBookId };
            }

            BookInfoRefreshedEvent published = null;
            var events = DispatchProxy.Create<IEventAggregator, Recorder>();
            ((Recorder)(object)events).Handler = (method, args) =>
            {
                published = (BookInfoRefreshedEvent)args[0];
                return null;
            };
            var refresh = new Refresh(new List<Book> { local }, events);
            refresh.Run(author, new List<Book> { remote });

            Assert.That(published.Added, Has.Count.EqualTo(1));
            Assert.That(published.Added[0].EbookMonitored, Is.EqualTo(expected));
            Assert.That(published.Added[0].AudiobookMonitored, Is.False);
            Assert.That(local.IsMonitored(), Is.False);
        }

        [Test]
        public void author_refresh_should_force_both_media_sides_with_their_assigned_snapshots(
            [Values(BookMediaType.Ebook, BookMediaType.Audiobook)] BookMediaType dueMediaType)
        {
            var author = new Author { Id = 100, Name = "Tom Clancy" };
            var local = new List<Book>
            {
                DeadOrAlive(author, 1, BookMediaType.Audiobook, true, true),
                DeadOrAlive(author, 2, BookMediaType.Ebook, true, false)
            };
            foreach (var book in local)
            {
                book.LastInfoSync = book.MediaType == dueMediaType ? null : DateTime.UtcNow;
            }

            var remote = local.Select(book => DeadOrAlive(author, 0, book.MediaType, true, false)).ToList();
            var refreshed = new List<BookMediaType>();
            var child = DispatchProxy.Create<IRefreshBookService, Recorder>();
            ((Recorder)(object)child).Handler = (method, args) =>
            {
                var books = (List<Book>)args[0];
                var snapshot = (List<Book>)args[1];
                Assert.That(args[3], Is.True, "Author refresh forces child refresh even when a media side was recently synced.");
                Assert.That(snapshot, Has.Count.EqualTo(1));
                foreach (var book in books)
                {
                    Assert.That(snapshot.Single().MediaType, Is.EqualTo(book.MediaType));
                    refreshed.Add(book.MediaType);
                }

                return false;
            };

            CreateRefresh(local, child).Run(author, remote);

            Assert.That(refreshed, Is.EquivalentTo(new[] { BookMediaType.Audiobook, BookMediaType.Ebook }));
        }

        [Test]
        public void contested_rows_should_keep_their_pocket_regardless_of_server_order(
            [Values(BookMediaType.Ebook, BookMediaType.Audiobook)] BookMediaType mediaType,
            [Values] bool reverseOrder,
            [Values] bool equalAffinity,
            [Values] bool reverseSecondPass)
        {
            // Two local copies of the hc pocket (one filed, one a fileless legacy duplicate) and a second
            // server pocket that overlaps both by gr id but has no key or edition affinity to either row.
            var author = new Author { Id = 100, Name = "Tom Clancy", AddOptions = null };
            var filed = DeadOrAlive(author, 1, mediaType, true, true);
            var duplicate = DeadOrAlive(author, 2, mediaType, true, true);
            filed.BookFiles.Add(new BookFile { Id = 500, EditionId = 600 });
            if (!equalAffinity)
            {
                duplicate.Editions[0].ForeignEditionId = "fixture-duplicate-edition";
            }
            var remote = new List<Book>
            {
                DeadOrAlive(author, 0, mediaType, true, false),
                DeadOrAlive(author, 0, mediaType, false, false)
            };
            remote[1].Editions[0].ForeignEditionId = "fixture-other-pocket-edition";
            if (reverseOrder)
            {
                remote.Reverse();
            }

            var refreshedPockets = new Dictionary<int, Book>();
            var child = DispatchProxy.Create<IRefreshBookService, Recorder>();
            ((Recorder)(object)child).Handler = (method, args) =>
            {
                foreach (var book in (List<Book>)args[0])
                {
                    var pocket = BookIdentity.FindMatchingRemotePocket((List<Book>)args[1], book);
                    refreshedPockets.Add(book.Id, pocket);
                    book.UseMetadataFrom(pocket);
                    book.Editions = RefreshEntityCopy.CloneBook(pocket, includeEditions: true).Editions;
                    foreach (var edition in book.Editions)
                    {
                        edition.BookId = book.Id;
                    }
                }

                return false;
            };
            var refresh = CreateRefresh(new List<Book> { filed, duplicate }, child);
            for (var pass = 0; pass < 2; pass++)
            {
                refreshedPockets.Clear();
                refresh.Run(author, remote);

                Assert.Multiple(() =>
                {
                    Assert.That(refresh.Result.Added, Is.Empty);
                    Assert.That(refresh.Result.Merged, Is.Empty);
                    Assert.That(refresh.Result.Deleted, Is.Empty);
                    Assert.That(refreshedPockets[filed.Id].BaseBookId, Is.EqualTo("hc:578022"),
                        "The filed row must stay with its pocket in either server order and on repeated refresh.");
                    Assert.That(refreshedPockets[duplicate.Id].BaseBookId, Is.EqualTo("gr:12502746"));
                    Assert.That(filed.BookFiles.Single().Id, Is.EqualTo(500));
                    Assert.That(filed.IsMonitored(), Is.True);
                    Assert.That(duplicate.IsMonitored(), Is.True);
                });

                if (reverseSecondPass)
                {
                    remote.Reverse();
                }
            }
        }
    }
}
