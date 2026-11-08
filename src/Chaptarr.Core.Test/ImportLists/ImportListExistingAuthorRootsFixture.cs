using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test.Books;
using Chaptarr.Core.Test.Common;
using Newtonsoft.Json;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.RootFolders;
using static Chaptarr.Core.Test.Common.InterfaceProxy;
using static Chaptarr.Core.Test.Books.AuthorLibraryServiceSpecificBookMonitoringFixture;

namespace Chaptarr.Core.Test.ImportLists
{
    [TestFixture]
    [NonParallelizable]
    public class ImportListExistingAuthorRootsFixture
    {
        [TestCase(false, false, 0)]
        [TestCase(true, false, 0)]
        [TestCase(false, true, 0)]
        [TestCase(true, true, 0)]
        [TestCase(false, false, 1)]
        [TestCase(true, false, 1)]
        [TestCase(false, true, 1)]
        [TestCase(true, true, 1)]
        [TestCase(false, false, 2)]
        [TestCase(true, false, 2)]
        [TestCase(false, true, 2)]
        [TestCase(true, true, 2)]
        [TestCase(false, false, 3)]
        [TestCase(true, false, 3)]
        [TestCase(false, true, 3)]
        [TestCase(true, true, 3)]
        [TestCase(false, false, 3, false)]
        [TestCase(true, false, 3, false)]
        [TestCase(false, false, 3, false, true)]
        [TestCase(true, false, 3, false, true)]
        [TestCase(false, false, 3, true, false, false)]
        [TestCase(true, false, 3, true, false, false)]
        [TestCase(false, false, 3, false, true, false)]
        [TestCase(true, false, 3, false, true, false)]
        public void list_format_initialization_should_inherit_only_unset_policies_and_preserve_explicit_none(bool postgres, bool newAuthor, int optionsKind,
            bool bothSides = true, bool genericSource = false, bool keep = true)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var logger = LogManager.GetCurrentClassLogger();
                var events = Proxy<IEventAggregator>((method, args) => null);
                var authors = new AuthorRepository(database, events, logger);
                var author = new Author
                {
                    Name = "Root Policy Author", CleanName = "rootpolicyauthor", SortName = "rootpolicyauthor",
                    SortNameLastFirst = "Author, Root Policy", TitleSlug = "root-policy-author",
                    Path = @"C:\Existing\Root Policy Author".AsOsAgnostic(), GoodreadsAuthorId = "gr:562254", Added = DateTime.UtcNow
                };
                if (!newAuthor) authors.Insert(author);
                var localAuthors = authors.All();
                var authorService = Proxy<IAuthorService>((method, args) => method.Name switch
                {
                    nameof(IAuthorService.GetAllAuthors) => localAuthors,
                    nameof(IAuthorService.FindByProviderId) => localAuthors.FirstOrDefault(),
                    _ => throw new AssertionException(method.Name)
                });
                var editions = new EditionService(new EditionRepository(database, events), events, logger);
                var books = new BookService(new BookRepository(database, events), editions, events, authorService,
                    Proxy<IMediaFileService>((method, args) => new List<BookFile>()), null,
                    new SeriesBookLinkRepository(database, events), null, logger);
                var audiobookRoot = BuildAudiobookRoot(@"C:\Audio".AsOsAgnostic());
                var ebookRoot = BuildEbookRoot(@"C:\Ebooks".AsOsAgnostic());
                var explicitNone = optionsKind >= 2;
                audiobookRoot.SetAudiobookSettings(new MediaTypeSettings
                {
                    QualityProfileId = 3, MetadataProfileId = 4, Monitored = explicitNone,
                    MonitorExistingMode = explicitNone ? MonitorTypes.All : MonitorTypes.Missing,
                    MonitorNewItems = explicitNone ? NewItemMonitorTypes.All : NewItemMonitorTypes.New
                });
                ebookRoot.SetEbookSettings(new MediaTypeSettings
                {
                    QualityProfileId = 5, MetadataProfileId = 6, Monitored = explicitNone,
                    MonitorExistingMode = explicitNone ? MonitorTypes.All : MonitorTypes.Existing,
                    MonitorNewItems = explicitNone ? NewItemMonitorTypes.All : NewItemMonitorTypes.New
                });
                var roots = new StubRootFolderService(audiobookRoot, ebookRoot);
                var pending = new PendingAuthorImportService(new PendingAuthorImportRepository(database, events), authorService, events, logger);
                var enqueued = new List<MonitoringConfig>();
                var ingressPending = Proxy<IPendingAuthorImportService>((method, args) =>
                {
                    if (method.Name == nameof(IPendingAuthorImportService.EnqueueAsync)) enqueued.Add((MonitoringConfig)args[1]);
                    return method.Invoke(pending, args);
                });
                var queue = Proxy<IManageCommandQueue>((method, args) => new CommandModel { Body = (Command)args[0] });
                ImportListSyncServiceSearchOnAddFixture.BuildMissingMediaSideService(author,
                    CatalogBook(BookMediaType.Ebook, "Requested Root Policy Work", "123"), BookMediaType.Audiobook, true,
                    authorService, books, editions, ingressPending, roots, queue, bothSides: bothSides, newAuthor: newAuthor, keep: keep,
                    configureList: list =>
                    {
                        list.MonitorNewItems = NewItemMonitorTypes.All;
                        if (optionsKind == 3) list.ShouldMonitor = ImportListMonitorType.None;
                        if (genericSource)
                        {
                            list.Settings = new NzbDrone.Core.ImportLists.LazyLibrarianImport.LazyLibrarianImportSettings
                            {
                                MonitorAudiobooks = true, MonitorEbooks = bothSides,
                                AudiobookRootFolderPath = audiobookRoot.Path, EbookRootFolderPath = bothSides ? ebookRoot.Path : null,
                                AudiobookQualityProfileId = 3, AudiobookMetadataProfileId = 4,
                                EbookQualityProfileId = bothSides ? 5 : 0, EbookMetadataProfileId = bothSides ? 6 : 0
                            };
                            list.RootFolderPath = audiobookRoot.Path;
                            list.QualityProfileId = 3;
                            list.MetadataProfileId = 4;
                        }
                        list.MonitoringOptions = optionsKind == 0 ? null : new ImportListMonitoringOptions();
                        if (explicitNone)
                        {
                            list.MonitoringOptions.AudiobookMonitored = false;
                            list.MonitoringOptions.EbookMonitored = false;
                            list.MonitoringOptions.AudiobookMonitor = MonitorTypes.None;
                            list.MonitoringOptions.EbookMonitor = MonitorTypes.None;
                            list.MonitoringOptions.AudiobookMonitorNewItems = NewItemMonitorTypes.None;
                            list.MonitoringOptions.EbookMonitorNewItems = NewItemMonitorTypes.None;
                        }
                    }).Execute(new ImportListSyncCommand(1));
                var saved = pending.GetByProviderId(author.GoodreadsAuthorId);
                Assert.That(saved, Is.Not.Null, "The actual list ingress must persist its format initialization request.");
                var listedTargets = optionsKind != 3;
                Assert.Multiple(() =>
                {
                    Assert.That(enqueued, Has.Count.EqualTo(1));
                    Assert.That(saved.HasAudiobook(), Is.True);
                    Assert.That(saved.HasEbook(), Is.EqualTo(bothSides));
                    Assert.That(saved.AudiobookRootFolderPath, Is.EqualTo(audiobookRoot.Path));
                    Assert.That(saved.EbookRootFolderPath, Is.EqualTo(bothSides ? ebookRoot.Path : null));
                    Assert.That(saved.AudiobookQualityProfileId, Is.EqualTo(3));
                    Assert.That(saved.AudiobookMetadataProfileId, Is.EqualTo(4));
                    Assert.That(saved.EbookQualityProfileId, Is.EqualTo(bothSides ? (int?)5 : null));
                    Assert.That(saved.EbookMetadataProfileId, Is.EqualTo(bothSides ? (int?)6 : null));
                    Assert.That(saved.AudiobookMonitored, Is.False);
                    Assert.That(saved.EbookMonitored, Is.EqualTo(bothSides ? (bool?)false : null));
                    Assert.That(saved.AudiobookMonitorExistingMode, Is.EqualTo(explicitNone ? MonitorTypes.None : MonitorTypes.Missing));
                    Assert.That(saved.EbookMonitorExistingMode, Is.EqualTo(bothSides ? (MonitorTypes?)(explicitNone ? MonitorTypes.None : MonitorTypes.Existing) : null));
                    Assert.That(saved.AudiobookMonitorNewItems, Is.EqualTo(explicitNone ? NewItemMonitorTypes.None : NewItemMonitorTypes.New));
                    Assert.That(saved.EbookMonitorNewItems, Is.EqualTo(bothSides ? (NewItemMonitorTypes?)(explicitNone ? NewItemMonitorTypes.None : NewItemMonitorTypes.New) : null));
                    Assert.That(saved.AudiobookBooksToMonitor, listedTargets ? Does.Contain("gr:123") : Is.Null);
                    Assert.That(saved.EbookBooksToMonitor, bothSides && listedTargets ? Does.Contain("gr:123") : Is.Null);
                    Assert.That(enqueued.Single().EbookBooksToMonitor, bothSides && listedTargets ? Is.Not.Null : Is.Null);
                    Assert.That(newAuthor || localAuthors.All(local => local.AudiobookMonitored == null && local.EbookMonitored == null), Is.True);
                });
            });
        }

        [TestCase(false, false, 1)]
        [TestCase(true, false, 1)]
        [TestCase(false, false, 100)]
        [TestCase(true, false, 100)]
        [TestCase(false, true, 1)]
        [TestCase(true, true, 1)]
        [TestCase(false, true, 1, 1)]
        [TestCase(true, true, 1, 1)]
        [TestCase(false, true, 1, 2)]
        [TestCase(true, true, 1, 2)]
        [TestCase(false, true, 1, 3, true)]
        [TestCase(true, true, 1, 3, true)]
        [TestCase(false, true, 1, 0, false, true)]
        [TestCase(true, true, 1, 0, false, true)]
        public void actual_list_both_missing_sides_should_apply_each_requested_root(bool postgres, bool newAuthor, int unlistedCount, int emptyCatalogReads = 0, bool consumeHeldSearchBeforeRetry = false, bool scanBeforePendingResolution = false)
        {
            const BookMediaType side = BookMediaType.Audiobook;
            const bool monitored = true;
            DualDatabaseFixture.Run(postgres, database =>
            {
                var validationLogs = new NLog.Targets.MemoryTarget { Layout = "${message} ${exception:format=tostring}" };
                var logConfig = new NLog.Config.LoggingConfiguration();
                logConfig.AddRule(LogLevel.Warn, LogLevel.Fatal, validationLogs);
                using var logFactory = new LogFactory { Configuration = logConfig };
                var logger = logFactory.GetLogger("ImportListRoots");
                var progressiveCalls = new List<string>();
                var progressiveWrites = 0;
                var applyingProgressiveSettings = false;
                Action<Author> completeInitialScan = null;
                var initialScanTargets = new List<int>();
                var events = Proxy<IEventAggregator>((method, args) =>
                {
                    if (applyingProgressiveSettings && args[0] is AuthorEditedEvent) progressiveWrites++;
                    if (scanBeforePendingResolution && args[0] is AuthorRefreshCompleteEvent refreshed)
                        completeInitialScan?.Invoke(refreshed.Author);
                    return null;
                });
                var commands = new List<Command>();
                var queue = Proxy<IManageCommandQueue>((method, args) =>
                {
                    ((Command)args[0]).Trigger = (CommandTrigger)args[2];
                    commands.Add((Command)args[0]);
                    return new CommandModel { Id = commands.Count, Body = (Command)args[0] };
                });
                var authors = new AuthorRepository(database, events, logger);
                var bookRows = new BookRepository(database, events);
                var editionRows = new EditionRepository(database, events);
                var aliases = new ProviderAliasService(new ProviderAliasRepository(database, events));
                var editions = new EditionService(editionRows, events, logger);
                var roots = new StubRootFolderService(BuildAudiobookRoot(@"C:\Audio".AsOsAgnostic()), BuildEbookRoot(@"C:\Ebooks".AsOsAgnostic()));
                var paths = new StubAuthorPathBuilder();
                IBookService books = null;
                var authorService = new AuthorService(authors, events, paths, roots, queue, new CacheManager(), bookRows,
                    null, logger, null, null, new Lazy<IBookService>(() => books), mainDatabase: database, aliases);
                books = new BookService(bookRows, editions, events, authorService,
                    Proxy<IMediaFileService>((method, args) => new List<BookFile>()), null,
                    new SeriesBookLinkRepository(database, events), null, logger, providerAliasService: aliases);
                var author = new Author
                {
                    Name = "Existing Author", CleanName = "existingauthor", SortName = "existingauthor",
                    SortNameLastFirst = "Author, Existing", TitleSlug = "existing-author", Path = @"C:\Existing\Existing Author".AsOsAgnostic(),
                    Added = DateTime.UtcNow, GoodreadsAuthorId = "gr:562254"
                };
                if (consumeHeldSearchBeforeRetry && !newAuthor)
                {
                    author.AudiobookMonitored = true;
                    author.EbookMonitored = true;
                    author.AddOptions = new AddAuthorOptions { SearchForMissingBooks = true };
                }
                if (!newAuthor) authors.Insert(author);
                var otherSide = side == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook;
                var other = CatalogBook(otherSide, "Selected Work", "123");
                var requests = new PendingAuthorImportRepository(database, events);
                var pending = new PendingAuthorImportService(requests, authorService, events, logger, aliases);
                var ingressMetadataReads = 0;
                ImportListSyncServiceSearchOnAddFixture.BuildMissingMediaSideService(author, other, side, monitored,
                    authorService, books, editions, pending, roots, queue, bothSides: true, newAuthor: newAuthor,
                    metadataLookup: (id, media) =>
                    {
                        System.Threading.Interlocked.Increment(ref ingressMetadataReads);
                        Assert.That(id, Is.EqualTo("gr:123"));
                        Assert.That(media, Is.EqualTo(side));
                    }).Execute(new NzbDrone.Core.ImportLists.ImportListSyncCommand(1));
                Assert.That(ingressMetadataReads, Is.EqualTo(1));

                var storedRequest = pending.GetByProviderId(author.GoodreadsAuthorId);
                Assert.That(storedRequest, Is.Not.Null, "The list must persist the selected media-side request.");
                Assert.That(storedRequest.HasAudiobook(), Is.True);
                Assert.That(storedRequest.HasEbook(), Is.True);
                if (scanBeforePendingResolution)
                {
                    storedRequest.AudiobookMonitorExistingMode = MonitorTypes.First;
                    storedRequest.EbookMonitorExistingMode = MonitorTypes.First;
                }
                if (consumeHeldSearchBeforeRetry && !newAuthor)
                {
                    storedRequest.SearchForMissingBooks = true;
                    storedRequest.AudiobookBooksToSearch = null;
                    storedRequest.EbookBooksToSearch = null;
                }
                storedRequest.NextAttemptAt = DateTime.UtcNow.AddMinutes(-1);
                requests.Update(storedRequest);
                var catalog = new List<Book> { CatalogBook(side, "Selected Work", "123"), CatalogBook(otherSide, "Selected Work", "123") };
                catalog.AddRange(Enumerable.Range(0, unlistedCount).Select(index => CatalogBook(side, "Other Work " + index, (124 + index).ToString())));
                if (scanBeforePendingResolution)
                {
                    foreach (var selected in catalog.Where(book => book.GoodreadsWorkId == "gr:123"))
                    {
                        selected.ReleaseDate = DateTime.UtcNow.AddDays(-3);
                        foreach (var edition in selected.Editions) edition.ReleaseDate = selected.ReleaseDate;
                    }
                }
                var remote = new Author
                {
                    Name = author.Name, GoodreadsAuthorId = author.GoodreadsAuthorId,
                    Books = catalog,
                    Series = new List<Series>()
                };
                var profiles = new StubMetadataProfileService();
                var authorWriter = Proxy<IAuthorService>((method, args) =>
                {
                    var progressive = method.Name == nameof(IAuthorService.UpdateAuthorProgressiveSettings);
                    if (progressive) progressiveCalls.Add((string)args[9]);
                    applyingProgressiveSettings = progressive;
                    try { return method.Invoke(authorService, args); }
                    finally { applyingProgressiveSettings = false; }
                });
                var authorReads = 0;
                var workSignals = 0;
                var authorInfo = Proxy<IProvideAuthorInfo>((method, args) =>
                {
                    Assert.That(method.Name, Is.EqualTo(nameof(IProvideAuthorInfo.GetAuthorInfo)));
                    authorReads++;
                    return authorReads <= emptyCatalogReads ? new Author
                    {
                        Name = author.Name, GoodreadsAuthorId = author.GoodreadsAuthorId,
                        Books = new List<Book>(), Series = new List<Series>()
                    } : remote;
                });
                var library = new AuthorLibraryService(authorWriter, authorInfo, books, null, editions,
                    null, profiles, new TestQualityProfileService(), paths, roots, queue, events, pending, database,
                    new ImportListExclusionService(new ImportListExclusionRepository(database, events), logger),
                    new EditionMetadataProfileFilter(new TestTermMatcherService()), null, logger, new EditionSelector(logger));
                var workInfo = Proxy<IProvideBookInfo>((method, args) =>
                {
                    if (emptyCatalogReads == 0) throw new AssertionException("A complete author blob must not require a direct work rescue.");
                    Assert.That(method.Name, Is.EqualTo(nameof(IProvideBookInfo.GetWorkInfo)));
                    workSignals++;
                    if (emptyCatalogReads > 2 && authorReads == 3) throw new BookNotFoundException((string)args[0]);
                    return Tuple.Create(remote.GoodreadsAuthorId, CatalogBook((BookMediaType)args[1], "Selected Work", "123"), new List<Author> { remote });
                });
                completeInitialScan = scannedAuthor =>
                {
                    new AuthorScannedHandler(new BookMonitoredService(authorService, books, logger), authorService, queue,
                        new BookAddedService(new CacheManager(), queue, books, logger), events, logger, books)
                        .Handle(new AuthorScanSkippedEvent(scannedAuthor, AuthorScanSkippedReason.NoFolderEvidence));
                    initialScanTargets.AddRange(bookRows.GetMissingBookSearchTargets(null, scannedAuthor.Id).Select(target => target.BookId));
                };
                new ProcessPendingImportsCommandHandler(pending, library, books, workInfo,
                    queue, events, logger, aliases).Execute(new ProcessPendingImportsCommand());
                var retainedSearch = true;
                if (emptyCatalogReads > 1)
                {
                    var retained = pending.GetByProviderId(author.GoodreadsAuthorId);
                    Assert.That(retained, Is.Not.Null);
                    if (emptyCatalogReads > 2)
                    {
                        retained.NextAttemptAt = DateTime.UtcNow.AddMinutes(-1);
                        requests.Update(retained);
                        var notReadyPending = new PendingAuthorImportService(requests, authorService, events, logger, aliases);
                        new ProcessPendingImportsCommandHandler(notReadyPending, library, books, workInfo,
                            queue, events, logger, aliases).Execute(new ProcessPendingImportsCommand());
                        retained = notReadyPending.GetByProviderId(author.GoodreadsAuthorId);
                        Assert.That(retained, Is.Not.Null);
                    }
                    var audiobookSearch = JsonConvert.DeserializeObject<List<string>>(retained.AudiobookBooksToSearch ?? "[]") ?? new List<string>();
                    var ebookSearch = JsonConvert.DeserializeObject<List<string>>(retained.EbookBooksToSearch ?? "[]") ?? new List<string>();
                    retainedSearch = retained.SearchForMissingBooks ||
                        (audiobookSearch.Contains("gr:123") && ebookSearch.Contains("gr:123"));
                    TestContext.WriteLine($"Pending search intent: aggregate={retained.SearchForMissingBooks}, " +
                        $"Audiobook=[{string.Join(",", audiobookSearch)}], Ebook=[{string.Join(",", ebookSearch)}]");
                    if (consumeHeldSearchBeforeRetry)
                    {
                        var heldAuthor = authorService.FindByProviderId("gr", author.GoodreadsAuthorId);
                        Assert.That(bookRows.GetMissingBookSearchTargets(null, heldAuthor.Id), Is.Empty);
                        new AuthorScannedHandler(new BookMonitoredService(authorService, books, logger), authorService, queue,
                            new BookAddedService(new CacheManager(), queue, books, logger), events, logger, books).Handle(new AuthorScannedEvent(heldAuthor));
                        Assert.That(authors.Get(heldAuthor.Id).AddOptions, Is.Null);
                    }
                    Assert.That(commands.OfType<BookSearchCommand>(), Is.Empty);
                    retained.NextAttemptAt = DateTime.UtcNow.AddMinutes(-1);
                    requests.Update(retained);
                    var restartedPending = new PendingAuthorImportService(requests, authorService, events, logger, aliases);
                    new ProcessPendingImportsCommandHandler(restartedPending, library, books, workInfo,
                        queue, events, logger, aliases).Execute(new ProcessPendingImportsCommand());
                }

                var remaining = pending.GetByProviderId(author.GoodreadsAuthorId);
                if (remaining != null)
                {
                    foreach (var validationLog in validationLogs.Logs) TestContext.WriteLine(validationLog);
                }
                var savedAuthor = authorService.FindByProviderId("gr", author.GoodreadsAuthorId);
                var savedBooks = books.GetBooksByAuthor(savedAuthor.Id);
                var progressivePasses = newAuthor ? emptyCatalogReads : emptyCatalogReads + 1;
                Assert.Multiple(() =>
                {
                    Assert.That(initialScanTargets.Count, Is.EqualTo(scanBeforePendingResolution ? 2 : 0), "The completed initial scan must already cover the listed rows before pending resolution.");
                    Assert.That(retainedSearch, Is.True, "Unresolved requests must keep their search intent if a scan consumes the existing author search.");
                    Assert.That(authorReads, Is.EqualTo(emptyCatalogReads + 1));
                    Assert.That(workSignals, Is.EqualTo(emptyCatalogReads == 0 ? 0 : emptyCatalogReads > 2 ? 4 : 2));
                    Assert.That(remaining, Is.Null, remaining?.LastError);
                    Assert.That(savedBooks, Has.Count.EqualTo(2 + unlistedCount));
                    Assert.That(savedBooks.Where(book => book.GoodreadsWorkId == "gr:123").All(book => book.IsMonitored()), Is.True);
                    Assert.That(savedBooks.Single(book => book.GoodreadsWorkId == "gr:124").IsMonitored(), Is.False);
                    Assert.That(progressiveCalls, Is.EqualTo(Enumerable.Range(0, progressivePasses).SelectMany(_ => new[] { @"C:\Audio".AsOsAgnostic(), @"C:\Ebooks".AsOsAgnostic() })));
                    Assert.That(progressiveWrites, Is.EqualTo(newAuthor ? 0 : 2));
                    Assert.That(savedAuthor.AudiobookQualityProfileId, Is.EqualTo(3));
                    Assert.That(savedAuthor.AudiobookMetadataProfileId, Is.EqualTo(4));
                    Assert.That(savedAuthor.EbookQualityProfileId, Is.EqualTo(5));
                    Assert.That(savedAuthor.EbookMetadataProfileId, Is.EqualTo(6));
                    Assert.That(savedAuthor.AudiobookRootFolderPath, Is.EqualTo(@"C:\Audio".AsOsAgnostic()));
                    Assert.That(savedAuthor.EbookRootFolderPath, Is.EqualTo(@"C:\Ebooks".AsOsAgnostic()));
                });
                new AuthorScannedHandler(new BookMonitoredService(authorService, books, logger), authorService, queue,
                    new BookAddedService(new CacheManager(), queue, books, logger), events, logger, books).Handle(new AuthorScannedEvent(savedAuthor));
                Assert.That(commands.OfType<MissingBookSearchCommand>().ToList(), Has.Count.EqualTo(newAuthor || consumeHeldSearchBeforeRetry ? 1 : 0));
                if (newAuthor && !consumeHeldSearchBeforeRetry)
                {
                    Assert.That(bookRows.GetMissingBookSearchTargets(null, savedAuthor.Id).Select(target => target.BookId),
                        Is.EquivalentTo(savedBooks.Where(book => book.GoodreadsWorkId == "gr:123").Select(book => book.Id)));
                    Assert.That(commands.OfType<BookSearchCommand>(), Is.Empty);
                }
                else
                {
                    Assert.That(commands.OfType<BookSearchCommand>().SelectMany(command => command.BookIds),
                        Is.EquivalentTo(savedBooks.Where(book => book.GoodreadsWorkId == "gr:123").Select(book => book.Id)));
                    Assert.That(commands.OfType<BookSearchCommand>().Single().Trigger, Is.Not.EqualTo(CommandTrigger.Manual));
                }
                TestContext.WriteLine($"{database.DatabaseType}, new author {newAuthor}, catalog rows {savedBooks.Count}: progressive calls {progressiveCalls.Count}, progressive author writes {progressiveWrites}");
                var commandCount = commands.Count;
                ImportListSyncServiceSearchOnAddFixture.BuildMissingMediaSideService(savedAuthor, other, side, monitored,
                    authorService, books, editions, pending, roots, queue, bothSides: true,
                    metadataLookup: (_, _) => System.Threading.Interlocked.Increment(ref ingressMetadataReads)).Execute(new NzbDrone.Core.ImportLists.ImportListSyncCommand(1));
                Assert.That(ingressMetadataReads, Is.EqualTo(1), "Stored work ownership must not repeat ingress metadata proof.");
                Assert.That(pending.GetByProviderId(author.GoodreadsAuthorId), Is.Null);
                Assert.That(progressiveCalls.Count, Is.EqualTo(progressivePasses * 2));
                Assert.That(progressiveWrites, Is.EqualTo(newAuthor ? 0 : 2));
                Assert.That(commands.Count, Is.EqualTo(commandCount));
            });
        }

        private static Book CatalogBook(BookMediaType side, string title, string work)
        {
            return new Book
            {
                Title = title, CleanTitle = title.ToLowerInvariant(), TitleSlug = title.ToLowerInvariant().Replace(" ", "-"),
                MediaType = side, GoodreadsWorkId = "gr:" + work, HardcoverBookId = "hc:" + work,
                Added = DateTime.UtcNow, ReleaseDate = DateTime.UtcNow.AddDays(-2), Editions = new List<Edition> { new()
                {
                    Title = title, ForeignEditionId = "hc:edition:" + work + (side == BookMediaType.Audiobook ? "1" : "2"),
                    ReadingFormatId = side == BookMediaType.Audiobook ? 2 : 3,
                    Narrator = side == BookMediaType.Audiobook ? "Fixture Narrator" : null, Language = "eng",
                    ReleaseDate = DateTime.UtcNow.AddDays(-2)
                } }
            };
        }
    }
}
