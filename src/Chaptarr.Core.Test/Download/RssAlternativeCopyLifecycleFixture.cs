using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Chaptarr.Core.Test.Common;
using Chaptarr.Api.V1.Queue;
using NzbDrone.Core.Queue;
using Chaptarr.Api.V1.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.IndexerSearch.Definitions;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.DecisionEngine.Specifications.RssSync;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Aggregation;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Jobs;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Delay;
using NzbDrone.Core.Profiles;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    [NonParallelizable]
    public partial class RssAlternativeCopyLifecycleFixture
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private sealed class Scene
        {
            public IMainDatabase Database { get; }
            public Author Author { get; }
            public List<Book> Books { get; }
            public HistoryService History { get; }
            public IBookService BookService { get; }
            public IAuthorService AuthorService { get; }
            public ParsingService Parser { get; }
            public IConfigService Config { get; } = InterfaceProxy.Proxy<IConfigService>((method, args) => method.Name switch
            {
                "get_BookMatchingStrictness" => BookMatchingStrictness.Balanced,
                "get_DownloadPropersAndRepacks" => ProperDownloadTypes.PreferAndUpgrade,
                "get_EnableCompletedDownloadHandling" => true,
                "get_MinimumAge" => 0,
                "get_RssSyncInterval" => 15,
                _ => throw new InvalidOperationException("Unexpected configuration call: " + method.Name)
            });
            public ICustomFormatCalculationService Formats { get; set; } = InterfaceProxy.Proxy<ICustomFormatCalculationService>((method, args) => new List<CustomFormat>());
            public IRemoteBookAggregationService Aggregation { get; set; } = new RemoteBookAggregationService(Array.Empty<NzbDrone.Core.Download.Aggregation.Aggregators.IAggregateRemoteBook>(), Logger);
            public IEventAggregator Events { get; } = InterfaceProxy.Proxy<IEventAggregator>((method, args) => null);
            public int CatalogReads { get; private set; }
            public int HistoryReads { get; private set; }
            public int ExistingBookReads { get; private set; }
            public bool FailHistoryRead { get; set; }
            public BookRepository BookWriter { get; }

            public Scene(IMainDatabase database, BookMediaType media, int copies = 2)
            {
                Database = database;
                var profile = new QualityProfile
                {
                    Id = 1, Name = "Fixture profile", ProfileType = media == BookMediaType.Audiobook ? ProfileType.Audiobook : ProfileType.Ebook,
                    UpgradeAllowed = true, Cutoff = media == BookMediaType.Audiobook ? Quality.MP3.Id : Quality.EPUB.Id,
                    Items = (media == BookMediaType.Audiobook ? new[] { Quality.MP3, Quality.M4B } : new[] { Quality.EPUB })
                        .Select(quality => new QualityProfileQualityItem { Quality = quality, Allowed = true }).ToList()
                };
                Author = new Author
                {
                    Name = "Test Writer", CleanName = "testwriter", TitleSlug = "test-writer", Path = "/fixture", Added = DateTime.UtcNow,
                    Monitored = true, AudiobookMonitored = true, EbookMonitored = true,
                    AudiobookQualityProfileId = 1, EbookQualityProfileId = 1,
                    AudiobookQualityProfile = profile, EbookQualityProfile = profile
                };
                new BasicRepository<Author>(database, Events).Insert(Author);
                Books = new List<Book>();
                var bookWriter = new BookRepository(database, Events);
                BookWriter = bookWriter;
                var editionWriter = new BasicRepository<Edition>(database, Events);
                foreach (var suffix in Enumerable.Range(0, copies).Select(index => index == 0 ? "first" : index == 1 ? "second" : "copy-" + index))
                {
                    var book = new Book
                    {
                        AuthorId = Author.Id, Author = Author, Title = "Paper Lanterns", CleanTitle = "paperlanterns", TitleSlug = "paper-lanterns-" + suffix,
                        HardcoverBookId = "hc:8200", MediaType = media, Monitored = true, AnyEditionOk = false, BookFiles = new List<BookFile>(),
                        AudiobookMonitored = media == BookMediaType.Audiobook, EbookMonitored = media == BookMediaType.Ebook,
                        Added = DateTime.UtcNow
                    };
                    bookWriter.Insert(book);
                    var edition = new Edition
                    {
                        BookId = book.Id, Book = book, Title = book.Title, TitleSlug = book.TitleSlug,
                        Monitored = true, ManualAdd = true, ReadingFormatId = media == BookMediaType.Audiobook ? 2 : 4,
                        NarratorNames = new List<string> { suffix == "first" ? "Reader One" : "Reader Two" }
                    };
                    editionWriter.Insert(edition);
                    book.Editions = new List<Edition> { edition };
                    Books.Add(book);
                }

                Author.Books = Books;
                BookService = InterfaceProxy.Proxy<IBookService>((method, args) =>
                {
                    if (method.Name == nameof(IBookService.GetBooksByAuthor))
                    {
                        CatalogReads++;
                        return Books.Where(book => book.AuthorId == (int)args[0]).ToList();
                    }

                    if (method.Name == nameof(IBookService.GetExistingBooks))
                    {
                        ExistingBookReads++;
                        var ids = ((IEnumerable<int>)args[0]).ToHashSet();
                        return bookWriter.FindExisting(ids).Select(book =>
                        {
                            var loaded = Books.Single(original => original.Id == book.Id);
                            book.Author = Author; book.Editions = loaded.Editions; book.BookFiles = loaded.BookFiles;
                            return book;
                        }).ToList();
                    }

                    if (method.Name == nameof(IBookService.GetBook)) return Books.Single(book => book.Id == (int)args[0]);
                    throw new NotImplementedException(method.Name);
                });
                AuthorService = InterfaceProxy.Proxy<IAuthorService>((method, args) => method.Name switch
                {
                    nameof(IAuthorService.GetAuthor) => Author,
                    nameof(IAuthorService.GetExistingAuthors) => new List<Author> { Author },
                    _ => throw new NotImplementedException(method.Name)
                });
                Parser = new ParsingService(AuthorService, BookService, null, null, Logger);
                History = new HistoryService(new HistoryRepository(database, Events), Logger);
            }

            public ReleaseInfo Release(BookMediaType media, string guid = "release-one", bool torrent = false)
            {
                ReleaseInfo report = torrent ? new TorrentInfo() : new ReleaseInfo();
                report.Title = "Test Writer - Paper Lanterns [" + (media == BookMediaType.Audiobook ? "M4B" : "EPUB") + "]";
                report.Author = Author.Name; report.Book = "Paper Lanterns";
                report.Indexer = "Fixture Indexer"; report.IndexerId = 7; report.Guid = guid;
                report.DownloadProtocol = DownloadProtocol.Torrent;
                report.PublishDate = new DateTime(DateTime.UtcNow.AddHours(-1).Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
                report.Size = 600;
                return report;
            }

            public DownloadDecisionMaker Maker(params IDecisionEngineSpecification[] extra)
            {
                var upgradable = new UpgradableSpecification(Config, Logger);
                var specifications = new List<IDecisionEngineSpecification>
                {
                    new MonitoredBookSpecification(Logger), new ReleaseTitleMatchSpecification(Logger, Config),
                    new HistorySpecification(InterfaceProxy.Proxy<IHistoryService>((method, args) =>
                    {
                        if (method.Name == nameof(IHistoryService.GetAcquisitionHistory)) { HistoryReads++; if (FailHistoryRead) throw new InvalidOperationException("History unavailable"); return History.GetAcquisitionHistory((IEnumerable<int>)args[0]); }
                        throw new NotImplementedException(method.Name);
                    }), new DownloadHistoryService(new DownloadHistoryRepository(Database, Events), History), upgradable, Formats, Config, Logger)
                };
                specifications.AddRange(extra);
                return new DownloadDecisionMaker(specifications, Parser, BookService, new EditionFtsRepository(Database, Logger),
                    Formats, Aggregation,
                    null, Config, Logger);
            }

            public IPrioritizeDownloadDecision Prioritizer() => new DownloadDecisionPriorizationService(Config,
                InterfaceProxy.Proxy<IDelayProfileService>((method, args) => new DelayProfile { PreferredProtocol = DownloadProtocol.Torrent }), null, Logger);

            public PendingReleaseService Pending(bool blockedIndexer = false)
            {
                return new PendingReleaseService(
                    indexerStatusService: InterfaceProxy.Proxy<IIndexerStatusService>((method, args) => blockedIndexer
                        ? new List<IndexerStatus> { new() { ProviderId = 8, DisabledTill = DateTime.UtcNow.AddHours(1) } } : new List<IndexerStatus>()),
                    repository: new PendingReleaseRepository(Database, Events), authorService: AuthorService, parsingService: Parser,
                    delayProfileService: InterfaceProxy.Proxy<IDelayProfileService>((method, args) => method.Name == nameof(IDelayProfileService.AllForTags) ? new List<DelayProfile> { new() { PreferredProtocol = DownloadProtocol.Torrent } } : new DelayProfile { PreferredProtocol = DownloadProtocol.Torrent }),
                    taskManager: InterfaceProxy.Proxy<ITaskManager>((method, args) => DateTime.UtcNow.AddMinutes(15)),
                    configService: Config, formatCalculator: Formats,
                    aggregationService: Aggregation,
                    downloadClientFactory: InterfaceProxy.Proxy<IDownloadClientFactory>((method, args) => new List<DownloadClientDefinition>()),
                    indexerFactory: InterfaceProxy.Proxy<IIndexerFactory>((method, args) => null),
                    eventAggregator: Events, logger: Logger, bookService: BookService);
            }

            public TrackedDownload Track(string downloadId)
            {
                var service = new TrackedDownloadService(Parser, new CacheManager(), History, Events,
                    new DownloadHistoryService(new DownloadHistoryRepository(Database, Events), History), Formats, Logger,
                    InterfaceProxy.Proxy<IDownloadClientFileSnapshotService>((method, args) => null));
                return service.TrackDownload(new DownloadClientDefinition { Id = 1, Protocol = DownloadProtocol.Torrent }, new DownloadClientItem
                {
                    DownloadId = downloadId, Title = "opaque client title", Status = DownloadItemStatus.Downloading, TotalSize = 600,
                    DownloadClientInfo = new DownloadClientItemClientInfo { Name = "Offline client" }
                });
            }
        }

        [TestCase(false)] [TestCase(true)]
        public void rss_category_evidence_scopes_dual_media_and_survives_pending_replay(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var scene = new Scene(database, BookMediaType.Audiobook);
                var ebook = scene.Books[1];
                ebook.MediaType = BookMediaType.Ebook;
                ebook.EbookMonitored = true;
                ebook.AudiobookMonitored = false;
                scene.BookWriter.Update(ebook);
                var profile = new QualityProfile
                {
                    Id = 2, Name = "Category fixture", ProfileType = ProfileType.Ebook, Cutoff = Quality.EPUB.Id,
                    Items = new[] { Quality.Unknown, Quality.EPUB }
                        .Select(quality => new QualityProfileQualityItem { Quality = quality, Allowed = true }).ToList()
                };
                scene.Author.AudiobookQualityProfile.Value.Items.Insert(0,
                    new QualityProfileQualityItem { Quality = Quality.UnknownAudio, Allowed = true });
                scene.Author.EbookQualityProfileId = profile.Id;
                scene.Author.EbookQualityProfile = profile;
                var maker = scene.Maker(new QualityAllowedByProfileSpecification(Logger), new MonitoredMediaTypeSpecification(Logger));
                var cases = new (int[] Categories, string Format, BookMediaType? Expected)[]
                {
                    (new[] { 7000, 7020, 107020 }, "", BookMediaType.Ebook),
                    (new[] { 3000, 3030 }, "", BookMediaType.Audiobook),
                    (new[] { 7000 }, "", null),
                    (Array.Empty<int>(), "", null),
                    (new[] { 107020 }, "", null),
                    (new[] { 3030, 7020 }, "", null),
                    (new[] { 7000 }, " [EPUB]", BookMediaType.Ebook),
                    (new[] { 7000 }, " [M4B]", BookMediaType.Audiobook)
                };
                foreach (var item in cases)
                {
                    var report = scene.Release(BookMediaType.Ebook);
                    report.Title = "Test Writer - Paper Lanterns" + item.Format;
                    report.Categories = item.Categories.ToList();
                    var decisions = maker.GetRssDecision(new List<ReleaseInfo> { report });
                    Assert.That(decisions.Count, Is.EqualTo(item.Expected.HasValue ? 1 : 0),
                        $"{database.DatabaseType}: {report.Title}, categories={string.Join(',', item.Categories)}");
                    if (!item.Expected.HasValue)
                    {
                        continue;
                    }

                    var decision = decisions.Single();
                    Assert.That(decision.Approved, Is.True);
                    Assert.That(decision.RemoteBook.Books.Single().MediaType, Is.EqualTo(item.Expected));
                    var pending = scene.Pending();
                    pending.AddMany(new List<Tuple<DownloadDecision, PendingReleaseReason>>
                    {
                        Tuple.Create(decision, PendingReleaseReason.Delay)
                    });
                    var replay = scene.Pending().GetPending(out var dates);
                    var replayed = maker.GetRssDecision(replay, false, dates).Single();
                    Assert.That(replayed.Approved, Is.True);
                    Assert.That(replayed.RemoteBook.Books.Single().Id, Is.EqualTo(decision.RemoteBook.Books.Single().Id));
                    new PendingReleaseRepository(database, scene.Events).Purge();
                }

                var unknownEbook = scene.Release(BookMediaType.Ebook);
                unknownEbook.Title = "Test Writer - Paper Lanterns";
                unknownEbook.Categories = new List<int> { 7020 };
                profile.Items.Single(item => item.Quality == Quality.Unknown).Allowed = false;
                var rejected = maker.GetRssDecision(new List<ReleaseInfo> { unknownEbook }).Single();
                Assert.That(rejected.RemoteBook.Books.Single().Id, Is.EqualTo(ebook.Id));
                Assert.That(rejected.Rejections.Select(reason => reason.Reason), Has.Some.Contains("not wanted in profile"));

                profile.Items.Single(item => item.Quality == Quality.Unknown).Allowed = true;
                scene.Books[0].AudiobookMonitored = false;
                scene.BookWriter.Update(scene.Books[0]);
                unknownEbook.Categories = new List<int> { 7000 };
                var soleWantedSide = maker.GetRssDecision(new List<ReleaseInfo> { unknownEbook }).Single();
                Assert.That(soleWantedSide.Approved, Is.True);
                Assert.That(soleWantedSide.RemoteBook.Books.Single().Id, Is.EqualTo(ebook.Id));
            });
        }

        [TestCase(false)] [TestCase(true)]
        public void unresolved_same_title_legacy_rows_should_keep_their_own_author_context_after_one_batch(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var events = InterfaceProxy.Proxy<IEventAggregator>((method, args) => null);
                var authors = new AuthorRepository(database, events, Logger);
                var captured = new[] { "Writer One", "Writer Two" }.Select((name, index) => authors.Insert(new Author
                { Name = name, CleanName = "writer" + index, SortName = name, TitleSlug = "writer-" + index, Added = DateTime.UtcNow, Path = "/fixture-" + index })).ToList();
                var repository = new PendingReleaseRepository(database, events);
                foreach (var author in captured)
                {
                    repository.Insert(new PendingRelease
                    {
                        AuthorId = author.Id, Title = "opaque title", Reason = PendingReleaseReason.Fallback, Added = DateTime.UtcNow,
                        Release = new ReleaseInfo { Title = "opaque title", Indexer = "Fixture Indexer", IndexerId = 7, Guid = "legacy-" + author.Id, PublishDate = DateTime.UtcNow.AddHours(-1), DownloadProtocol = DownloadProtocol.Torrent },
                        ParsedBookInfo = new ParsedBookInfo { ReleaseTitle = "opaque title", AuthorName = author.Name, BookTitle = "opaque title", Quality = new QualityModel(Quality.EPUB) },
                        AdditionalInfo = new PendingReleaseAdditionalInfo()
                    });
                }

                var authorBatches = 0;
                var parserBatches = 0;
                var seenAuthors = new List<int>();
                var service = new PendingReleaseService(
                    InterfaceProxy.Proxy<IIndexerStatusService>((method, args) => new List<IndexerStatus>()), repository,
                    InterfaceProxy.Proxy<IAuthorService>((method, args) =>
                    { authorBatches++; return authors.FindExisting((IEnumerable<int>)args[0]).ToList(); }),
                    InterfaceProxy.Proxy<IParsingService>((method, args) =>
                    {
                        Assert.That(method.Name, Is.EqualTo(nameof(IParsingService.GetBooks)));
                        parserBatches++;
                        seenAuthors.Add(((Author)args[1]).Id);
                        return ((IReadOnlyList<ParsedBookInfo>)args[0]).Select(_ => new List<Book>()).ToList();
                    }),
                    null, null, null,
                    InterfaceProxy.Proxy<ICustomFormatCalculationService>((method, args) => throw new InvalidOperationException("Unexpected snapshot format preparation")),
                    InterfaceProxy.Proxy<IRemoteBookAggregationService>((method, args) => throw new InvalidOperationException("Unexpected snapshot aggregation")),
                    null, null, events, Logger,
                    InterfaceProxy.Proxy<IBookService>((method, args) => throw new InvalidOperationException("Unexpected saved-target read")));
                var reports = service.GetPending(out var dates);
                TestContext.Out.WriteLine($"PENDING_LEGACY_BATCH provider={database.DatabaseType}, rows={reports.Count}, authors={captured.Count}, authorBatches={authorBatches}, parserBatches={parserBatches}");
                Assert.Multiple(() =>
                {
                    Assert.That(authorBatches, Is.EqualTo(1));
                    Assert.That(parserBatches, Is.EqualTo(2));
                    Assert.That(seenAuthors, Is.EquivalentTo(captured.Select(author => author.Id)));
                    Assert.That(reports, Has.Count.EqualTo(2));
                    Assert.That(reports.All(report => report.PendingContext.AuthorId == report.PendingContext.RemoteBook.Author.Id), Is.True);
                    Assert.That(reports.All(report => report.PendingContext.RemoteBook.Books.Count == 0), Is.True);
                    Assert.That(repository.All(), Has.Count.EqualTo(2));
                    Assert.That(repository.All().All(row => row.AdditionalInfo.BookIds.Count == 0), Is.True);
                    Assert.That(dates, Is.Empty);
                });
            });
        }

        [TestCase(false, 1)] [TestCase(true, 1)]
        [TestCase(false, 25)] [TestCase(true, 25)]
        public void pending_rss_should_reuse_one_saved_target_batch_without_catalog_remapping(bool postgres, int count)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var scene = new Scene(database, BookMediaType.Ebook);
                var formats = 0;
                var augmentations = 0;
                var calculator = scene.Formats;
                scene.Formats = InterfaceProxy.Proxy<ICustomFormatCalculationService>((method, args) =>
                { formats++; return method.Invoke(calculator, args); });
                scene.Aggregation = InterfaceProxy.Proxy<IRemoteBookAggregationService>((method, args) =>
                { augmentations++; return args[0]; });
                var pending = scene.Pending();
                pending.AddMany(Enumerable.Range(0, count).Select(index =>
                {
                    var report = scene.Release(BookMediaType.Ebook, "pending-" + index);
                    report.PublishDate = report.PublishDate.AddMinutes(-index);
                    return Tuple.Create(new DownloadDecision(Context(scene, report)), PendingReleaseReason.Delay);
                }).ToList());
                var before = scene.ExistingBookReads;
                var reports = pending.GetPending(out var dates);
                var afterSnapshot = scene.ExistingBookReads;
                var snapshotFormats = formats;
                var snapshotAugmentations = augmentations;
                var decisions = scene.Maker().GetRssDecision(reports, false, dates);
                TestContext.Out.WriteLine($"PENDING_RSS_BATCH provider={database.DatabaseType}, reports={count}, savedTargetReads={afterSnapshot - before}, rssTargetReads={scene.ExistingBookReads - afterSnapshot}, catalogReads={scene.CatalogReads}, historyReads={scene.HistoryReads}, decisions={decisions.Count}, snapshotFormats={snapshotFormats}, snapshotAugmentations={snapshotAugmentations}, totalFormats={formats}, totalAugmentations={augmentations}");
                Assert.Multiple(() =>
                {
                    Assert.That(reports, Has.Count.EqualTo(count));
                    Assert.That(afterSnapshot - before, Is.EqualTo(1));
                    Assert.That(scene.ExistingBookReads, Is.EqualTo(afterSnapshot));
                    Assert.That(scene.CatalogReads, Is.Zero);
                    Assert.That(scene.HistoryReads, Is.EqualTo(1));
                    Assert.That(snapshotFormats + snapshotAugmentations, Is.Zero);
                    Assert.That(formats, Is.EqualTo(count * 2));
                    Assert.That(augmentations, Is.EqualTo(count * 2));
                    Assert.That(decisions, Has.Count.EqualTo(count * 2));
                    Assert.That(decisions.SelectMany(decision => decision.RemoteBook.Books).Select(book => book.Id),
                        Is.EquivalentTo(Enumerable.Range(0, count).SelectMany(_ => scene.Books.Select(book => book.Id))));
                    Assert.That(reports.All(report => report.PendingContext.RemoteBook.TargetEditionIds.Keys.ToHashSet().SetEquals(scene.Books.Select(book => book.Id))), Is.True);
                    Assert.That(dates.Keys.Select(key => key.BookId), Is.EquivalentTo(scene.Books.Select(book => book.Id)));
                });
            });
        }

        [TestCase(false, BookMediaType.Audiobook)]
        [TestCase(true, BookMediaType.Audiobook)]
        [TestCase(false, BookMediaType.Ebook)]
        [TestCase(true, BookMediaType.Ebook)]
        public void rss_should_load_each_author_catalog_once_per_batch_and_refresh_it_next_batch(bool postgres, BookMediaType media)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var scene = new Scene(database, media);
                var maker = scene.Maker();
                var reports = Enumerable.Range(0, 25).Select(index => scene.Release(media, "batch-" + index)).ToList();
                for (var batch = 1; batch <= 2; batch++)
                {
                    var decisions = maker.GetRssDecision(reports);
                    Assert.Multiple(() =>
                    {
                        Assert.That(scene.CatalogReads, Is.EqualTo(batch));
                        Assert.That(decisions, Has.Count.EqualTo(reports.Count * scene.Books.Count));
                        Assert.That(decisions.All(decision => decision.Approved), Is.True);
                        Assert.That(decisions.SelectMany(decision => decision.RemoteBook.Books).Select(book => book.Id).Distinct(),
                            Is.EquivalentTo(scene.Books.Select(book => book.Id)));
                    });
                }
            });
        }

        [TestCase(false, BookMediaType.Audiobook)]
        [TestCase(true, BookMediaType.Audiobook)]
        [TestCase(false, BookMediaType.Ebook)]
        [TestCase(true, BookMediaType.Ebook)]
        public async Task rss_should_score_each_copy_then_send_one_grab_and_restore_both_targets(bool postgres, BookMediaType media)
        {
            await Run(postgres, async scene =>
            {
                var report = scene.Release(media);
                var decisions = scene.Maker().GetRssDecision(new List<ReleaseInfo> { report });
                Assert.That(decisions, Has.Count.EqualTo(2));
                Assert.That(decisions.All(decision => decision.Approved && decision.RemoteBook.Books.Count == 1), Is.True);
                Assert.That(scene.CatalogReads, Is.EqualTo(1));
                Assert.That(scene.HistoryReads, Is.EqualTo(1));
                var sent = new List<RemoteBook>();
                var downloader = InterfaceProxy.Proxy<IDownloadService>((method, args) =>
                {
                    var remote = (RemoteBook)args[0]; sent.Add(remote);
                    var message = new BookGrabbedEvent(remote) { DownloadId = "one-download", DownloadClient = "Offline client" };
                    scene.History.Handle(message);
                    new DownloadHistoryService(new DownloadHistoryRepository(scene.Database, scene.Events), scene.History).Handle(message);
                    return Task.CompletedTask;
                });
                var process = new ProcessDownloadDecisions(downloader, scene.Prioritizer(), scene.Pending(), new Chaptarr.Core.Test.Download.EmptyQueueSpecification(), Logger);
                var result = await process.ProcessDecisions(decisions);
                Assert.That(sent, Has.Count.EqualTo(1));
                Assert.That(result.Grabbed, Has.Count.EqualTo(1));
                var history = scene.History.FindByDownloadId("one-download");
                Assert.That(history.Select(row => row.BookId), Is.EquivalentTo(scene.Books.Select(book => book.Id)));
                Assert.That(history.Select(row => row.EditionId), Is.EquivalentTo(scene.Books.Select(book => book.Editions[0].Id)));
                Assert.That(history.Select(row => row.Date).Distinct().Count(), Is.EqualTo(1));
                Assert.That(history.All(row => row.GetDataValue(EntityHistory.ALTERNATIVE_COPIES) == bool.TrueString), Is.True);
                var tracked = scene.Track("one-download");
                Assert.That(tracked.RemoteBook.Books.Select(book => book.Id), Is.EquivalentTo(scene.Books.Select(book => book.Id)));
                Assert.That(tracked.RemoteBook.AlternativeCopies, Is.True);
                Assert.That(tracked.RemoteBook.AlternativeCopyGrabDate, Is.EqualTo(history[0].Date));
                Assert.That(tracked.RemoteBook.Release.IndexerId, Is.EqualTo(7));
                Assert.That(tracked.RemoteBook.TargetEditionIds.Values, Is.EquivalentTo(history.Select(row => row.EditionId)));
            }, media);
        }

        [TestCase(false, BookMediaType.Audiobook)]
        [TestCase(true, BookMediaType.Audiobook)]
        [TestCase(false, BookMediaType.Ebook)]
        [TestCase(true, BookMediaType.Ebook)]
        public async Task satisfied_copy_should_not_veto_the_independently_wanted_copy(bool postgres, BookMediaType media)
        {
            await Run(postgres, scene =>
            {
                scene.Books[0].BookFiles = new List<NzbDrone.Core.MediaFiles.BookFile>
                {
                    new() { Quality = new QualityModel(media == BookMediaType.Audiobook ? Quality.MP3 : Quality.EPUB) }
                };
                var cutoff = new CutoffSpecification(new UpgradableSpecification(scene.Config, Logger), scene.Formats, Logger);
                var decisions = scene.Maker(cutoff).GetRssDecision(new List<ReleaseInfo> { scene.Release(media) });
                Assert.That(decisions.Single(decision => decision.RemoteBook.Books[0].Id == scene.Books[0].Id).Rejected, Is.True);
                Assert.That(decisions.Single(decision => decision.RemoteBook.Books[0].Id == scene.Books[1].Id).Approved, Is.True);
                return Task.CompletedTask;
            }, media);
        }

        [TestCase(false, BookMediaType.Audiobook)]
        [TestCase(true, BookMediaType.Audiobook)]
        [TestCase(false, BookMediaType.Ebook)]
        [TestCase(true, BookMediaType.Ebook)]
        public async Task alternative_pending_should_prune_only_unmonitored_original_copy_and_never_recall_another(bool postgres, BookMediaType media)
        {
            await Run(postgres, scene =>
            {
                var remote = new RemoteBook
                {
                    Books = scene.Books.ToList(), Author = scene.Author, Release = scene.Release(media), AlternativeCopies = true,
                    ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(media == BookMediaType.Audiobook ? Quality.M4B : Quality.EPUB) },
                    TargetEditionIds = scene.Books.ToDictionary(book => book.Id, book => book.Editions[0].Id)
                };
                var pending = scene.Pending(); pending.Add(new DownloadDecision(remote), PendingReleaseReason.Delay);
                scene.Books[0].Monitored = scene.Books[0].AudiobookMonitored = scene.Books[0].EbookMonitored = false;
                scene.BookWriter.SetFields(scene.Books[0], book => book.AudiobookMonitored, book => book.EbookMonitored);
                var reports = pending.GetPending();
                Assert.That(reports, Has.Count.EqualTo(1));
                var stored = new PendingReleaseRepository(scene.Database, scene.Events).All().Single();
                Assert.That(stored.AdditionalInfo.AlternativeCopies, Is.True);
                Assert.That(stored.AdditionalInfo.BookIds, Is.EqualTo(new[] { scene.Books[1].Id }));
                Assert.That(stored.AdditionalInfo.TargetEditionIds.Keys, Is.EqualTo(new[] { scene.Books[1].Id }));
                var decisions = scene.Maker().GetRssDecision(reports);
                Assert.That(decisions.Single().RemoteBook.Books.Single().Id, Is.EqualTo(scene.Books[1].Id));
                Assert.That(decisions.Single().Approved, Is.True);
                return Task.CompletedTask;
            }, media);
        }

        [TestCase(false, BookMediaType.Audiobook)]
        [TestCase(true, BookMediaType.Audiobook)]
        [TestCase(false, BookMediaType.Ebook)]
        [TestCase(true, BookMediaType.Ebook)]
        public async Task newest_grab_ids_should_win_even_if_its_clock_date_is_earlier(bool postgres, BookMediaType media)
        {
            await Run(postgres, scene =>
            {
                var repository = new HistoryRepository(scene.Database, scene.Events);
                var later = DateTime.UtcNow; var earlier = later.AddHours(-1);
                foreach (var book in scene.Books)
                {
                    repository.Insert(new EntityHistory { BookId = book.Id, AuthorId = scene.Author.Id, EditionId = book.Editions[0].Id,
                        DownloadId = "REUSED", Quality = new QualityModel(media == BookMediaType.Audiobook ? Quality.M4B : Quality.EPUB), SourceTitle = scene.Release(media).Title, Date = later, EventType = EntityHistoryEventType.Grabbed,
                        Data = new Dictionary<string, string> { [EntityHistory.ALTERNATIVE_COPIES] = bool.TrueString } });
                }

                repository.Insert(new EntityHistory { BookId = scene.Books[1].Id, AuthorId = scene.Author.Id, EditionId = scene.Books[1].Editions[0].Id,
                    DownloadId = "REUSED", Quality = new QualityModel(media == BookMediaType.Audiobook ? Quality.M4B : Quality.EPUB), SourceTitle = scene.Release(media).Title, Date = earlier, EventType = EntityHistoryEventType.Grabbed,
                    Data = new Dictionary<string, string> { [EntityHistory.ALTERNATIVE_COPIES] = bool.FalseString } });
                var tracked = scene.Track("REUSED");
                Assert.That(tracked.RemoteBook.Books.Select(book => book.Id), Is.EqualTo(new[] { scene.Books[1].Id }));
                Assert.That(tracked.RemoteBook.AlternativeCopies, Is.False);
                var queue = new QueueService(scene.Events, scene.History, scene.Formats);
                queue.Handle(new TrackedDownloadRefreshedEvent(new List<TrackedDownload> { tracked }));
                Assert.That(queue.GetQueue().Single().TargetBookIds, Is.EqualTo(new[] { scene.Books[1].Id }));
                return Task.CompletedTask;
            }, media);
        }

        [TestCase(false, BookMediaType.Audiobook)] [TestCase(true, BookMediaType.Audiobook)]
        [TestCase(false, BookMediaType.Ebook)] [TestCase(true, BookMediaType.Ebook)]
        public Task rss_delay_capture_should_survive_single_queue_grab_without_second_dispatch(bool postgres, BookMediaType media)
        {
            return Run(postgres, scene =>
            {
                var database = scene.Database;
                var books = scene.Books;
                var history = scene.History;
                var events = scene.Events;
                var pending = scene.Pending();
                var native = new PendingReleaseRepository(database, events);
                var holding = true;
                var delay = InterfaceProxy.Proxy<IDecisionEngineSpecification>((method, args) => method.Name switch
                {
                    "get_Priority" => SpecificationPriority.Default,
                    "get_Type" => RejectionType.Temporary,
                    nameof(IDecisionEngineSpecification.IsSatisfiedBy) => holding ? Decision.RejectSoftFilter("Waiting period", "Delay") : Decision.Accept(),
                    _ => throw new InvalidOperationException(method.Name)
                });
                var maker = scene.Maker(delay);
                var prioritizer = scene.Prioritizer();
                var report = scene.Release(media, "same-physical-release");
                var sent = new List<RemoteBook>();
                var downloader = InterfaceProxy.Proxy<IDownloadService>((method, args) =>
                {
                    var remote = (RemoteBook)args[0];
                    sent.Add(remote);
                    var message = new BookGrabbedEvent(remote) { DownloadId = "download-" + sent.Count, DownloadClient = "Offline fixture" };
                    history.Handle(message);
                    new DownloadHistoryService(new DownloadHistoryRepository(database, events), history).Handle(message);
                    pending.Handle(message);
                    return Task.CompletedTask;
                });
                var processor = new ProcessDownloadDecisions(downloader, prioritizer, pending, new Chaptarr.Core.Test.Download.EmptyQueueSpecification(), LogManager.GetCurrentClassLogger());
                var fetchNumber = 0;
                var fetcher = InterfaceProxy.Proxy<IFetchAndParseRss>((method, args) => Task.FromResult(fetchNumber++ == 0 ? new List<ReleaseInfo> { report } : new List<ReleaseInfo>()));
                var rss = new RssSyncService(null, null, fetcher, maker, processor, pending, events, LogManager.GetCurrentClassLogger());
                rss.Execute(new RssSyncCommand());
                var initial = native.All().OrderBy(row => row.Id).ToList();
                Assert.That(initial, Has.Count.EqualTo(1));
                Assert.That(initial.All(row => row.Reason == PendingReleaseReason.Delay && row.AdditionalInfo.AlternativeCopies && row.AdditionalInfo.BookIds.Count == 2), Is.True);
                Assert.That(sent, Is.Empty);
                var queue = pending.GetPendingQueue();
                var selected = queue.Single(row => row.Book.Id == books[0].Id);
                new QueueActionController(pending, processor).Grab(selected.Id).GetAwaiter().GetResult();
                Assert.That(sent, Has.Count.EqualTo(1));
                Assert.That(sent[0].Books.Select(book => book.Id), Is.EquivalentTo(books.Select(book => book.Id)));
                var afterManual = native.All().ToList();
                Assert.That(afterManual, Is.Empty);
                Assert.That(history.FindByDownloadId("download-1").Select(row => row.BookId), Is.EquivalentTo(books.Select(book => book.Id)));
                holding = false;
                rss.Execute(new RssSyncCommand());
                Assert.That(sent, Has.Count.EqualTo(1));
                Assert.That(sent.Select(remote => remote.Release.Guid).Distinct(), Is.EqualTo(new[] { report.Guid }));
                Assert.That(native.All(), Is.Empty);
                return Task.CompletedTask;
            }, media);
        }

        private static RemoteBook Context(Scene scene, ReleaseInfo report, bool alternatives = true, params Book[] targets) => new()
        {
            Books = (targets.Length == 0 ? scene.Books : targets.ToList()).ToList(), Author = scene.Author, Release = report,
            AlternativeCopies = alternatives, ReleaseSource = ReleaseSourceType.Rss, DownloadAllowed = true,
            ParsedBookInfo = new ParsedBookInfo { AuthorName = scene.Author.Name, BookTitle = "Paper Lanterns", Quality = new QualityModel(scene.Books[0].MediaType == BookMediaType.Audiobook ? Quality.M4B : Quality.EPUB) },
            TargetEditionIds = (targets.Length == 0 ? scene.Books : targets.ToList()).ToDictionary(book => book.Id, book => book.Editions[0].Id)
        };

        private static CustomFormat ConfigurePreferredNarrator(Scene scene)
        {
            var format = new CustomFormat { Id = 3, Name = BuiltInCustomFormats.PreferredNarratorName, BuiltInKey = BuiltInCustomFormats.PreferredNarratorKey,
                Specifications = new List<ICustomFormatSpecification> { new PreferredNarratorSpecification { Name = BuiltInCustomFormats.PreferredNarratorName } } };
            scene.Formats = new CustomFormatCalculationService(InterfaceProxy.Proxy<ICustomFormatService>((method, args) => new List<CustomFormat> { format }), Logger);
            var profile = scene.Author.AudiobookQualityProfile.Value;
            profile.CutoffFormatScore = 100;
            profile.FormatItems = new List<ProfileFormatItem> { new() { Format = format, Score = 100 } };
            return format;
        }

        private static async Task<ProcessedDecisions> Process(Scene scene, List<DownloadDecision> decisions, List<RemoteBook> sent)
        {
            var pending = scene.Pending();
            var downloader = InterfaceProxy.Proxy<IDownloadService>((method, args) =>
            {
                var remote = (RemoteBook)args[0]; sent.Add(remote);
                var message = new BookGrabbedEvent(remote) { DownloadId = remote.Release.Guid, DownloadClient = "Offline client" };
                scene.History.Handle(message);
                new DownloadHistoryService(new DownloadHistoryRepository(scene.Database, scene.Events), scene.History).Handle(message);
                pending.Handle(message);
                return Task.CompletedTask;
            });
            return await new ProcessDownloadDecisions(downloader, scene.Prioritizer(), pending, new Chaptarr.Core.Test.Download.EmptyQueueSpecification(), Logger).ProcessDecisions(decisions);
        }

        [TestCase(false)] [TestCase(true)]
        public async Task each_copy_should_keep_its_own_real_narrator_score_and_preferred_release(bool postgres)
        {
            await Run(postgres, async scene =>
            {
                ConfigurePreferredNarrator(scene);
                var first = scene.Release(BookMediaType.Audiobook, "reader-one"); first.Narrator = "Reader One";
                var second = scene.Release(BookMediaType.Audiobook, "reader-two"); second.Narrator = "Reader Two";
                var decisions = scene.Maker().GetRssDecision(new List<ReleaseInfo> { first, second });
                Assert.That(decisions, Has.Count.EqualTo(4));
                Assert.That(decisions.Single(row => row.RemoteBook.Books[0].Id == scene.Books[0].Id && row.RemoteBook.Release == first).RemoteBook.CustomFormatScore, Is.EqualTo(100));
                Assert.That(decisions.Single(row => row.RemoteBook.Books[0].Id == scene.Books[1].Id && row.RemoteBook.Release == first).RemoteBook.CustomFormatScore, Is.Zero);
                Assert.That(scene.HistoryReads, Is.EqualTo(1));
                var sent = new List<RemoteBook>(); await Process(scene, decisions, sent);
                Assert.That(sent, Has.Count.EqualTo(2));
                Assert.That(sent.Single(row => row.Books[0].Id == scene.Books[0].Id).Release.Guid, Is.EqualTo(first.Guid));
                Assert.That(sent.Single(row => row.Books[0].Id == scene.Books[1].Id).Release.Guid, Is.EqualTo(second.Guid));
                Assert.That(sent.All(row => row.Books.Count == 1), Is.True);
            }, BookMediaType.Audiobook);
        }

        [TestCase(false, false)] [TestCase(true, false)]
        [TestCase(false, true)] [TestCase(true, true)]
        public async Task alternative_queue_rows_should_show_their_own_formats_and_allow_the_other_rows_upgrade(bool postgres, bool torrent)
        {
            await Run(postgres, async scene =>
            {
                var format = ConfigurePreferredNarrator(scene);
                var report = scene.Release(BookMediaType.Audiobook, torrent: torrent); report.Narrator = "Reader One";
                var pending = scene.Pending(); pending.Add(new DownloadDecision(Context(scene, report)), PendingReleaseReason.Delay);
                var pendingRows = pending.GetPendingQueue().ToResource(false, false);
                Assert.That(pendingRows.Single(row => row.BookId == scene.Books[0].Id).CustomFormatScore, Is.EqualTo(100));
                Assert.That(pendingRows.Single(row => row.BookId == scene.Books[1].Id).CustomFormatScore, Is.Zero);
                var sent = new List<RemoteBook>(); await Process(scene, scene.Maker().GetRssDecision(new List<ReleaseInfo> { report }), sent);
                Assert.That(sent, Has.Count.EqualTo(1));
                var history = scene.History.FindByDownloadId(report.Guid);
                Assert.That(history.Single(row => row.BookId == scene.Books[0].Id).GetDataValue("CustomFormatScore"), Is.EqualTo("100"));
                Assert.That(history.Single(row => row.BookId == scene.Books[1].Id).GetDataValue("CustomFormatScore"), Is.EqualTo("0"));
                var queue = new QueueService(scene.Events, scene.History, scene.Formats);
                queue.Handle(new TrackedDownloadRefreshedEvent(new List<TrackedDownload> { scene.Track(report.Guid) }));
                var rows = queue.GetQueue();
                Assert.That(rows, Has.Count.EqualTo(2));
                var resources = rows.ToResource(false, false);
                Assert.That(resources.Single(row => row.BookId == scene.Books[0].Id).CustomFormatScore, Is.EqualTo(100));
                Assert.That(resources.Single(row => row.BookId == scene.Books[0].Id).CustomFormats.Select(value => value.Name), Does.Contain(format.Name));
                Assert.That(resources.Single(row => row.BookId == scene.Books[1].Id).CustomFormatScore, Is.Zero);
                Assert.That(resources.Single(row => row.BookId == scene.Books[1].Id).CustomFormats, Is.Empty);
                var candidateReport = scene.Release(BookMediaType.Audiobook, "better-for-second"); candidateReport.Narrator = "Reader Two";
                var candidate = Context(scene, candidateReport, true, scene.Books[1]);
                candidate.CustomFormats = scene.Formats.ParseCustomFormat(candidate, candidateReport.Size);
                candidate.CustomFormatScore = 100;
                var specification = new QueueSpecification(queue, new UpgradableSpecification(scene.Config, Logger), scene.Formats, scene.Config, Logger);
                Assert.That(specification.IsSatisfiedBy(candidate, null).Accepted, Is.True);
            }, BookMediaType.Audiobook);
        }

        [TestCase(false, BookMediaType.Audiobook)] [TestCase(true, BookMediaType.Audiobook)]
        [TestCase(false, BookMediaType.Ebook)] [TestCase(true, BookMediaType.Ebook)]
        public async Task delay_only_copy_should_join_the_other_eligible_copys_same_release_grab(bool postgres, BookMediaType media)
        {
            await Run(postgres, async scene =>
            {
                var pending = scene.Pending();
                var older = scene.Release(media, "old-pending"); older.PublishDate = DateTime.UtcNow.AddHours(-3);
                pending.Add(new DownloadDecision(Context(scene, older, true, scene.Books[1])), PendingReleaseReason.Delay);
                var delay = new DelaySpecification(pending, new UpgradableSpecification(scene.Config, Logger),
                    InterfaceProxy.Proxy<IDelayProfileService>((method, args) => new DelayProfile { PreferredProtocol = DownloadProtocol.Torrent, TorrentDelay = 90 }),
                    InterfaceProxy.Proxy<IMediaFileService>((method, args) => new List<BookFile>()), Logger);
                var decisions = scene.Maker(delay).GetRssDecision(new List<ReleaseInfo> { scene.Release(media) });
                Assert.That(decisions.Single(row => row.RemoteBook.Books[0].Id == scene.Books[0].Id).TemporarilyRejected, Is.True);
                Assert.That(decisions.Single(row => row.RemoteBook.Books[0].Id == scene.Books[1].Id).Approved, Is.True);
                var sent = new List<RemoteBook>(); await Process(scene, decisions, sent);
                Assert.That(sent.Single().Books.Select(book => book.Id), Is.EquivalentTo(scene.Books.Select(book => book.Id)));
                Assert.That(new PendingReleaseRepository(scene.Database, scene.Events).All(), Is.Empty);
                Assert.That(scene.History.FindByDownloadId(sent.Single().Release.Guid).Select(row => row.BookId),
                    Is.EquivalentTo(scene.Books.Select(book => book.Id)));
            }, media);
        }

        [TestCase(false, BookMediaType.Audiobook)] [TestCase(true, BookMediaType.Audiobook)]
        [TestCase(false, BookMediaType.Ebook)] [TestCase(true, BookMediaType.Ebook)]
        public async Task rejecting_one_saved_alternative_should_keep_the_other_original_target(bool postgres, BookMediaType media)
        {
            await Run(postgres, scene =>
            {
                var report = scene.Release(media); var pending = scene.Pending();
                pending.Add(new DownloadDecision(Context(scene, report)), PendingReleaseReason.Delay);
                var rejected = new DownloadDecision(Context(scene, report, true, scene.Books[0]), new Rejection("Wanted-copy mismatch", RejectionType.Permanent));
                pending.Handle(new RssSyncCompleteEvent(new ProcessedDecisions(new(), new(), new() { rejected })));
                var saved = new PendingReleaseRepository(scene.Database, scene.Events).All().Single();
                Assert.That(saved.AdditionalInfo.BookIds, Is.EqualTo(new[] { scene.Books[1].Id }));
                Assert.That(saved.AdditionalInfo.TargetEditionIds.Keys, Is.EqualTo(new[] { scene.Books[1].Id }));
                return Task.CompletedTask;
            }, media);
        }

        [TestCase(false, BookMediaType.Audiobook)] [TestCase(true, BookMediaType.Audiobook)]
        [TestCase(false, BookMediaType.Ebook)] [TestCase(true, BookMediaType.Ebook)]
        public async Task ordinary_required_collection_should_keep_whole_context_and_one_unmonitor_should_cancel_it(bool postgres, BookMediaType media)
        {
            await Run(postgres, async scene =>
            {
                var remote = Context(scene, scene.Release(media), false);
                var sent = new List<RemoteBook>(); await Process(scene, new() { new DownloadDecision(remote) }, sent);
                Assert.That(sent.Single().Books.Select(book => book.Id), Is.EquivalentTo(scene.Books.Select(book => book.Id)));
                Assert.That(scene.Track(remote.Release.Guid).RemoteBook.AlternativeCopies, Is.False);
                var pending = scene.Pending(); pending.Add(new DownloadDecision(remote), PendingReleaseReason.Delay);
                scene.Books[0].AudiobookMonitored = scene.Books[0].EbookMonitored = false;
                scene.BookWriter.SetFields(scene.Books[0], book => book.AudiobookMonitored, book => book.EbookMonitored);
                Assert.That(pending.GetPending(), Is.Empty);
                Assert.That(new PendingReleaseRepository(scene.Database, scene.Events).All(), Is.Empty);
            }, media);
        }

        [TestCase(false, BookMediaType.Audiobook)] [TestCase(true, BookMediaType.Audiobook)]
        [TestCase(false, BookMediaType.Ebook)] [TestCase(true, BookMediaType.Ebook)]
        public async Task failed_batched_history_read_should_preserve_pending_rss_and_keep_search_error_hard(bool postgres, BookMediaType media)
        {
            await Run(postgres, scene =>
            {
                var previousLogging = LogManager.Configuration;
                var target = new MemoryTarget { Name = "BatchErrorCapture", Layout = "${message}" };
                var logging = new LoggingConfiguration();
                logging.AddRule(LogLevel.Error, LogLevel.Fatal, target, Logger.Name);
                LogManager.Configuration = logging;
                try
                {
                    var pending = scene.Pending(); var report = scene.Release(media);
                    pending.Add(new DownloadDecision(Context(scene, report)), PendingReleaseReason.Delay);
                    scene.FailHistoryRead = true;
                    var maker = scene.Maker(); var rss = maker.GetRssDecision(pending.GetPending());
                    Assert.That(rss, Is.Empty);
                    pending.Handle(new RssSyncCompleteEvent(new ProcessedDecisions(new(), new(), new())));
                    Assert.That(new PendingReleaseRepository(scene.Database, scene.Events).All(), Has.Count.EqualTo(1));
                    var search = maker.GetSearchDecision(new() { report, scene.Release(media, "second-report") }, new BookSearchCriteria { Author = scene.Author, Books = new() { scene.Books[0] }, AuthorCatalog = scene.Books });
                    Assert.That(search, Has.Count.EqualTo(2));
                    Assert.That(search.All(decision => !decision.Approved && decision.Rejections.Any(rejection => rejection.Category == "Error" && !rejection.CanBypass)), Is.True);
                    Assert.That(target.Logs, Has.Count.EqualTo(2));
                }
                finally
                {
                    LogManager.Configuration = previousLogging;
                    target.Dispose();
                }
                return Task.CompletedTask;
            }, media);
        }

        [TestCase(false, BookMediaType.Audiobook)] [TestCase(true, BookMediaType.Audiobook)]
        [TestCase(false, BookMediaType.Ebook)] [TestCase(true, BookMediaType.Ebook)]
        public async Task restored_singleton_pending_contexts_for_the_same_release_should_dispatch_once(bool postgres, BookMediaType media)
        {
            await Run(postgres, async scene =>
            {
                var pending = scene.Pending(); var report = scene.Release(media);
                pending.AddMany(scene.Books.Select(book => Tuple.Create(new DownloadDecision(Context(scene, report, true, book)), PendingReleaseReason.Delay)).ToList());
                var restored = pending.GetPending();
                Assert.That(restored, Has.Count.EqualTo(2));
                Assert.That(restored[0], Is.Not.SameAs(restored[1]));
                var decisions = scene.Maker().GetRssDecision(restored);
                Assert.That(decisions, Has.Count.EqualTo(2));
                Assert.That(scene.HistoryReads, Is.EqualTo(1));
                var sent = new List<RemoteBook>(); await Process(scene, decisions, sent);
                Assert.That(sent, Has.Count.EqualTo(1));
                Assert.That(sent[0].Books.Select(book => book.Id), Is.EquivalentTo(scene.Books.Select(book => book.Id)));
                Assert.That(scene.History.FindByDownloadId(report.Guid), Has.Count.EqualTo(2));
            }, media);
        }

        [TestCase(false, BookMediaType.Audiobook)] [TestCase(true, BookMediaType.Audiobook)]
        [TestCase(false, BookMediaType.Ebook)] [TestCase(true, BookMediaType.Ebook)]
        public async Task ordinary_pending_with_a_missing_required_target_should_cancel_without_sending_a_subset(bool postgres, BookMediaType media)
        {
            await Run(postgres, scene =>
            {
                var pending = scene.Pending(); pending.Add(new DownloadDecision(Context(scene, scene.Release(media), false)), PendingReleaseReason.Delay);
                var editions = new EditionRepository(scene.Database, scene.Events);
                editions.DeleteMany(editions.FindByBook(new[] { scene.Books[1].Id }));
                scene.BookWriter.Delete(scene.Books[1].Id);
                var restored = pending.GetPending();
                Assert.That(restored, Is.Empty);
                Assert.That(scene.Maker().GetRssDecision(restored), Is.Empty);
                Assert.That(new PendingReleaseRepository(scene.Database, scene.Events).All(), Is.Empty);
                return Task.CompletedTask;
            }, media);
        }

        [TestCase(false, BookMediaType.Audiobook)] [TestCase(true, BookMediaType.Audiobook)]
        [TestCase(false, BookMediaType.Ebook)] [TestCase(true, BookMediaType.Ebook)]
        public async Task recalled_copy_batch_should_share_catalog_and_history_reads(bool postgres, BookMediaType media)
        {
            await Run(postgres, scene =>
            {
                var reports = Enumerable.Range(0, 3).Select(index => scene.Release(media, "batch-" + index)).ToList();
                var timer = System.Diagnostics.Stopwatch.StartNew();
                var decisions = scene.Maker().GetRssDecision(reports);
                timer.Stop();
                Assert.That(decisions, Has.Count.EqualTo(60));
                Assert.That(decisions.All(decision => decision.Approved && decision.RemoteBook.Books.Count == 1), Is.True);
                Assert.That(scene.CatalogReads, Is.EqualTo(1));
                Assert.That(scene.HistoryReads, Is.EqualTo(1));
                Assert.That(scene.ExistingBookReads, Is.EqualTo(3));
                TestContext.Out.WriteLine($"RSS copy batch: provider={(postgres ? "PostgreSQL" : "SQLite")}, media={media}, reports=3, recalledRowsPerReport=20, decisions=60, catalogReads={scene.CatalogReads}, historyBatchReads={scene.HistoryReads}, bookBatchReads={scene.ExistingBookReads}, elapsedMs={timer.Elapsed.TotalMilliseconds:F3}");
                return Task.CompletedTask;
            }, media, copies: 20);
        }

        [TestCase(false, BookMediaType.Audiobook)] [TestCase(true, BookMediaType.Audiobook)]
        [TestCase(false, BookMediaType.Ebook)] [TestCase(true, BookMediaType.Ebook)]
        public async Task one_failed_copy_evaluation_should_publish_none_of_that_reports_decisions(bool postgres, BookMediaType media)
        {
            await Run(postgres, scene =>
            {
                var faulted = scene.Release(media, "faulted");
                var healthy = scene.Release(media, "healthy");
                var spec = InterfaceProxy.Proxy<IDecisionEngineSpecification>((method, args) => method.Name switch
                {
                    "get_Priority" => SpecificationPriority.Database,
                    "get_Type" => RejectionType.Permanent,
                    nameof(IDecisionEngineSpecification.IsSatisfiedBy) => ((RemoteBook)args[0]).Release.Guid == faulted.Guid &&
                        ((RemoteBook)args[0]).Books.Single().Id == scene.Books[1].Id
                        ? throw new InvalidOperationException("Injected copy-evaluation failure") : Decision.Accept(),
                    _ => throw new NotImplementedException(method.Name)
                });
                var decisions = scene.Maker(spec).GetRssDecision(new List<ReleaseInfo> { faulted, healthy });
                Assert.That(decisions, Has.Count.EqualTo(2));
                Assert.That(decisions.All(decision => decision.Approved && decision.RemoteBook.Release == healthy), Is.True);
                Assert.That(decisions.SelectMany(decision => decision.RemoteBook.Books).Select(book => book.Id),
                    Is.EquivalentTo(scene.Books.Select(book => book.Id)));
                return Task.CompletedTask;
            }, media);
        }

        [TestCase(false, BookMediaType.Audiobook, false)] [TestCase(true, BookMediaType.Audiobook, false)]
        [TestCase(false, BookMediaType.Ebook, false)] [TestCase(true, BookMediaType.Ebook, false)]
        [TestCase(false, BookMediaType.Audiobook, true)] [TestCase(true, BookMediaType.Audiobook, true)]
        [TestCase(false, BookMediaType.Ebook, true)] [TestCase(true, BookMediaType.Ebook, true)]
        public async Task rss_should_reuse_raw_pending_ages_including_blocked_fallback_rows(bool postgres, BookMediaType media, bool blockedIndexer)
        {
            await Run(postgres, scene =>
            {
                var pending = scene.Pending(blockedIndexer);
                var older = scene.Release(media, "older-pending");
                older.IndexerId = 8; older.PublishDate = DateTime.UtcNow.AddHours(-3);
                pending.Add(new DownloadDecision(Context(scene, older, true, scene.Books[1])), PendingReleaseReason.Fallback);
                var replay = pending.GetPending(out var dates);
                Assert.That(replay.Count, Is.EqualTo(blockedIndexer ? 0 : 1));
                Assert.That(dates[(scene.Author.Id, scene.Books[1].Id)], Is.EqualTo(new DateTime(older.PublishDate.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc)));
                var extraReads = 0;
                var delay = new DelaySpecification(InterfaceProxy.Proxy<IPendingReleaseService>((method, args) =>
                    { extraReads++; throw new InvalidOperationException("Prepared RSS batch must not reload pending evidence"); }),
                    new UpgradableSpecification(scene.Config, Logger),
                    InterfaceProxy.Proxy<IDelayProfileService>((method, args) => new DelayProfile { PreferredProtocol = DownloadProtocol.Torrent, TorrentDelay = 90 }),
                    InterfaceProxy.Proxy<IMediaFileService>((method, args) => new List<BookFile>()), Logger);
                var reports = Enumerable.Range(0, 3).Select(index => scene.Release(media, "fresh-" + index)).ToList();
                var decisions = scene.Maker(delay).GetRssDecision(reports, false, dates);
                Assert.That(decisions, Has.Count.EqualTo(6));
                Assert.That(decisions.Where(decision => decision.RemoteBook.Books.Single().Id == scene.Books[0].Id).All(decision => decision.TemporarilyRejected), Is.True);
                Assert.That(decisions.Where(decision => decision.RemoteBook.Books.Single().Id == scene.Books[1].Id).All(decision => decision.Approved), Is.True);
                Assert.That(decisions.All(decision => ReferenceEquals(decision.RemoteBook.PreparedPendingReleaseDates, dates)), Is.True);
                Assert.That(extraReads, Is.Zero);
                Assert.That(scene.ExistingBookReads, Is.EqualTo(4));
                Assert.That(scene.HistoryReads, Is.EqualTo(1));
                return Task.CompletedTask;
            }, media);
        }

        [TestCase(false, BookMediaType.Audiobook)] [TestCase(true, BookMediaType.Audiobook)]
        [TestCase(false, BookMediaType.Ebook)] [TestCase(true, BookMediaType.Ebook)]
        public async Task generic_grab_history_should_roundtrip_raw_fields_without_exposing_them_or_claiming_a_cast(bool postgres, BookMediaType media)
        {
            await Run(postgres, scene =>
            {
                var report = scene.Release(media);
                Assert.That(report, Is.Not.InstanceOf<TorrentInfo>());
                report.NarratorEvidenceFields = new List<string> { "Reader One", "Reader Two" };
                var remote = Context(scene, report);
                scene.History.Handle(new BookGrabbedEvent(remote) { DownloadId = "raw-fields", DownloadClient = "Offline client" });
                var stored = scene.History.FindByDownloadId("raw-fields");
                Assert.That(stored.Select(row => row.BookId), Is.EquivalentTo(scene.Books.Select(book => book.Id)));
                foreach (var row in stored)
                {
                    Assert.That(System.Text.Json.JsonSerializer.Deserialize<List<string>>(row.GetDataValue(nameof(ReleaseInfo.NarratorEvidenceFields))),
                        Is.EqualTo(report.NarratorEvidenceFields));
                    Assert.That(row.GetDataValue("Narrator"), Is.Null);
                    Assert.That(row.GetDataValue(EntityHistory.ALTERNATIVE_COPY_INCOMPATIBLE), Is.Null);
                    row.Author = scene.Author;
                    Assert.That(row.ToResource(scene.Formats).Data.Keys.Any(key => string.Equals(key, nameof(ReleaseInfo.NarratorEvidenceFields), StringComparison.OrdinalIgnoreCase)), Is.False);
                    Assert.That(row.GetDataValue(nameof(ReleaseInfo.NarratorEvidenceFields)), Is.Not.Null);
                }

                Assert.That(report.Narrator, Is.Null);
                Assert.That(remote.ParsedBookInfo.Narrator, Is.Null);
                return Task.CompletedTask;
            }, media);
        }

        private static Task Run(bool postgres, Func<Scene, Task> action, BookMediaType media, int copies = 2)
        {
            DualDatabaseFixture.Run(postgres, database => action(new Scene(database, media, copies)).GetAwaiter().GetResult());
            return Task.CompletedTask;
        }
    }
}
