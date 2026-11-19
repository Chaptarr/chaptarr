using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Chaptarr.Api.V1.PendingImport;
using Chaptarr.Core.Test.Common;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.Books
{
    public partial class BookServiceWantedNarratorSeriesLinksFixture
    {
        [TestCase(false)]
        [TestCase(true)]
        public void pending_list_batch_failure_preserves_all_intent_and_retry_searches_it_once(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var logger = LogManager.GetCurrentClassLogger();
                var events = InterfaceProxy.Proxy<IEventAggregator>((_, _) => null);
                var authors = new AuthorRepository(database, events, logger);
                var author = authors.Insert(new Author
                {
                    Name = "Requested author", CleanName = "requestedauthor", SortName = "requestedauthor",
                    TitleSlug = "requested-author", Path = @"C:\Audio\Requested Author".AsOsAgnostic(),
                    AudiobookPath = @"C:\Audio\Requested Author".AsOsAgnostic(), EbookPath = @"C:\Audio\Requested Author".AsOsAgnostic(),
                    AudiobookRootFolderPath = @"C:\Audio".AsOsAgnostic(), EbookRootFolderPath = @"C:\Audio".AsOsAgnostic(),
                    HardcoverAuthorId = "hc:9001", AudiobookMonitored = true, EbookMonitored = false,
                    Added = DateTime.UtcNow
                });
                var authorService = InterfaceProxy.Proxy<IAuthorService>((method, args) => method.Name switch
                {
                    nameof(IAuthorService.GetAuthor) => authors.Get((int)args[0]),
                    nameof(IAuthorService.GetExistingAuthors) => authors.Get((IEnumerable<int>)args[0]).ToList(),
                    nameof(IAuthorService.GetAuthors) => authors.Get((IEnumerable<int>)args[0]).ToList(),
                    nameof(IAuthorService.FindByProviderId) => authors.Get(author.Id),
                    _ => throw new NotImplementedException(method.Name)
                });
                var bookRepository = new BookRepository(database, events);
                var editionRepository = new EditionRepository(database, events);
                var editionService = new EditionService(editionRepository, events, logger);
                var roots = InterfaceProxy.Proxy<IRootFolderService>((method, _) => method.Name == nameof(IRootFolderService.All)
                    ? new List<RootFolder> { new() { Path = @"C:\Audio".AsOsAgnostic(), FolderType = FolderType.Mixed } }
                    : throw new NotImplementedException(method.Name));
                var books = new BookService(bookRepository, editionService, events, authorService, new StubMediaFileService(), roots,
                    new SeriesBookLinkRepository(database, events), new StubMultiCopySeriesService(), logger);
                var targets = new List<Book>();
                foreach (var (work, media) in new[] { ("hc:1001", BookMediaType.Audiobook), ("hc:1002", BookMediaType.Audiobook), ("hc:1003", BookMediaType.Ebook) })
                {
                    var book = bookRepository.Insert(new Book
                    {
                        AuthorId = author.Id, Title = "Requested book", CleanTitle = "requestedbook", TitleSlug = work.Replace(':', '-'),
                        HardcoverBookId = work, MediaType = media, AnyEditionOk = true, Added = DateTime.UtcNow
                    });
                    editionRepository.Insert(new Edition { BookId = book.Id, Title = book.Title, ForeignEditionId = work + "0", Monitored = true,
                        ReadingFormatId = media == BookMediaType.Audiobook ? 2 : 4 });
                    targets.Add(book);
                }
                var accepted = new List<BookSearchCommand>();
                var pushes = 0;
                var rejectBatch = true;
                var queue = InterfaceProxy.Proxy<IManageCommandQueue>((method, args) =>
                {
                    if (method.Name == nameof(IManageCommandQueue.Push) && args[0] is BookSearchCommand search)
                    {
                        pushes++;
                        if (rejectBatch) throw new InvalidOperationException("Injected queue rejection");
                        if ((CommandTrigger)args[2] != CommandTrigger.Unspecified) search.Trigger = (CommandTrigger)args[2];
                        accepted.Add(search);
                    }
                    return method.ReturnType == typeof(CommandModel) ? new CommandModel() : null;
                });
                var pendingRepository = new PendingAuthorImportRepository(database, events);
                var pending = new PendingAuthorImportService(pendingRepository, authorService, events, logger);
                var config = new MonitoringConfig
                {
                    CreateAudiobook = true, CreateEbook = true,
                    AudiobookBooksToMonitor = new List<string> { "hc:1001", "hc:1002" },
                    AudiobookBooksToSearch = new List<string> { "hc:1001", "hc:1002" },
                    EbookBooksToMonitor = new List<string> { "hc:1003" }, EbookBooksToSearch = new List<string> { "hc:1003" },
                    RequestedBy = "ImportList:Requested books"
                };
                pending.EnqueueAsync("hc:9001", config, config.RequestedBy).GetAwaiter().GetResult();
                var library = InterfaceProxy.Proxy<IAuthorLibraryService>((method, _) =>
                    method.Name == nameof(IAuthorLibraryService.AddAuthorAsync) ? Task.FromResult(authors.Get(author.Id)) :
                    throw new NotImplementedException(method.Name));
                var handler = new ProcessPendingImportsCommandHandler(pending, library, books, null, queue, events, logger);
                handler.Execute(new ProcessPendingImportsCommand());
                var retry = pendingRepository.GetActiveByProviderId("hc:9001");
                Assert.That(retry, Is.Not.Null, $"Status={pendingRepository.GetByProviderId("hc:9001")?.OverallStatus}; error={pendingRepository.GetByProviderId("hc:9001")?.LastError}; accepted={accepted.Count}");
                Assert.That(retry.OverallStatus, Is.EqualTo(PendingImportStatus.Retrying));
                Assert.That(pushes, Is.EqualTo(1));
                Assert.That(accepted, Is.Empty);
                Assert.That(retry.AudiobookBooksToSearch, Does.Contain("hc:1001").And.Contain("hc:1002"));
                Assert.That(retry.EbookBooksToSearch, Does.Contain("hc:1003"));
                Assert.That(bookRepository.Get(targets.Select(book => book.Id)).Where(book => book.AddOptions.SearchForNewBook).Select(book => book.Id),
                    Is.EquivalentTo(targets.Select(book => book.Id)));
                retry.NextAttemptAt = DateTime.UtcNow.AddMinutes(-1);
                pendingRepository.Update(retry);
                rejectBatch = false;
                handler.Execute(new ProcessPendingImportsCommand());
                Assert.That(pendingRepository.GetActiveByProviderId("hc:9001"), Is.Null);
                Assert.That(pushes, Is.EqualTo(2));
                Assert.That(accepted, Has.Count.EqualTo(1));
                Assert.That(accepted.Single().BookIds, Is.EquivalentTo(targets.Select(book => book.Id)));
                Assert.That(accepted.Single().Trigger, Is.EqualTo(CommandTrigger.Unspecified));
                Assert.That(bookRepository.Get(targets.Select(book => book.Id)).All(book => !book.AddOptions.SearchForNewBook), Is.True);
                Assert.That(authors.Get(author.Id).EbookMonitored, Is.False);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void retry_search_progress_must_not_erase_a_newer_merged_request(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var logger = LogManager.GetCurrentClassLogger();
                var events = InterfaceProxy.Proxy<IEventAggregator>((_, _) => null);
                var repository = new PendingAuthorImportRepository(database, events);
                var service = new PendingAuthorImportService(repository, InterfaceProxy.Proxy<IAuthorService>((_, _) => null), events, logger);
                var config = new MonitoringConfig
                {
                    CreateAudiobook = true, RequestedBy = "ImportList:Requested books",
                    AudiobookBooksToMonitor = new List<string> { "hc:1001" }, AudiobookBooksToSearch = new List<string> { "hc:1001" }
                };
                service.EnqueueAsync("hc:9001", config, config.RequestedBy).GetAwaiter().GetResult();
                var worker = repository.GetActiveByProviderId("hc:9001");
                worker.AudiobookBooksToSearch = "[]";
                config.AudiobookBooksToMonitor = new List<string> { "hc:1002" };
                config.AudiobookBooksToSearch = new List<string> { "hc:1002" };
                service.EnqueueAsync("hc:9001", config, config.RequestedBy).GetAwaiter().GetResult();
                service.ScheduleRetry(worker, "Injected queue rejection");
                var saved = repository.GetActiveByProviderId("hc:9001");
                Assert.That(saved.AudiobookBooksToSearch, Does.Contain("hc:1001").And.Contain("hc:1002"));
                Assert.That(saved.OverallStatus, Is.EqualTo(PendingImportStatus.Retrying));
            });
        }

        [TestCase(false, false, false, false)]
        [TestCase(true, false, false, false)]
        [TestCase(false, true, false, false)]
        [TestCase(true, true, false, false)]
        [TestCase(false, false, true, false)]
        [TestCase(true, false, true, false)]
        [TestCase(false, false, false, true)]
        [TestCase(true, false, false, true)]
        public void requested_profile_filtered_narration_should_import_pin_refresh_and_heal_an_existing_retry(bool postgres, bool alreadyStuck, bool initiallyMissing, bool missingForeignId)
        {
            RunWithDatabase(postgres, database =>
            {
                using var logFactory = new LogFactory();
                var memory = new MemoryTarget { Layout = "${level}|${message}|${exception:format=tostring}" };
                var logging = new LoggingConfiguration();
                logging.AddRule(LogLevel.Debug, LogLevel.Fatal, memory);
                logFactory.Configuration = logging;
                var logger = logFactory.GetLogger("RequestedProfileNarration");
                var events = new StubEventAggregator();
                var profiles = new MetadataProfileRepository(database, events);
                var profile = profiles.Insert(new MetadataProfile
                {
                    Name = "English dated books", AllowedLanguages = "eng", SkipMissingDate = true,
                    MinPopularity = 0, Ignored = new List<string>()
                });
                var authors = new AuthorRepository(database, events, logger);
                var authorService = InterfaceProxy.Proxy<IAuthorService>((method, args) =>
                {
                    switch (method.Name)
                    {
                        case nameof(IAuthorService.FindByProviderId): return authors.All().FirstOrDefault();
                        case nameof(IAuthorService.GetAuthor): return authors.Get((int)args[0]);
                        case nameof(IAuthorService.GetExistingAuthors): return authors.Get((IEnumerable<int>)args[0]).ToList();
                        case nameof(IAuthorService.GetAllAuthors): return authors.All().ToList();
                        case nameof(IAuthorService.GetAuthors): return authors.Get((IEnumerable<int>)args[0]).ToList();
                        case nameof(IAuthorService.AddAuthor): return authors.Insert((Author)args[0]);
                        case nameof(IAuthorService.UpdateAuthor): return authors.Update((Author)args[0]);
                        case nameof(IAuthorService.UpdateAuthorProgressiveSettings): return args[0];
                        case nameof(IAuthorService.AuthorPathExists): return false;
                        case nameof(IAuthorService.SetAddOptions): authors.SetFields((Author)args[0], author => author.AddOptions); return null;
                        default: throw new NotImplementedException(method.Name);
                    }
                });
                var root = new RootFolder { Path = @"C:\Audio".AsOsAgnostic(), FolderType = FolderType.Audiobook };
                root.SetAudiobookSettings(new MediaTypeSettings
                {
                    QualityProfileId = 1, MetadataProfileId = profile.Id, Monitored = false,
                    MonitorExistingBooks = false, MonitorNewItems = NewItemMonitorTypes.None
                });
                var roots = InterfaceProxy.Proxy<IRootFolderService>((method, _) => method.Name switch
                {
                    nameof(IRootFolderService.All) => new List<RootFolder> { root },
                    nameof(IRootFolderService.GetBestRootFolder) => root,
                    _ => throw new NotImplementedException(method.Name)
                });
                var bookRepository = new BookRepository(database, events);
                var editionRepository = new EditionRepository(database, events);
                var editionService = new EditionService(editionRepository, events, logger);
                var files = new StubMediaFileService();
                var books = new BookService(bookRepository, editionService, events, authorService, files, roots,
                    new SeriesBookLinkRepository(database, events), new StubMultiCopySeriesService(), logger);
                var profileService = new MetadataProfileService(profiles, authorService, books, editionService, files, null, roots,
                    new TestTermMatcherService(), events, logger);
                var profileFiltering = new List<string>();
                string DescribeCatalog(IEnumerable<Book> catalog)
                {
                    var rows = catalog.ToList();
                    var editions = rows.SelectMany(book => book.Editions ?? new List<Edition>()).ToList();
                    return $"{rows.Count} works / {editions.Count} editions [{string.Join(", ", editions.Select(edition => edition.ForeignEditionId ?? edition.HardcoverEditionId))}]";
                }
                var recordedProfileService = InterfaceProxy.Proxy<IMetadataProfileService>((method, args) =>
                {
                    var before = method.Name == nameof(IMetadataProfileService.FilterBooks)
                        ? DescribeCatalog(((Author)args[0]).Books) : null;
                    var result = method.Invoke(profileService, args);
                    if (before != null)
                    {
                        var message = $"{(postgres ? "PostgreSQL" : "SQLite")}: profile filtering {before} -> {DescribeCatalog((List<Book>)result)}";
                        profileFiltering.Add(message);
                        TestContext.WriteLine(message);
                    }
                    return result;
                });
                var filter = new EditionMetadataProfileFilter(new TestTermMatcherService());
                var requestedEditionAvailable = !initiallyMissing;
                var includeCompatibleEdition = false;
                var reverseCompatibleEditions = false;
                var requestedTitle = "French recording";
                var requestedNarrator = "Requested Narrator";
                var requestedOverview = "Original requested edition overview";
                HashSet<string> requestedProviderIds = null;
                Author Catalog()
                {
                    Book Pocket(string work, string edition) => new Book
                    {
                        Title = "Requested work", Overview = string.Empty, TitleSlug = work.Replace(':', '-'), CleanTitle = "requestedwork",
                        HardcoverBookId = work, MediaType = BookMediaType.Audiobook, AnyEditionOk = true,
                        Editions = new List<Edition>
                        {
                            new() { ForeignEditionId = missingForeignId ? null : edition, HardcoverEditionId = edition, Title = requestedTitle, Overview = edition == "hc:edition:202" ? requestedOverview : string.Empty,
                                RemoteProviderIds = edition == "hc:edition:202" ? requestedProviderIds : null,
                                ReadingFormatId = 2, Language = "fra", Narrator = requestedNarrator, Monitored = true },
                            new() { ForeignEditionId = edition + "9", Title = "Other French recording", ReadingFormatId = 2, Language = "fra", Narrator = "Other Narrator" }
                        }
                    };
                    var requestedBook = Pocket("hc:1001", "hc:edition:202");
                    if (initiallyMissing)
                    {
                        requestedBook.ReleaseDate = new DateTime(2020, 1, 1);
                        requestedBook.Editions[1].Language = "eng";
                        if (!requestedEditionAvailable)
                        {
                            requestedBook.Editions.RemoveAt(0);
                        }
                    }

                    if (includeCompatibleEdition)
                    {
                        var compatible = new Edition { ForeignEditionId = missingForeignId ? null : "hc:edition:203", HardcoverEditionId = "hc:edition:203",
                            Title = "Another edition of requested narration", ReadingFormatId = 2, Language = "fra", Narrator = "Requested Narrator" };
                        if (missingForeignId && reverseCompatibleEditions)
                            requestedBook.Editions.Insert(0, compatible);
                        else
                            requestedBook.Editions.Add(compatible);
                    }

                    return new Author
                    {
                        Name = "Requested Author", Overview = string.Empty, CleanName = "requestedauthor", TitleSlug = "requested-author", HardcoverAuthorId = "hc:9001",
                        Path = @"C:\Audio\Requested Author".AsOsAgnostic(), AudiobookPath = @"C:\Audio\Requested Author".AsOsAgnostic(),
                        Books = new List<Book> { requestedBook, Pocket("hc:1002", "hc:edition:303") },
                        Series = new List<Series>()
                    };
                }
                var fetches = 0;
                var authorInfo = InterfaceProxy.Proxy<IProvideAuthorInfo>((method, _) =>
                {
                    Assert.That(method.Name, Is.EqualTo(nameof(IProvideAuthorInfo.GetAuthorInfo)));
                    fetches++;
                    return Catalog();
                });
                var exclusions = InterfaceProxy.Proxy<IImportListExclusionService>((method, _) => method.Name switch
                {
                    nameof(IImportListExclusionService.All) => new List<ImportListExclusion>(),
                    nameof(IImportListExclusionService.FindByForeignId) => new List<ImportListExclusion>(),
                    _ => throw new NotImplementedException(method.Name)
                });
                var searches = new List<BookSearchCommand>();
                var rejectSearch = false;
                var queue = InterfaceProxy.Proxy<IManageCommandQueue>((method, args) =>
                {
                    if (method.Name == nameof(IManageCommandQueue.Push) && args[0] is BookSearchCommand search)
                    {
                        if (rejectSearch) throw new InvalidOperationException("Queue full");
                        searches.Add(search);
                    }
                    return method.ReturnType == typeof(CommandModel) ? new CommandModel() : null;
                });
                var covers = InterfaceProxy.Proxy<IMapCoversToLocal>((method, _) => null);
                var selector = new EditionSelector(logger);
                var refreshEditions = new RefreshEditionService(editionService, InterfaceProxy.Proxy<IMetadataTagService>((_, _) => null), null, database, logger);
                var refreshBooks = new RefreshBookService(books, authorService, roots, editionService, authorInfo, null,
                    refreshEditions, files, null, events, null, selector, filter, covers, logger, mainDatabase: null);
                var configService = InterfaceProxy.Proxy<IConfigService>((method, _) => method.ReturnType == typeof(bool) ? false :
                    method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null);
                var monitorNew = InterfaceProxy.Proxy<IMonitorNewBookService>((_, _) => false);
                var refresh = new RefreshAuthorService(MetadataHealthGateStub.Ready(), authorInfo, authorService, books, editionService, recordedProfileService, refreshBooks, null,
                    events, queue, files, new global::NzbDrone.Core.History.HistoryRepository(database, events), roots, null, monitorNew, configService, exclusions, null, null, null, logger, editionSelector: selector, mainDatabase: database, bookRepository: new BookRepository(database, events), authorRepository: new AuthorRepository(database, events, logger));
                var pendingRepository = new PendingAuthorImportRepository(database, events);
                var pendingService = new PendingAuthorImportService(pendingRepository, authorService, events, logger);
                var paths = InterfaceProxy.Proxy<IBuildAuthorPaths>((method, _) => method.ReturnType == typeof(string) ? @"C:\Audio\Requested Author".AsOsAgnostic() : null);
                var library = new AuthorLibraryService(authorService, authorInfo, books, null, editionService, null, recordedProfileService,
                    new TestQualityProfileService(), paths, roots, queue, events, pendingService, database, exclusions, filter, null,
                    logger, selector, refresh);
                var config = new MonitoringConfig
                {
                    CreateAudiobook = true, CreateEbook = false, AudiobookRootFolderPath = root.Path,
                    AudiobookQualityProfileId = 1, AudiobookMetadataProfileId = profile.Id, AudiobookMonitored = false,
                    AudiobookMonitorExistingMode = MonitorTypes.None, AudiobookMonitorNewItems = NewItemMonitorTypes.None,
                    AudiobookBooksToMonitor = new List<string> { "hc:1001" },
                    AudiobookNarrations = new List<RequestedNarration>
                    {
                        new() { WorkProviderId = "hc:1001", EditionProviderId = "hc-ed:202", RequiresEdition = true, Search = true }
                    },
                    RequestedBy = "ImportList:Requested Narration"
                };
                if (alreadyStuck)
                {
                    var author = Catalog();
                    author.Added = DateTime.UtcNow;
                    author.AudiobookRootFolderPath = root.Path;
                    author.AudiobookQualityProfileId = 1;
                    author.AudiobookMetadataProfileId = profile.Id;
                    author.AudiobookMonitored = false;
                    authors.Insert(author);
                    Assert.That(profileService.FilterBooks(new Author { Id = author.Id, Books = Catalog().Books }, profile.Id), Is.Empty);
                }
                pendingService.EnqueueAsync("hc:9001", config, config.RequestedBy).GetAwaiter().GetResult();
                var item = pendingRepository.GetByProviderId("hc:9001");
                if (alreadyStuck)
                {
                    item.OverallStatus = PendingImportStatus.Retrying;
                    item.AudiobookStatus = PendingImportStatus.Retrying;
                    item.AttemptCount = 7;
                    item.NextAttemptAt = DateTime.UtcNow.AddMinutes(-1);
                    pendingRepository.Update(item);
                }
                var rescueLookups = 0;
                var rescue = InterfaceProxy.Proxy<IProvideBookInfo>((method, _) =>
                {
                    rescueLookups++;
                    throw new InvalidOperationException("The requested narration exists in the supplied author catalog and must not require a metadata rescue.");
                });
                var handler = new ProcessPendingImportsCommandHandler(pendingService, library, books, rescue, queue, events, logger);
                rejectSearch = alreadyStuck;
                handler.Execute(new ProcessPendingImportsCommand());
                if (alreadyStuck)
                {
                    var retry = pendingRepository.GetActiveByProviderId("hc:9001");
                    Assert.That(retry.OverallStatus, Is.EqualTo(PendingImportStatus.Retrying));
                    Assert.That(books.GetBooksByAuthor(authors.All().Single().Id).Single().AddOptions.SearchForNewBook, Is.True);
                    retry.NextAttemptAt = DateTime.UtcNow.AddMinutes(-1);
                    pendingRepository.Update(retry);
                    rejectSearch = false;
                    handler.Execute(new ProcessPendingImportsCommand());
                }
                if (initiallyMissing)
                {
                    var failed = pendingRepository.GetByProviderId("hc:9001");
                    Assert.That(failed.OverallStatus, Is.EqualTo(PendingImportStatus.Failed));
                    Assert.That(failed.LastError, Does.Contain("Requested Audiobook edition hc-ed:202")
                        .And.Contain("unavailable in the receiving author's profile-filtered catalog")
                        .And.Contain("edition request was not fulfilled")
                        .And.Contain("Automatic retries stopped")
                        .And.Contain("later list refresh"));
                    var resource = failed.ToResource();
                    Assert.That(resource.OverallStatus, Is.EqualTo("Failed"));
                    Assert.That(resource.LastError, Is.EqualTo(failed.LastError));
                    var fallback = books.GetBooksByAuthor(authors.All().Single().Id).Single();
                    Assert.That(fallback.IsMonitored(), Is.True);
                    Assert.That(fallback.AnyEditionOk, Is.True);
                    Assert.That(fallback.Editions.Any(edition => edition.ManualAdd), Is.False);
                    handler.Execute(new ProcessPendingImportsCommand());
                    Assert.That(fetches, Is.EqualTo(1), "A terminal failure must not refetch the author every retry cycle.");
                    requestedEditionAvailable = true;
                    pendingService.EnqueueAsync("hc:9001", config, config.RequestedBy).GetAwaiter().GetResult();
                    handler.Execute(new ProcessPendingImportsCommand());
                }

                Assert.That(rescueLookups, Is.Zero, string.Join("\n", profileFiltering));
                Assert.That(pendingRepository.GetActiveByProviderId("hc:9001"), Is.Null,
                    pendingRepository.GetByProviderId("hc:9001")?.LastError + "\n" +
                    string.Join("\n", memory.Logs.Where(message => message.Contains("Exception", StringComparison.Ordinal))));
                var savedAuthor = authors.All().Single();
                var savedBooks = books.GetBooksByAuthor(savedAuthor.Id);
                Assert.That(savedBooks, Has.Count.EqualTo(1));
                var saved = savedBooks.Single();
                Assert.That(saved.Editions, Has.Count.EqualTo(initiallyMissing ? 2 : 1));
                Assert.That(saved.Editions.Single(edition => edition.Monitored).Language, Is.EqualTo("fra"));
                Assert.That(saved.AnyEditionOk, Is.False);
                Assert.That(saved.IsMonitored(), Is.True);
                Assert.That(saved.Editions.Single(edition => edition.Monitored).HardcoverEditionId, Is.EqualTo("hc:edition:202"));
                Assert.That(saved.Editions.Single(edition => edition.Monitored).ForeignEditionId, Is.EqualTo(missingForeignId ? null : "hc:edition:202"));
                Assert.That(saved.Editions.Single(edition => edition.Monitored).ManualAdd, Is.True);
                Assert.That(fetches, Is.EqualTo(initiallyMissing || alreadyStuck ? 2 : 1));
                Assert.That(searches, Has.Count.EqualTo(1));
                Assert.That(saved.AddOptions.SearchForNewBook, Is.False);
                var savedEdition = saved.Editions.Single(edition => edition.Monitored);
                var savedEditionId = savedEdition.Id;
                var savedNarrator = savedEdition.Narrator;
                if (missingForeignId)
                {
                    requestedTitle = "French recording refreshed";
                    requestedNarrator = "REQUESTED NARRATOR";
                    requestedOverview = "Refreshed requested edition overview";
                    requestedProviderIds = new HashSet<string> { "gr:7702", "gb:refreshed-202" };
                }
                Assert.Multiple(() =>
                {
                    refresh.ReconcileAuthorBlob(savedAuthor, Catalog());
                    var refreshed = books.GetBooksByAuthor(savedAuthor.Id).Single();
                    Assert.That(refreshed.Editions, Has.Count.EqualTo(initiallyMissing ? 2 : 1));
                    Assert.That(refreshed.Id, Is.EqualTo(saved.Id));
                    var refreshedEdition = refreshed.Editions.Single(edition => edition.Monitored);
                    TestContext.WriteLine($"{(postgres ? "PostgreSQL" : "SQLite")}: refreshed local edition {refreshedEdition.Id}, local book {refreshedEdition.BookId}, author {refreshed.AuthorId}, Hardcover {refreshedEdition.HardcoverEditionId}, title {refreshedEdition.Title}, narrator {refreshedEdition.Narrator}");
                    Assert.That(refreshed.AuthorId, Is.EqualTo(savedAuthor.Id));
                    Assert.That(refreshedEdition.Id, Is.EqualTo(savedEditionId));
                    Assert.That(refreshedEdition.BookId, Is.EqualTo(saved.Id));
                    Assert.That(refreshedEdition.Title, Is.EqualTo(requestedTitle));
                    Assert.That(refreshedEdition.Narrator, Is.EqualTo(savedNarrator));
                    Assert.That(refreshedEdition.Overview, Is.EqualTo(requestedOverview));
                    if (missingForeignId)
                    {
                        Assert.That(editionRepository.FindAllByProviderIds("gr", new[] { "7702" })["7702"].Select(edition => edition.Id),
                            Is.EquivalentTo(new[] { savedEditionId }));
                        Assert.That(editionRepository.FindAllByProviderIds("gb", new[] { "refreshed-202" })["refreshed-202"].Select(edition => edition.Id),
                            Is.EquivalentTo(new[] { savedEditionId }));
                    }
                    Assert.That(refreshed.Editions.Single(edition => edition.Monitored).HardcoverEditionId, Is.EqualTo("hc:edition:202"));
                    Assert.That(refreshed.Editions.Single(edition => edition.Monitored).ForeignEditionId, Is.EqualTo(missingForeignId ? null : "hc:edition:202"));
                    Assert.That(refreshed.Editions.Single(edition => edition.Monitored).ManualAdd, Is.True);
                    Assert.That(savedAuthor.AudiobookMonitored, Is.False);
                    includeCompatibleEdition = true;
                    config.AudiobookMonitorExistingMode = null;
                    config.AudiobookNarrations.Single().EditionProviderId = "hc-ed:203";
                    for (var sync = 0; sync < 2; sync++)
                    {
                        reverseCompatibleEditions = sync == 0;
                        pendingService.EnqueueAsync("hc:9001", config, config.RequestedBy).GetAwaiter().GetResult();
                        handler.Execute(new ProcessPendingImportsCommand());
                        Assert.That(pendingRepository.GetActiveByProviderId("hc:9001"), Is.Null);
                        Assert.That(searches, Has.Count.EqualTo(1), "A compatible already-monitored narration must not repeat its search.");
                        refresh.ReconcileAuthorBlob(savedAuthor, Catalog());
                        var repeated = books.GetBooksByAuthor(savedAuthor.Id).Single();
                        Assert.That(repeated.Id, Is.EqualTo(saved.Id));
                        var repeatedEdition = repeated.Editions.Single(edition => edition.Monitored);
                        TestContext.WriteLine($"{(postgres ? "PostgreSQL" : "SQLite")}: repeated local edition {repeatedEdition.Id}, local book {repeatedEdition.BookId}, author {repeated.AuthorId}, Hardcover {repeatedEdition.HardcoverEditionId}, title {repeatedEdition.Title}, narrator {repeatedEdition.Narrator}");
                        Assert.That(repeated.AuthorId, Is.EqualTo(savedAuthor.Id));
                        Assert.That(repeatedEdition.Id, Is.EqualTo(savedEditionId));
                        Assert.That(repeatedEdition.BookId, Is.EqualTo(saved.Id));
                        Assert.That(repeatedEdition.Title, Is.EqualTo(requestedTitle));
                        Assert.That(repeatedEdition.Narrator, Is.EqualTo(savedNarrator));
                        Assert.That(repeatedEdition.Overview, Is.EqualTo(requestedOverview));
                        if (missingForeignId)
                        {
                            Assert.That(editionRepository.FindAllByProviderIds("gr", new[] { "7702" })["7702"].Select(edition => edition.Id),
                                Is.EquivalentTo(new[] { savedEditionId }));
                            Assert.That(editionRepository.FindAllByProviderIds("gb", new[] { "refreshed-202" })["refreshed-202"].Select(edition => edition.Id),
                                Is.EquivalentTo(new[] { savedEditionId }));
                        }
                        Assert.That(repeated.Editions.Single(edition => edition.Monitored).HardcoverEditionId, Is.EqualTo("hc:edition:202"));
                        Assert.That(repeated.Editions.Single(edition => edition.Monitored).ForeignEditionId, Is.EqualTo(missingForeignId ? null : "hc:edition:202"));
                        Assert.That(repeated.Editions.Any(edition => edition.HardcoverEditionId == "hc:edition:203"), Is.False);
                    }
                });
            });
        }
    }
}
