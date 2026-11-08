using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using NzbDrone.Common.Disk;
using NzbDrone.Core.MediaFiles.BookImport;
using Chaptarr.Api.V1.Author;
using Chaptarr.Core.Test.Common;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Cache;
using NzbDrone.Core.AuthorStats;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.MetadataSource;
using BookInfoException = NzbDrone.Core.MetadataSource.BookInfo.BookInfoException;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RootFolders;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    [NonParallelizable]
    public partial class AuthorSyncOptionsFixture
    {
        private static readonly string AudioRootPath = @"C:\fixture\audio".AsOsAgnostic();
        private static readonly string EbookRootPath = @"C:\fixture\ebook".AsOsAgnostic();

        private sealed class Harness
        {
            public readonly AuthorService Authors;
            public readonly BookService Books;
            public readonly BookRepository BookRows;
            public readonly global::NzbDrone.Core.History.HistoryRepository HistoryRows;
            public readonly AuthorRepository AuthorRows;
            public readonly PendingAuthorImportService Pending;
            public readonly AuthorLibraryService Library;
            public readonly IProvideAuthorInfo Info;
            public readonly EditionService Editions;
            public readonly MetadataProfileService Profiles;
            public readonly AuthorController Controller;
            public readonly IRootFolderService Roots;
            public readonly IManageCommandQueue Queue;
            public readonly IEventAggregator Events;
            public readonly Logger Logger = LogManager.GetCurrentClassLogger();
            public readonly List<Command> Commands = new();
            public readonly List<int[]> SearchTargets = new();
            public readonly RootFolder AudioRoot;
            public readonly RootFolder EbookRoot;
            public int MetadataFetches;
            public int CatalogWorkCount = 1;
            public readonly List<BookEditedEvent> BookChanges = new();
            public bool Available = true;
            public bool RejectScan;
            public Exception MetadataError;
            public readonly MediaFileRepository Files;

            public Harness(IMainDatabase database, string fileRoot = null, Logger logger = null)
            {
                if (logger != null) Logger = logger;
                AudioRoot = AuthorLibraryServiceRootFolderInheritanceFixture.BuildAudiobookRoot(
                    fileRoot == null ? AudioRootPath : Path.Combine(fileRoot, "audio"), 3, 2, MonitorTypes.All, true, NewItemMonitorTypes.All);
                EbookRoot = AuthorLibraryServiceRootFolderInheritanceFixture.BuildEbookRoot(
                    fileRoot == null ? EbookRootPath : Path.Combine(fileRoot, "ebooks"), 4, 3, MonitorTypes.None, true, NewItemMonitorTypes.All);
                AudioRoot.DefaultSyncMonitoredAcrossFormats = true;
                EbookRoot.DefaultSyncMonitoredAcrossFormats = true;
                Roots = new AuthorLibraryServiceRootFolderInheritanceFixture.StubRootFolderService(AudioRoot, EbookRoot);
                var paths = new AuthorPathBuilder(Proxy<IBuildFileNames>((_, args) => ((Author)args[0]).Name), Roots);
                Events = Proxy<IEventAggregator>((_, args) =>
                {
                    if (args[0] is BookEditedEvent bookEdited) BookChanges.Add(bookEdited);
                    if (args[0] is AuthorSetupMetadataRefusedEvent refused) Pending.Handle(refused);
                    if (args[0] is PendingAuthorImportFailedEvent failed) Pending.Handle(failed);
                    if (args[0] is PendingAuthorImportCancelledEvent cancelled) Pending.Handle(cancelled);
                    if (args[0] is AuthorEditedEvent edited)
                        new AuthorEditedService(Queue, Logger).Handle(edited);
                    if (args[0] is AuthorScannedEvent scanned)
                        new AuthorScannedHandler(new BookMonitoredService(Authors, Books, Logger), Authors, Queue,
                            Proxy<IBookAddedService>((_, _) => null), Events, Logger, Books).Handle(scanned);
                    return null;
                });
                Files = new MediaFileRepository(database, Events);
                BookRows = new BookRepository(database, Events);
                HistoryRows = new global::NzbDrone.Core.History.HistoryRepository(database, Events);
                Queue = Proxy<IManageCommandQueue>((method, args) =>
                {
                    if (method.Name != "Push") throw new NotSupportedException(method.Name);
                    var command = (Command)args[0];
                    command.Trigger = (CommandTrigger)args[2];
                    if (RejectScan && command is RefreshAuthorCommand) throw new InvalidOperationException("Scan queue unavailable");
                    Commands.Add(command);
                    if (command is MissingBookSearchCommand search)
                        SearchTargets.Add(BookRows.GetMissingBookSearchTargets(Enum.TryParse<BookMediaType>(search.MediaType, true, out var searchSide) ? searchSide : null, search.AuthorId).Select(target => target.BookId).ToArray());
                    return new CommandModel { Id = Commands.Count, Name = command.Name, Body = command };
                });
                AuthorRows = new AuthorRepository(database, Events, Logger);
                Authors = new AuthorService(AuthorRows, Events, paths, Roots, Queue,
                    new CacheManager(), BookRows, null, Logger, null, null, null, database);
                var editions = new EditionService(new EditionRepository(database, Events), Events, Logger);
                Editions = editions;
                Books = new BookService(BookRows, editions, Events, Authors, Proxy<IMediaFileService>((_, _) => new List<BookFile>()),
                    Roots, new SeriesBookLinkRepository(database, Events), null, Logger);
                Pending = new PendingAuthorImportService(new PendingAuthorImportRepository(database, Events), Authors, Events, Logger);
                var info = Proxy<IProvideAuthorInfo>((method, args) =>
                {
                    if (method.Name != nameof(IProvideAuthorInfo.GetAuthorInfo)) throw new NotSupportedException(method.Name);
                    MetadataFetches++;
                    if (MetadataError != null) throw MetadataError;
                    if (!Available) throw new AuthorNotFoundException((string)args[0]);
                    var author = new Author { Name = "Sync Options Writer", CleanName = "syncoptionswriter", HardcoverAuthorId = "hc:123456",
                        TitleSlug = "sync-options-writer", Overview = string.Empty, Books = new List<Book>(), Series = new List<Series>() };
                    foreach (var side in new[] { BookMediaType.Audiobook, BookMediaType.Ebook })
                        for (var index = 0; index < CatalogWorkCount; index++)
                        {
                            var suffix = CatalogWorkCount == 1 ? "" : " " + index;
                            author.Books.Add(new Book { Author = author, Overview = string.Empty, Title = "Sync Options Work" + suffix, CleanTitle = "syncoptionswork" + suffix.Trim(), TitleSlug = "sync-options-work-" + side + (CatalogWorkCount == 1 ? "" : "-" + index),
                                MediaType = side, HardcoverBookId = "hc:" + (456789 + index), BaseBookId = "hc:" + (456789 + index), ReleaseDate = DateTime.UtcNow.AddDays(-10),
                                Editions = new List<Edition> { new() { Title = "Sync Options Work" + suffix, ForeignEditionId = "hc-ed:" + (CatalogWorkCount == 1 ? (side == BookMediaType.Audiobook ? 111 : 222) : 100000 + index * 2 + (int)side),
                                    Language = "eng", ReadingFormatId = side == BookMediaType.Audiobook ? 2 : 3, Monitored = true } } });
                        }
                    return author;
                });
                Info = info;
                Library = AuthorLibraryServiceRootFolderInheritanceFixture.BuildService(
                    Authors, info, Roots, Books, Events, pendingImportService: Pending, commandQueue: Queue, database: database);
                var profileRepository = Proxy<IMetadataProfileRepository>((method, args) => method.Name switch
                {
                    "Exists" => true,
                    "Get" or "Find" => new MetadataProfile { Id = (int)args[0], Name = "Unrestricted profile",
                        ProfileType = (int)args[0] is 2 or 12 or 22 ? MetadataProfileType.Audiobook : MetadataProfileType.Ebook },
                    _ => throw new NotSupportedException(method.Name)
                });
                Profiles = new MetadataProfileService(profileRepository, Authors, Books, editions,
                    Proxy<IMediaFileService>((_, _) => new List<BookFile>()), null, Roots, null, Events, Logger);
                Set(Library, "_importListExclusionService", Proxy<IImportListExclusionService>((method, _) =>
                    method.Name == nameof(IImportListExclusionService.FindByForeignId) ? new List<ImportListExclusion>()
                    : throw new NotSupportedException(method.Name)));
                Set(Library, "_metadataProfileService", Profiles);
                Set(Library, "_editionService", editions);
                Set(Library, "_authorPathBuilder", paths);
                Set(Library, "_narratorLinkService", Proxy<INarratorLinkService>((_, _) => null));
                Set(Library, "_refreshAuthorService", HydrationRefresh(this, database, info));
                Controller = AuthorLibraryServiceRootFolderInheritanceFixture.BuildModalController(Authors, Library, Roots, Queue, Events, Books);
                Set(Controller, "_bookMonitoredService", new BookMonitoredService(Authors, Books, Logger));
                Set(Controller, "_fileNameBuilder", Proxy<IBuildFileNames>((_, _) => "Sync Options Writer"));
                Set(Controller, "_coverMapper", Proxy<IMapCoversToLocal>((_, _) => null));
                Set(Controller, "_authorStatisticsService", Proxy<IAuthorStatisticsService>((_, _) => new AuthorStatistics()));
            }

            public Author Author => Authors.FindByProviderId("hc", "123456");
            public AuthorImportResource Request(string media = "both", bool? sync = true) => new()
            {
                ForeignAuthorId = "hc:123456", AuthorName = "Sync Options Writer", MediaType = media,
                AudiobookRootFolderPath = media is "audiobook" or "both" ? AudioRoot.Path : null,
                EbookRootFolderPath = media is "ebook" or "both" ? EbookRoot.Path : null,
                AudiobookQualityProfileId = media is "audiobook" or "both" ? 3 : null,
                EbookQualityProfileId = media is "ebook" or "both" ? 4 : null,
                AudiobookMetadataProfileId = media is "audiobook" or "both" ? 2 : null,
                EbookMetadataProfileId = media is "ebook" or "both" ? 3 : null,
                AudiobookMonitored = media is "audiobook" or "both" ? true : null,
                EbookMonitored = media is "ebook" or "both" ? true : null,
                AudiobookMonitorExistingMode = media is "audiobook" or "both" ? MonitorTypes.All : null,
                EbookMonitorExistingMode = media is "ebook" or "both" ? MonitorTypes.All : null,
                AudiobookMonitorNewItems = media is "audiobook" or "both" ? NewItemMonitorTypes.All : null,
                EbookMonitorNewItems = media is "ebook" or "both" ? NewItemMonitorTypes.All : null,
                SyncMonitoredAcrossFormats = sync, ManualFlag = true, SearchForMissingBooks = false
            };
            public void Import(AuthorImportResource request) => Controller.ImportAuthor(request).GetAwaiter().GetResult();
            public void Scan() => new AuthorScannedHandler(new BookMonitoredService(Authors, Books, Logger), Authors, Queue,
                Proxy<IBookAddedService>((_, _) => null), Events, Logger, Books).Handle(new AuthorScannedEvent(Author));
            public void QueueDiskScan(RefreshAuthorCommand command)
            {
                var refresh = new RefreshAuthorService(MetadataHealthGateStub.Ready(), authorInfo: null, authorService: Authors, bookService: Books,
                    editionService: null, metadataProfileService: null, refreshBookService: null, refreshSeriesService: null,
                    eventAggregator: Events, commandQueueManager: Queue,
                    mediaFileService: Proxy<IMediaFileService>((_, _) => new List<BookFile>()), historyRepository: HistoryRows,
                    rootFolderService: Roots, checkIfAuthorShouldBeRefreshed: null, monitorNewBookService: null,
                    configService: Proxy<IConfigService>((_, _) => RescanAfterRefreshType.Never),
                    importListExclusionService: null, syncMetadataService: null, syncQueueService: null,
                    rootFolderSettingsResolver: null, logger: Logger, bookRepository: BookRows, authorRepository: AuthorRows);
                refresh.Execute(command);
            }
            public void ProcessPending() => new ProcessPendingImportsCommandHandler(Pending, Library, Books, null, Queue, Events, Logger)
                .Execute(new ProcessPendingImportsCommand());
        }

        [Test]
        public void missing_format_setup_loads_once_then_scans_before_file_sensitive_monitoring_and_search(
            [Values(false, true)] bool postgres,
            [Values("audiobook", "ebook")] string missingSide,
            [Values(MonitorTypes.Missing, MonitorTypes.Existing)] MonitorTypes initialMode,
            [Values(false, true)] bool nativeAdd)
        {
            var directory = Path.Combine(Path.GetTempPath(), "chaptarr-author-scan-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                DualDatabaseFixture.Run(postgres, database =>
                {
                    var h = new Harness(database, directory);
                    h.Import(h.Request(missingSide == "ebook" ? "audiobook" : "ebook", false));
                    h.Scan();
                    h.Commands.Clear();
                    h.MetadataFetches = 0;
                    var request = h.Request(missingSide, false);
                    if (missingSide == "audiobook") request.AudiobookMonitorExistingMode = initialMode;
                    else request.EbookMonitorExistingMode = initialMode;
                    request.SearchForMissingBooks = true;
                    var side = missingSide == "audiobook" ? BookMediaType.Audiobook : BookMediaType.Ebook;
                    var folder = Path.Combine(missingSide == "audiobook" ? h.AudioRoot.Path : h.EbookRoot.Path, h.Author.Name);
                    Directory.CreateDirectory(folder);
                    var file = Path.Combine(folder, side == BookMediaType.Audiobook ? "existing.m4b" : "existing.epub");
                    File.WriteAllText(file, "Constructed file for scan lifecycle; media identification is controlled.");
                    if (nativeAdd)
                        h.Controller.AddAuthor(NativeAuthorRequest(request)).GetAwaiter().GetResult();
                    else
                        h.Import(request);

                    Assert.That(h.MetadataFetches, Is.EqualTo(1));
                    var refresh = h.Commands.OfType<RefreshAuthorCommand>().Single();
                    Assert.That(refresh.RefreshMetadata, Is.False, "The catalog was already loaded by import.");
                    Assert.That(refresh.RescanFolders, Is.True);
                    Assert.That(refresh.IsNewAuthor, Is.True, "Initial setup scans even when rescan-after-refresh is disabled.");
                    Assert.That(h.SearchTargets, Is.Empty, "Files must be discovered before selecting missing books.");
                    Assert.That(h.Author.AddOptions.SearchForMissingBooks, Is.True);
                    Assert.That(h.BookRows.GetBooksByAuthorId(h.Author.Id).Single(row => row.MediaType == side).IsMonitored(),
                        Is.False, "File-sensitive initial selection belongs after file discovery.");
                    h.QueueDiskScan(refresh);
                    var scan = h.Commands.OfType<RescanFoldersCommand>().Single();
                    Assert.That(scan.AuthorIds, Is.EqualTo(new[] { h.Author.Id }));
                    Assert.That(scan.Folders, Does.Contain(folder));
                    Assert.That(h.SearchTargets, Is.Empty);

                    var book = h.BookRows.GetBooksByAuthorId(h.Author.Id).Single(row => row.MediaType == side);
                    var imports = 0;
                    var orchestrator = Proxy<IImportOrchestrator>((method, args) =>
                    {
                        Assert.That(method.Name, Is.EqualTo(nameof(IImportOrchestrator.ProcessFilesAsync)));
                        Assert.That(h.SearchTargets, Is.Empty);
                        var found = Directory.GetFiles((string)args[0]).ToList();
                        Assert.That(found, Is.EqualTo(new[] { file }));
                        h.Files.Insert(new BookFile { Path = file, EditionId = book.Editions.Single().Id,
                            DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow, Size = new FileInfo(file).Length, MediaType = missingSide,
                            Quality = new QualityModel(side == BookMediaType.Audiobook ? Quality.M4B : Quality.EPUB), MediaInfo = new MediaInfoModel() });
                        imports++;
                        return Task.FromResult(new OrchestratorImportResult { ScannedFilePaths = found, CleanupSafe = true });
                    });
                    var disk = Proxy<IDiskProvider>((method, args) => method.Name == nameof(IDiskProvider.FolderExists)
                        ? Directory.Exists((string)args[0]) : throw new NotSupportedException(method.Name));
                    var mediaFiles = Proxy<IMediaFileService>((method, args) => method.Name == nameof(IMediaFileService.GetFileWithPath)
                        ? h.Files.GetFileWithPath((List<string>)args[0]) : throw new NotSupportedException(method.Name));
                    var scanner = new DiskScanService(null, disk, null, mediaFiles, null, null, orchestrator, h.Authors, h.Roots,
                        Proxy<IMediaFileTableCleanupService>((_, _) => null), h.Queue, h.Events, h.Logger);
                    scanner.Execute(scan);
                    Assert.That(imports, Is.EqualTo(1));
                    Assert.That(h.SearchTargets, Has.Count.EqualTo(1), "The disk scanner's own completion event must trigger the saved search.");
                    Assert.That(h.Author.AddOptions, Is.Null);
                    h.Scan();
                    Assert.That(h.BookRows.Get(book.Id).IsMonitored(), Is.EqualTo(initialMode == MonitorTypes.Existing));
                    Assert.That(h.SearchTargets, Has.Count.EqualTo(1));
                    Assert.That(h.SearchTargets.Single(), Does.Not.Contain(book.Id));
                    Assert.That(h.Author.AddOptions, Is.Null);
                });
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Test]
        public void unavailable_missing_format_keeps_search_until_successful_setup(
            [Values(false, true)] bool postgres, [Values(false, true)] bool notFound)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                h.Import(h.Request("audiobook", false));
                h.Scan();
                h.Commands.Clear();
                h.MetadataError = notFound ? new AuthorNotFoundException("hc:123456") : new BookInfoException("Metadata is temporarily unavailable");
                var request = h.Request("ebook", false);
                request.SearchForMissingBooks = true;
                var response = h.Controller.ImportAuthor(request).GetAwaiter().GetResult();
                Assert.That(((AuthorResource)((OkObjectResult)response.Result).Value).HydrationWarning, Is.Not.Empty);
                Assert.That(h.Author.EbookRootFolderPath, Is.EqualTo(EbookRootPath));
                Assert.That(h.Author.AddOptions.EbookMonitor, Is.EqualTo(MonitorTypes.All));
                Assert.That(h.Author.AddOptions.SearchForMissingBooks, Is.True);
                Assert.That(h.SearchTargets, Is.Empty);
                Assert.That(h.Commands, Is.Empty);
                h.MetadataError = null;
                h.Import(request);
                Assert.That(h.SearchTargets, Is.Empty);
                h.Scan();
                Assert.That(h.SearchTargets, Has.Count.EqualTo(1));
                Assert.That(h.SearchTargets.Single(), Is.EqualTo(h.BookRows.GetBooksByAuthorId(h.Author.Id)
                    .Where(book => book.MediaType == BookMediaType.Ebook).Select(book => book.Id).ToArray()));
            });
        }

        [Test]
        public void rejected_initial_scan_retains_search_and_retry_does_not_reload_catalog(
            [Values(false, true)] bool postgres, [Values("audiobook", "ebook")] string requestedSide,
            [Values(false, true)] bool sync)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                h.Import(h.Request(requestedSide == "ebook" ? "audiobook" : "ebook", false));
                h.Scan();
                h.Commands.Clear();
                h.MetadataFetches = 0;
                h.RejectScan = true;
                var request = h.Request(requestedSide, sync);
                request.SearchForMissingBooks = true;
                Assert.Throws<InvalidOperationException>(() => h.Import(request));
                Assert.That(h.Author.AddOptions.SearchForMissingBooks, Is.True);
                Assert.That(h.SearchTargets, Is.Empty);
                h.RejectScan = false;
                h.Import(request);
                Assert.That(h.MetadataFetches, Is.EqualTo(1));
                Assert.That(h.Commands.OfType<RefreshAuthorCommand>().Count(), Is.EqualTo(1));
                Assert.That(h.SearchTargets, Is.Empty);
                h.Scan();
                Assert.That(h.SearchTargets, Has.Count.EqualTo(1));
                Assert.That(h.SearchTargets.Single(), Is.EqualTo(h.BookRows.GetBooksByAuthorId(h.Author.Id)
                    .Where(book => book.MediaType == (requestedSide == "ebook" ? BookMediaType.Ebook : BookMediaType.Audiobook)).Select(book => book.Id).ToArray()));
            });
        }

        [Test]
        public void terminal_missing_format_refusal_does_not_save_settings_or_search([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                h.Import(h.Request("audiobook", false));
                h.Scan();
                h.Commands.Clear();
                h.MetadataError = new AuthorTerminalException("author_no_primary_works", "hc:123456", null, "No primary books", false);
                var request = h.Request("ebook", false);
                request.SearchForMissingBooks = true;
                Assert.Throws<AuthorTerminalException>(() => h.Import(request));
                Assert.That(h.Author.EbookRootFolderPath, Is.Null.Or.Empty);
                Assert.That(h.Author.AddOptions, Is.Null);
                Assert.That(h.Commands, Is.Empty);
            });
        }

        private static void Set(object target, string field, object value) => target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(target, value);

        [Test]
        public void sync_eligibility_requires_exact_configured_roots()
        {
            var roots = new[]
            {
                new RootFolder { Path = AudioRootPath, FolderType = FolderType.Audiobook, DefaultSyncMonitoredAcrossFormats = true },
                new RootFolder { Path = EbookRootPath, FolderType = FolderType.Ebook, DefaultSyncMonitoredAcrossFormats = true }
            };
            var author = new Author { AudiobookRootFolderPath = Path.Combine(AudioRootPath, "author"), EbookRootFolderPath = EbookRootPath };
            Assert.That(AuthorMonitoringSyncPolicy.IsEligible(author, roots), Is.False);
            Assert.That(AuthorMonitoringSyncPolicy.ResolveDefault(author, roots).Value, Is.Null);
            author.AudiobookRootFolderPath = AudioRootPath;
            Assert.That(AuthorMonitoringSyncPolicy.IsEligible(author, roots), Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void native_add_preserves_initial_choice_precedence_and_search_waits_for_scan(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var request = h.Request();
                var resource = new AuthorResource
                {
                    ForeignAuthorId = request.ForeignAuthorId, AuthorName = request.AuthorName,
                    AudiobookRootFolderPath = request.AudiobookRootFolderPath, EbookRootFolderPath = request.EbookRootFolderPath,
                    AudiobookQualityProfileId = request.AudiobookQualityProfileId, EbookQualityProfileId = request.EbookQualityProfileId,
                    AudiobookMetadataProfileId = request.AudiobookMetadataProfileId, EbookMetadataProfileId = request.EbookMetadataProfileId,
                    AudiobookMonitored = true, EbookMonitored = true, SyncMonitoredAcrossFormats = true,
                    AudiobookMonitorExistingMode = MonitorTypes.All, EbookMonitorExistingMode = MonitorTypes.All,
                    AddOptions = new AddAuthorOptions { Monitor = MonitorTypes.None, SearchForMissingBooks = true }
                };
                h.Controller.AddAuthor(resource).GetAwaiter().GetResult();
                var refresh = h.Commands.OfType<RefreshAuthorCommand>().Single();
                Assert.That(refresh.RefreshMetadata, Is.False);
                Assert.That(refresh.IsNewAuthor, Is.True);
                Assert.That(refresh.RescanFolders, Is.True);
                h.QueueDiskScan(refresh);
                Assert.That(h.Commands.OfType<RescanFoldersCommand>().Single().AuthorIds, Is.EqualTo(new[] { h.Author.Id }));
                Assert.That(h.SearchTargets, Is.Empty);
                Assert.That(h.Author.AddOptions.AudiobookMonitor, Is.EqualTo(MonitorTypes.All));
                Assert.That(h.Author.AddOptions.EbookMonitor, Is.EqualTo(MonitorTypes.All));
                h.Scan();
                h.Scan();
                Assert.That(h.SearchTargets, Has.Count.EqualTo(1));
                Assert.That(h.SearchTargets.Single(), Has.Length.EqualTo(2));
                Assert.That(h.Author.AddOptions, Is.Null);
            });
        }

        private static AuthorResource NativeAuthorRequest(AuthorImportResource request) => new()
        {
            ForeignAuthorId = request.ForeignAuthorId, AuthorName = request.AuthorName,
            AudiobookRootFolderPath = request.AudiobookRootFolderPath, EbookRootFolderPath = request.EbookRootFolderPath,
            AudiobookQualityProfileId = request.AudiobookQualityProfileId, EbookQualityProfileId = request.EbookQualityProfileId,
            AudiobookMetadataProfileId = request.AudiobookMetadataProfileId, EbookMetadataProfileId = request.EbookMetadataProfileId,
            AudiobookMonitored = request.AudiobookMonitored, EbookMonitored = request.EbookMonitored,
            AudiobookMonitorExistingMode = request.AudiobookMonitorExistingMode, EbookMonitorExistingMode = request.EbookMonitorExistingMode,
            AudiobookMonitorNewItems = request.AudiobookMonitorNewItems, EbookMonitorNewItems = request.EbookMonitorNewItems,
            SyncMonitoredAcrossFormats = request.SyncMonitoredAcrossFormats,
            AddOptions = new AddAuthorOptions { SearchForMissingBooks = request.SearchForMissingBooks == true }
        };

        [Test]
        public void native_and_facade_add_queue_the_initial_scan_once_metadata_is_available(
            [Values(false, true)] bool postgres, [Values("audiobook", "ebook", "both")] string media,
            [Values(false, true)] bool initiallyUnavailable)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database) { Available = !initiallyUnavailable };
                var resource = NativeAuthorRequest(h.Request(media, media == "both"));
                resource.AddOptions.SearchForMissingBooks = true;
                if (media != "both")
                {
                    h.Controller.HttpContext.Items[Chaptarr.Http.Middleware.ReadarrFacadeContext.ItemKey] =
                        new Chaptarr.Http.Middleware.ReadarrFacadeContext("hc", media, "/readarr/hc/" + media);
                    resource.ForeignAuthorId = "123456";
                    resource.Monitored = true;
                }

                h.Controller.AddAuthor(resource).GetAwaiter().GetResult();
                if (initiallyUnavailable)
                {
                    Assert.That(h.Commands, Is.Empty);
                    Assert.That(h.Author, Is.Null);
                    h.Available = true;
                    h.ProcessPending();
                }

                var refresh = h.Commands.OfType<RefreshAuthorCommand>().Single();
                Assert.That(refresh.RefreshMetadata, Is.False);
                Assert.That(refresh.RescanFolders, Is.True);
                Assert.That(refresh.IsNewAuthor, Is.True);
                Assert.That(h.SearchTargets, Is.Empty);
                h.QueueDiskScan(refresh);
                Assert.That(h.Commands.OfType<RescanFoldersCommand>().Single().AuthorIds, Is.EqualTo(new[] { h.Author.Id }));
                h.Scan();
                h.Scan();
                Assert.That(h.SearchTargets, Has.Count.EqualTo(1));
                Assert.That(h.SearchTargets.Single(), Has.Length.EqualTo(media == "both" ? 2 : 1));
                Assert.That(h.Author.AddOptions, Is.Null);
            });
        }

        [Test]
        public void native_add_scan_keeps_exact_book_and_media_scope(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType requestedSide)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database) { CatalogWorkCount = 2 };
                var resource = NativeAuthorRequest(h.Request("both", false));
                resource.AudiobookMonitorExistingMode = requestedSide == BookMediaType.Audiobook ? MonitorTypes.SpecificBook : MonitorTypes.None;
                resource.EbookMonitorExistingMode = requestedSide == BookMediaType.Ebook ? MonitorTypes.SpecificBook : MonitorTypes.None;
                resource.AddOptions = new AddAuthorOptions
                {
                    Monitor = MonitorTypes.SpecificBook, MediaType = requestedSide,
                    BooksToMonitor = new List<string> { "hc:456789" }, SearchForMissingBooks = true
                };
                h.Controller.AddAuthor(resource).GetAwaiter().GetResult();
                h.QueueDiskScan(h.Commands.OfType<RefreshAuthorCommand>().Single());
                Assert.That(h.SearchTargets, Is.Empty);
                h.Scan();
                h.Scan();
                var selected = h.BookRows.GetBooksByAuthorId(h.Author.Id).Where(book => book.IsMonitored()).ToList();
                Assert.That(selected, Has.Count.EqualTo(1));
                Assert.That(selected.Single().MediaType, Is.EqualTo(requestedSide));
                Assert.That(selected.Single().HardcoverBookId, Is.EqualTo("hc:456789"));
                var search = h.Commands.OfType<BookSearchCommand>().Single();
                Assert.That(search.Trigger, Is.EqualTo(CommandTrigger.Unspecified));
                Assert.That(search.BookIds, Is.EqualTo(new[] { selected.Single().Id }));
                Assert.That(h.SearchTargets, Is.Empty, "An exact request must not schedule an author-wide missing search.");
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void native_add_scan_submission_failure_keeps_intent_for_deliberate_retry(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database) { RejectScan = true };
                var resource = NativeAuthorRequest(h.Request());
                resource.AddOptions.SearchForMissingBooks = true;
                Assert.ThrowsAsync<InvalidOperationException>(async () => await h.Controller.AddAuthor(resource));
                Assert.That(h.Author.AddOptions.SearchForMissingBooks, Is.True);
                Assert.That(h.SearchTargets, Is.Empty);
                Assert.That(h.Commands.OfType<RefreshAuthorCommand>(), Is.Empty);
                h.RejectScan = false;
                h.Controller.AddAuthor(resource).GetAwaiter().GetResult();
                Assert.That(h.MetadataFetches, Is.EqualTo(1), "Retry must not load a hydrated catalog again.");
                h.QueueDiskScan(h.Commands.OfType<RefreshAuthorCommand>().Single());
                h.Scan();
                h.Scan();
                Assert.That(h.SearchTargets, Has.Count.EqualTo(1));
                Assert.That(h.SearchTargets.Single(), Has.Length.EqualTo(2));
                Assert.That(h.Author.AddOptions, Is.Null);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void native_add_rejects_conflicting_generic_fallback_before_fetch(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var resource = new AuthorResource
                {
                    ForeignAuthorId = "hc:123456", AuthorName = "Sync Options Writer",
                    AudiobookRootFolderPath = AudioRootPath, EbookRootFolderPath = EbookRootPath,
                    SyncMonitoredAcrossFormats = true, AudiobookMonitorExistingMode = MonitorTypes.All,
                    AddOptions = new AddAuthorOptions { Monitor = MonitorTypes.None }
                };
                Assert.ThrowsAsync<ValidationException>(async () => await h.Controller.AddAuthor(resource));
                Assert.That(h.MetadataFetches, Is.Zero);
                Assert.That(h.Authors.GetAllAuthors(), Is.Empty);
                Assert.That(h.Pending.GetAll(), Is.Empty);
                Assert.That(h.Commands, Is.Empty);
            });
        }

        [TestCase(false, false, false)]
        [TestCase(true, false, false)]
        [TestCase(false, true, false)]
        [TestCase(true, true, false)]
        [TestCase(false, false, true)]
        [TestCase(true, false, true)]
        [TestCase(false, true, true)]
        [TestCase(true, true, true)]
        public void native_selected_book_request_preserves_scope_and_rejects_supplied_ongoing_conflicts(bool postgres, bool explicitConflict, bool populated)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                if (populated)
                {
                    h.Import(h.Request());
                    h.Scan();
                    h.Commands.Clear();
                }
                h.Available = false;
                var workId = populated ? "hc:999999" : "hc:456789";
                var ebookDefaults = h.EbookRoot.GetEbookSettings();
                ebookDefaults.MonitorNewItems = NewItemMonitorTypes.None;
                h.EbookRoot.SetEbookSettings(ebookDefaults);
                var resource = new AuthorResource
                {
                    ForeignAuthorId = "hc:123456", AuthorName = "Sync Options Writer",
                    AudiobookRootFolderPath = AudioRootPath, EbookRootFolderPath = EbookRootPath,
                    AddOptions = new AddAuthorOptions
                    {
                        Monitor = MonitorTypes.SpecificBook, MediaType = BookMediaType.Audiobook,
                        BooksToMonitor = new() { workId }
                    }
                };
                if (explicitConflict)
                {
                    resource.SyncMonitoredAcrossFormats = true;
                    resource.AudiobookMonitorNewItems = NewItemMonitorTypes.All;
                    resource.EbookMonitorNewItems = NewItemMonitorTypes.None;
                    Assert.ThrowsAsync<ValidationException>(async () => await h.Controller.AddAuthor(resource));
                    Assert.That(h.MetadataFetches, Is.EqualTo(populated ? 1 : 0));
                    Assert.That(h.Authors.GetAllAuthors().Count, Is.EqualTo(populated ? 1 : 0));
                    Assert.That(h.Pending.GetAll(), Is.Empty);
                    Assert.That(h.Commands, Is.Empty);
                    return;
                }
                var response = h.Controller.AddAuthor(resource).GetAwaiter().GetResult();
                Assert.That(response.Result, Is.TypeOf<AcceptedResult>());
                Assert.That(h.MetadataFetches, Is.EqualTo(populated ? 2 : 1), "A requested work missing from an existing side must still reach the provider and pending rescue path.");
                var pending = h.Pending.GetByProviderId("hc:123456");
                Assert.That(pending.IsAuthorSettingsRequest, Is.False);
                Assert.That(pending.AudiobookMonitorExistingMode, Is.EqualTo(MonitorTypes.SpecificBook));
                Assert.That(pending.EbookMonitorExistingMode, Is.EqualTo(MonitorTypes.None));
                Assert.That(pending.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.All));
                Assert.That(pending.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
                Assert.That(pending.AudiobookBooksToMonitor, Does.Contain(workId));
                Assert.That(pending.EbookBooksToMonitor, Is.Null);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void repeated_native_selected_book_requests_preserve_explicit_sync_and_media_scoped_ids(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database) { Available = false };
                var resource = new AuthorResource
                {
                    ForeignAuthorId = "hc:123456", AuthorName = "Sync Options Writer",
                    AudiobookRootFolderPath = AudioRootPath, EbookRootFolderPath = EbookRootPath,
                    SyncMonitoredAcrossFormats = false,
                    AddOptions = new AddAuthorOptions
                    {
                        Monitor = MonitorTypes.SpecificBook, MediaType = BookMediaType.Audiobook,
                        BooksToMonitor = new() { "hc:456789" }
                    }
                };
                h.Controller.AddAuthor(resource).GetAwaiter().GetResult();
                Assert.That(h.Pending.GetByProviderId("hc:123456").SyncMonitoredAcrossFormats, Is.False);
                resource.SyncMonitoredAcrossFormats = true;
                resource.AddOptions.BooksToMonitor = new() { "hc:999999" };
                h.Controller.AddAuthor(resource).GetAwaiter().GetResult();
                var pending = h.Pending.GetByProviderId("hc:123456");
                Assert.That(pending.SyncMonitoredAcrossFormats, Is.True);
                Assert.That(pending.IsAuthorSettingsRequest, Is.False);
                Assert.That(Newtonsoft.Json.JsonConvert.DeserializeObject<List<string>>(pending.AudiobookBooksToMonitor),
                    Is.EquivalentTo(new[] { "hc:456789", "hc:999999" }));
                Assert.That(pending.EbookBooksToMonitor, Is.Null);
                Assert.That(pending.AudiobookMonitorExistingMode, Is.EqualTo(MonitorTypes.SpecificBook));
                Assert.That(pending.EbookMonitorExistingMode, Is.EqualTo(MonitorTypes.None));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void existing_author_missing_catalog_failure_keeps_settings_and_returns_hydration_warning(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                h.Import(h.Request("audiobook", false));
                h.Scan();
                h.Available = false;
                var request = h.Request("ebook");
                var response = h.Controller.ImportAuthor(request).GetAwaiter().GetResult();
                var resource = (AuthorResource)((OkObjectResult)response.Result).Value;
                Assert.That(resource.HydrationWarning, Is.Not.Empty);
                Assert.That(h.Author.EbookRootFolderPath, Is.EqualTo(request.EbookRootFolderPath));
                Assert.That(h.Author.SyncMonitoredAcrossFormats, Is.True);
                Assert.That(h.BookRows.GetBooksByAuthorId(h.Author.Id).Any(book => book.MediaType == BookMediaType.Ebook), Is.False);
                Assert.That(h.Pending.GetAll(), Is.Empty);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void queued_search_survives_author_creation_by_another_route_before_replay(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database) { Available = false };
                var request = h.Request();
                request.SearchForMissingBooks = true;
                h.Import(request);
                var pending = h.Pending.GetByProviderId("hc:123456");
                h.Available = true;
                h.Library.AddAuthorAsync(request.ForeignAuthorId, new MonitoringConfig
                {
                    CreateAudiobook = true, CreateEbook = true,
                    AudiobookRootFolderPath = request.AudiobookRootFolderPath, EbookRootFolderPath = request.EbookRootFolderPath,
                    AudiobookQualityProfileId = request.AudiobookQualityProfileId, EbookQualityProfileId = request.EbookQualityProfileId,
                    AudiobookMetadataProfileId = request.AudiobookMetadataProfileId, EbookMetadataProfileId = request.EbookMetadataProfileId,
                    AudiobookMonitorExistingMode = MonitorTypes.All, EbookMonitorExistingMode = MonitorTypes.All,
                    AudiobookMonitored = true, EbookMonitored = true, SyncMonitoredAcrossFormats = true
                }).GetAwaiter().GetResult();
                h.Scan();
                h.Commands.Clear();
                h.ProcessPending();
                Assert.That(h.Author.AddOptions?.SearchForMissingBooks, Is.True);
                Assert.That(h.Commands.OfType<RefreshAuthorCommand>().Count(), Is.EqualTo(1));
                Assert.That(h.SearchTargets, Is.Empty);
                Assert.That(h.Pending.GetAll().Any(item => item.Id == pending.Id), Is.False);
                h.Scan();
                h.Scan();
                h.ProcessPending();
                Assert.That(h.SearchTargets, Has.Count.EqualTo(1));
                Assert.That(h.SearchTargets.Single(), Has.Length.EqualTo(2));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void queued_search_keeps_durable_options_when_scan_queue_rejects_handoff(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database) { Available = false };
                var request = h.Request();
                request.SearchForMissingBooks = true;
                h.Import(request);
                var pendingId = h.Pending.GetByProviderId("hc:123456").Id;
                h.Available = true;
                h.RejectScan = true;
                h.ProcessPending();
                Assert.That(h.Pending.Get(pendingId).OverallStatus, Is.EqualTo(PendingImportStatus.Retrying));
                Assert.That(h.Author.AddOptions.SearchForMissingBooks, Is.True);
                Assert.That(h.SearchTargets, Is.Empty);
                h.RejectScan = false;
                h.Pending.RetryNow(pendingId);
                h.ProcessPending();
                Assert.That(h.Commands.OfType<RefreshAuthorCommand>().Count(), Is.EqualTo(1));
                h.Scan();
                h.Scan();
                Assert.That(h.SearchTargets, Has.Count.EqualTo(1));
                Assert.That(h.SearchTargets.Single(), Has.Length.EqualTo(2));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void combined_add_completes_monitoring_before_one_search_and_keeps_independent_gates_profiles_tags(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var request = h.Request();
                request.SearchForMissingBooks = true;
                request.AudiobookTags = new HashSet<int> { 7 };
                request.EbookTags = new HashSet<int> { 8 };
                h.Import(request);
                Assert.Multiple(() =>
                {
                    Assert.That(h.MetadataFetches, Is.EqualTo(1));
                    Assert.That(h.Author.SyncMonitoredAcrossFormats, Is.True);
                    Assert.That(h.Author.LastSelectedMediaType, Is.EqualTo("ebook"));
                    Assert.That(h.SearchTargets, Is.Empty);
                    Assert.That(h.Author.AudiobookQualityProfileId, Is.EqualTo(3));
                    Assert.That(h.Author.EbookQualityProfileId, Is.EqualTo(4));
                    Assert.That(h.Author.AudiobookTags, Is.EquivalentTo(new[] { 7 }));
                    Assert.That(h.Author.EbookTags, Is.EquivalentTo(new[] { 8 }));
                });
                h.Scan();
                h.Scan();
                Assert.That(h.SearchTargets, Has.Count.EqualTo(1));
                Assert.That(h.SearchTargets.Single(), Has.Length.EqualTo(2));
                var edit = h.Author.ToResource();
                edit.AudiobookMonitored = false;
                edit.EbookMonitored = true;
                edit.SyncMonitoredAcrossFormats = null;
                h.Controller.UpdateAuthor(edit);
                Assert.That(h.Author.AudiobookMonitored, Is.False);
                Assert.That(h.Author.EbookMonitored, Is.True);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void explicit_false_overrides_root_true_and_preserves_independent_choices(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var request = h.Request(sync: false);
                request.EbookMonitorExistingMode = MonitorTypes.None;
                request.EbookMonitorNewItems = NewItemMonitorTypes.None;
                h.Import(request);
                h.Scan();
                Assert.Multiple(() =>
                {
                    Assert.That(h.Author.SyncMonitoredAcrossFormats, Is.False);
                    Assert.That(h.Author.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.All));
                    Assert.That(h.Author.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
                    Assert.That(h.BookRows.GetBooksByAuthorId(h.Author.Id).Single(book => book.MediaType == BookMediaType.Audiobook).IsMonitored(), Is.True);
                    Assert.That(h.BookRows.GetBooksByAuthorId(h.Author.Id).Single(book => book.MediaType == BookMediaType.Ebook).IsMonitored(), Is.False);
                });
            });
        }

        [TestCase(false, false, false)]
        [TestCase(true, false, false)]
        [TestCase(false, false, true)]
        [TestCase(true, false, true)]
        [TestCase(false, true, false)]
        [TestCase(true, true, false)]
        [TestCase(false, true, true)]
        [TestCase(true, true, true)]
        public void explicit_conflicts_fail_before_metadata_fetch_persistence_or_queueing(bool postgres, bool nativeAdd, bool ongoing)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database) { Available = false };
                var request = h.Request();
                if (ongoing) request.EbookMonitorNewItems = NewItemMonitorTypes.None;
                else request.EbookMonitorExistingMode = MonitorTypes.None;
                if (nativeAdd)
                {
                    var resource = new AuthorResource
                    {
                        ForeignAuthorId = request.ForeignAuthorId, AuthorName = request.AuthorName,
                        AudiobookRootFolderPath = request.AudiobookRootFolderPath, EbookRootFolderPath = request.EbookRootFolderPath,
                        AudiobookQualityProfileId = request.AudiobookQualityProfileId, EbookQualityProfileId = request.EbookQualityProfileId,
                        AudiobookMetadataProfileId = request.AudiobookMetadataProfileId, EbookMetadataProfileId = request.EbookMetadataProfileId,
                        AudiobookMonitorExistingMode = request.AudiobookMonitorExistingMode, EbookMonitorExistingMode = request.EbookMonitorExistingMode,
                        AudiobookMonitorNewItems = request.AudiobookMonitorNewItems, EbookMonitorNewItems = request.EbookMonitorNewItems,
                        SyncMonitoredAcrossFormats = true
                    };
                    Assert.ThrowsAsync<ValidationException>(async () => await h.Controller.AddAuthor(resource));
                }
                else Assert.Throws<ValidationException>(() => h.Import(request));
                Assert.Multiple(() =>
                {
                    Assert.That(h.MetadataFetches, Is.Zero);
                    Assert.That(h.Authors.GetAllAuthors(), Is.Empty);
                    Assert.That(h.Pending.GetAll(), Is.Empty);
                    Assert.That(h.Commands, Is.Empty);
                });
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void sole_side_cannot_enable_sync_and_explicit_unrequested_initial_conflicts_are_not_discarded(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                Assert.Throws<ValidationException>(() => h.Import(h.Request("audiobook")));
                h.Import(h.Request("audiobook", false));
                h.Scan();
                h.Commands.Clear();
                var request = h.Request("ebook");
                request.AudiobookMonitorExistingMode = MonitorTypes.All;
                request.EbookMonitorExistingMode = MonitorTypes.None;
                Assert.Throws<ValidationException>(() => h.Import(request));
                Assert.That(h.MetadataFetches, Is.EqualTo(1));
                Assert.That(h.Author.EbookRootFolderPath, Is.Null.Or.Empty);
                Assert.That(h.Commands, Is.Empty);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void one_explicit_shared_choice_overrides_conflicting_root_defaults_but_omitted_choices_require_resolution(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var request = h.Request();
                request.AudiobookMonitorExistingMode = null;
                request.EbookMonitorExistingMode = null;
                Assert.Throws<ValidationException>(() => h.Import(request));
                Assert.That(h.MetadataFetches, Is.Zero);
                request.AudiobookMonitorExistingMode = MonitorTypes.All;
                request.EbookMonitorNewItems = null;
                h.Import(request);
                Assert.That(h.Author.AddOptions.AudiobookMonitor, Is.EqualTo(MonitorTypes.All));
                Assert.That(h.Author.AddOptions.EbookMonitor, Is.EqualTo(MonitorTypes.All));
                Assert.That(h.BookRows.GetBooksByAuthorId(h.Author.Id).All(book => !book.IsMonitored()), Is.True,
                    "Resolved initial monitoring is deferred until the file scan.");
                h.Scan();
                Assert.That(h.BookRows.GetBooksByAuthorId(h.Author.Id).All(book => book.IsMonitored()), Is.True);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void adding_missing_side_to_settled_author_retains_initialization_and_does_not_unmonitor_other_side(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                h.Import(h.Request("audiobook", false));
                h.Scan();
                var request = h.Request("ebook", true);
                request.EbookMonitorExistingMode = MonitorTypes.None;
                request.SearchForMissingBooks = true;
                h.Import(request);
                Assert.Multiple(() =>
                {
                    Assert.That(h.Author.SyncMonitoredAcrossFormats, Is.True);
                    Assert.That(h.Author.AddOptions.EbookMonitor, Is.EqualTo(MonitorTypes.None));
                    Assert.That(h.SearchTargets, Is.Empty);
                });
                h.Scan();
                Assert.That(h.BookRows.GetBooksByAuthorId(h.Author.Id).All(book => book.IsMonitored()), Is.True);
                Assert.That(h.SearchTargets, Has.Count.EqualTo(1));
                Assert.That(h.SearchTargets.Single(), Is.EqualTo(h.BookRows.GetBooksByAuthorId(h.Author.Id)
                    .Where(book => book.MediaType == BookMediaType.Ebook).Select(book => book.Id).ToArray()));
            });
        }

        private static AuthorResource NativeAuthorUpdate(Author author)
        {
            var resource = author.ToResource();
            resource.AudiobookMonitorExisting = null;
            resource.AudiobookMonitorFuture = null;
            resource.EbookMonitorExisting = null;
            resource.EbookMonitorFuture = null;
            return resource;
        }

        [TestCase(false)]
        [TestCase(true)]
        public void partial_ongoing_policy_edit_links_both_sides_without_changing_book_selection_and_conflicts_are_atomic(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                h.Import(h.Request());
                h.Scan();
                h.Commands.Clear();
                var before = h.BookRows.GetBooksByAuthorId(h.Author.Id).Select(book => book.IsMonitored()).ToArray();
                var resource = NativeAuthorUpdate(h.Author);
                resource.SyncMonitoredAcrossFormats = null;
                resource.AudiobookMonitorNewItems = NewItemMonitorTypes.New;
                resource.EbookMonitorNewItems = null;
                h.Controller.UpdateAuthor(resource);
                Assert.That(h.Author.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.New));
                Assert.That(h.Author.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.New));
                Assert.That(h.BookRows.GetBooksByAuthorId(h.Author.Id).Select(book => book.IsMonitored()), Is.EqualTo(before));
                Assert.That(h.Commands, Is.Empty);
                resource = NativeAuthorUpdate(h.Author);
                resource.AudiobookMonitorNewItems = NewItemMonitorTypes.All;
                resource.EbookMonitorNewItems = NewItemMonitorTypes.None;
                Assert.Throws<ValidationException>(() => h.Controller.UpdateAuthor(resource, moveFiles: true));
                Assert.That(h.Commands, Is.Empty);
                Assert.That(h.Author.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.New));
                Assert.That(h.Author.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.New));
                var editor = new AuthorEditorController(h.Authors, h.Queue, h.Roots, null, null, null);
                Assert.Throws<ValidationException>(() => editor.SaveAll(new AuthorEditorResource { AuthorIds = new() { h.Author.Id },
                    AudiobookMonitorNewItems = NewItemMonitorTypes.All, EbookMonitorNewItems = NewItemMonitorTypes.None,
                    AudiobookRootFolderPath = "", EbookRootFolderPath = "", MoveFiles = true }));
                Assert.That(h.Commands, Is.Empty);
                Assert.That(h.Author.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.New));
            });
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void unrelated_partial_edit_preserves_legacy_mismatched_policies_and_explicit_false(bool postgres, bool echoSync)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                h.Import(h.Request());
                h.Scan();
                var author = h.Author;
                author.EbookMonitorNewItems = NewItemMonitorTypes.None;
                h.Authors.UpdateAuthor(author);
                var resource = NativeAuthorUpdate(h.Author);
                resource.SyncMonitoredAcrossFormats = echoSync ? true : null;
                resource.AudiobookMonitorNewItems = null;
                resource.EbookMonitorNewItems = null;
                h.Controller.UpdateAuthor(resource);
                Assert.That(h.Author.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.All));
                Assert.That(h.Author.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
                resource.SyncMonitoredAcrossFormats = false;
                h.Controller.UpdateAuthor(resource);
                Assert.That(h.Author.SyncMonitoredAcrossFormats, Is.False);
                Assert.That(h.Author.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void first_enable_requires_explicit_shared_policy_when_saved_choices_disagree(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var request = h.Request(sync: false);
                request.EbookMonitorNewItems = NewItemMonitorTypes.None;
                h.Import(request);
                h.Scan();
                h.Commands.Clear();
                var resource = NativeAuthorUpdate(h.Author);
                resource.SyncMonitoredAcrossFormats = true;
                resource.AudiobookMonitorNewItems = null;
                resource.EbookMonitorNewItems = null;
                Assert.Throws<ValidationException>(() => h.Controller.UpdateAuthor(resource));
                Assert.That(h.Author.SyncMonitoredAcrossFormats, Is.False);
                Assert.That(h.Commands, Is.Empty);
                resource.EbookMonitorNewItems = NewItemMonitorTypes.New;
                h.Controller.UpdateAuthor(resource);
                Assert.That(h.Author.SyncMonitoredAcrossFormats, Is.True);
                Assert.That(h.Author.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.New));
                Assert.That(h.Author.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.New));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void queued_author_settings_survive_list_merge_and_real_retry_search_waits_for_scan_and_sync(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database) { Available = false };
                var request = h.Request();
                request.SearchForMissingBooks = true;
                request.AudiobookMonitorNewItems = NewItemMonitorTypes.New;
                request.EbookMonitorNewItems = NewItemMonitorTypes.New;
                h.Import(request);
                var pending = h.Pending.GetByProviderId("hc:123456");
                Assert.That(pending.SyncMonitoredAcrossFormats, Is.True);
                Assert.That(pending.IsAuthorSettingsRequest, Is.True);
                h.Pending.EnqueueAsync("hc:123456", new MonitoringConfig
                {
                    RequestedBy = "ImportList:Fixture", CreateAudiobook = true, CreateEbook = true,
                    AudiobookMonitorExistingMode = MonitorTypes.None, EbookMonitorExistingMode = MonitorTypes.None,
                    AudiobookMonitorNewItems = NewItemMonitorTypes.All, EbookMonitorNewItems = NewItemMonitorTypes.All
                }, "ImportListSync").GetAwaiter().GetResult();
                pending = h.Pending.GetByProviderId("hc:123456");
                Assert.That(pending.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.New));
                Assert.That(pending.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.New));
                h.Available = true;
                h.ProcessPending();
                Assert.Multiple(() =>
                {
                    Assert.That(h.Author, Is.Not.Null);
                    Assert.That(h.Author.SyncMonitoredAcrossFormats, Is.True);
                    Assert.That(h.Author.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.New));
                    Assert.That(h.Author.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.New));
                    Assert.That(h.SearchTargets, Is.Empty);
                    Assert.That(h.Commands.OfType<RefreshAuthorCommand>().Count(), Is.EqualTo(1));
                });
                h.QueueDiskScan(h.Commands.OfType<RefreshAuthorCommand>().Single());
                Assert.That(h.Commands.OfType<RescanFoldersCommand>().Count(), Is.EqualTo(1));
                h.Scan();
                h.Scan();
                h.ProcessPending();
                Assert.That(h.SearchTargets, Has.Count.EqualTo(1));
                Assert.That(h.SearchTargets.Single(), Has.Length.EqualTo(2));
            });
        }
        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void automatic_other_side_demand_does_not_reinterpret_author_setup_as_conflicting_api_input_on_retry(bool postgres, bool listFirst)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database) { Available = false };
                void AddList() => h.Pending.EnqueueAsync("hc:123456", new MonitoringConfig
                {
                    RequestedBy = "ImportList:Fixture", CreateAudiobook = false, CreateEbook = true,
                    EbookRootFolderPath = EbookRootPath, EbookQualityProfileId = 4, EbookMetadataProfileId = 3,
                    EbookMonitored = true, EbookMonitorExistingMode = MonitorTypes.None, EbookMonitorNewItems = NewItemMonitorTypes.None
                }, "ImportListSync").GetAwaiter().GetResult();
                if (listFirst) AddList();
                h.Import(h.Request("audiobook", null));
                if (!listFirst) AddList();
                h.Available = true;
                h.ProcessPending();
                Assert.That(h.Author, Is.Not.Null, string.Join("; ", h.Pending.GetAll().Select(item => item.LastError)));
                Assert.That(h.BookRows.GetBooksByAuthorId(h.Author.Id).Select(book => book.MediaType),
                    Is.EquivalentTo(new[] { BookMediaType.Audiobook, BookMediaType.Ebook }));
                Assert.That(h.Pending.GetAll().Any(item => item.OverallStatus is PendingImportStatus.Pending or PendingImportStatus.Retrying), Is.False);
            });
        }

        [TestCase(false, "audiobook", "both")]
        [TestCase(true, "audiobook", "both")]
        [TestCase(false, "ebook", "both")]
        [TestCase(true, "ebook", "both")]
        [TestCase(false, "audiobook", "ebook")]
        [TestCase(true, "ebook", "audiobook")]
        public void media_scoped_facade_cannot_add_both_or_opposing_media(bool postgres, string facade, string requested)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                h.Controller.HttpContext.Items[Chaptarr.Http.Middleware.ReadarrFacadeContext.ItemKey] =
                    new Chaptarr.Http.Middleware.ReadarrFacadeContext("hc", facade, "/hc/" + facade);
                Assert.That(() => h.Import(h.Request(requested, false)), Throws.Exception);
                Assert.That(h.MetadataFetches, Is.Zero);
                Assert.That(h.Authors.GetAllAuthors(), Is.Empty);
                Assert.That(h.Pending.GetAll(), Is.Empty);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void settings_only_import_preserves_omitted_initial_policies_on_settled_sides(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                h.Import(h.Request());
                h.Scan();
                var saved = h.Author;
                saved.EbookMonitorNewItems = NewItemMonitorTypes.None;
                saved.AudiobookMonitored = false;
                h.Authors.UpdateAuthor(saved);
                var request = h.Request();
                request.SyncMonitoredAcrossFormats = null;
                request.AudiobookMonitorExistingMode = null;
                request.EbookMonitorExistingMode = null;
                request.AudiobookMonitorNewItems = null;
                request.EbookMonitorNewItems = null;
                request.AudiobookMonitored = null;
                request.EbookMonitored = null;
                h.Import(request);
                Assert.That(h.MetadataFetches, Is.EqualTo(1));
                Assert.That(h.Author.AudiobookMonitored, Is.False);
                Assert.That(h.Author.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.All));
                Assert.That(h.Author.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
                Assert.That(h.Author.AddOptions, Is.Null);
                Assert.That(h.BookRows.GetBooksByAuthorId(h.Author.Id).All(book => book.IsMonitored()), Is.True);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void repeated_synced_author_demand_keeps_existing_widening_and_a_shared_initial_choice(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database) { Available = false };
                h.Import(h.Request());
                var request = h.Request("ebook");
                request.EbookMonitorExistingMode = MonitorTypes.None;
                h.Import(request);
                var pending = h.Pending.GetByProviderId("hc:123456");
                Assert.That(pending.AudiobookMonitorExistingMode, Is.EqualTo(MonitorTypes.All));
                Assert.That(pending.EbookMonitorExistingMode, Is.EqualTo(MonitorTypes.All));
                h.Available = true;
                h.ProcessPending();
                h.Scan();
                Assert.That(h.BookRows.GetBooksByAuthorId(h.Author.Id).All(book => book.IsMonitored()), Is.True);
            });
        }

    }
}
