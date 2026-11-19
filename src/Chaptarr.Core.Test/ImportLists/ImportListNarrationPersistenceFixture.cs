using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.ImportLists.Hardcover.Library;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaCover.Commands;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RootFolders;
using static Chaptarr.Core.Test.Common.InterfaceProxy;
using static Chaptarr.Core.Test.Books.AuthorLibraryServiceSpecificBookMonitoringFixture;

namespace Chaptarr.Core.Test.ImportLists
{
    [TestFixture]
    [NonParallelizable]
    public class ImportListNarrationPersistenceFixture
    {
        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void repeated_list_sync_should_preserve_pins_reuse_equivalent_cast_and_persist_cache(bool postgres, bool existingFry)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var logger = LogManager.GetCurrentClassLogger();
                var completed = new List<ImportListSyncCompleteEvent>();
                var events = Proxy<IEventAggregator>((method, args) =>
                {
                    if (args[0] is ImportListSyncCompleteEvent sync) completed.Add(sync);
                    return null;
                });
                var queuedCommands = new List<Command>();
                var commands = Proxy<IManageCommandQueue>((method, args) =>
                {
                    queuedCommands.Add((Command)args[0]);
                    return new CommandModel { Id = queuedCommands.Count, Body = (Command)args[0] };
                });
                var authors = new AuthorRepository(database, events, logger);
                var bookRows = new BookRepository(database, events);
                var editionRows = new EditionRepository(database, events);
                var fileRows = new MediaFileRepository(database, events);
                var files = Proxy<IMediaFileService>((method, args) => method.Name == nameof(IMediaFileService.GetFilesByBooks)
                    ? fileRows.GetFilesByBooks((List<int>)args[0]) : throw new AssertionException(method.Name));
                var aliases = new ProviderAliasService(new ProviderAliasRepository(database, events));
                var roots = new StubRootFolderService(BuildAudiobookRoot(@"C:\Audio".AsOsAgnostic()),
                    BuildEbookRoot(@"C:\Ebooks".AsOsAgnostic()));
                var paths = new StubAuthorPathBuilder();
                var cache = new ImportListBookIdentityCacheRepository(database, events);

                Author AddAuthor(string name, string providerId) => authors.Insert(new Author
                {
                    Name = name, CleanName = name.ToLowerInvariant().Replace(" ", ""), SortName = name,
                    TitleSlug = name.ToLowerInvariant().Replace(" ", "-"),
                    Path = (@"C:\Audio\" + name).AsOsAgnostic(),
                    AudiobookRootFolderPath = @"C:\Audio".AsOsAgnostic(), EbookRootFolderPath = @"C:\Ebooks".AsOsAgnostic(),
                    HardcoverAuthorId = providerId, AudiobookMonitored = true, EbookMonitored = false,
                    LastInfoSync = DateTime.UtcNow.AddDays(-1), Added = DateTime.UtcNow
                });
                var author = AddAuthor("Listed Author", "hc:777");
                var foreignAuthor = AddAuthor("Other Author", "hc:888");
                Book AddBook(Author owner, string slug, BookMediaType media, string narrator, string editionId, bool pinned)
                {
                    var book = bookRows.Insert(new Book
                    {
                        AuthorId = owner.Id, Title = "Listed Work", CleanTitle = "listedwork", TitleSlug = slug,
                        HardcoverBookId = "hc:1001", MediaType = media, AnyEditionOk = !pinned,
                        AudiobookMonitored = media == BookMediaType.Audiobook,
                        EbookMonitored = media == BookMediaType.Ebook, Added = DateTime.UtcNow
                    });
                    editionRows.Insert(new Edition
                    {
                        BookId = book.Id, Title = book.Title, ForeignEditionId = editionId, HardcoverEditionId = editionId,
                        ReadingFormatId = 2, Narrator = narrator, NarratorNames = new List<string> { narrator },
                        Language = "eng", Monitored = true, ManualAdd = pinned
                    });
                    return book;
                }

                var dale = AddBook(author, "listed-work", BookMediaType.Audiobook, "Jim Dale", "hc:edition:201", true);
                editionRows.Insert(new Edition
                {
                    BookId = dale.Id, Title = dale.Title, ForeignEditionId = "hc:edition:202", HardcoverEditionId = "hc:edition:202",
                    ReadingFormatId = 2, Narrator = "Stephen Fry", NarratorNames = new List<string> { "Stephen Fry" }, Language = "eng"
                });
                var fry = existingFry ? AddBook(author, "listed-work-second", BookMediaType.Audiobook, "Stephen Fry", "hc:edition:203", false) : null;
                var foreign = AddBook(foreignAuthor, "other-author-work", BookMediaType.Audiobook, "Stephen Fry", "hc:edition:302", true);
                var ebook = AddBook(author, "listed-work-ebook", BookMediaType.Ebook, "Stephen Fry", "hc:edition:303", true);
                var dalePin = editionRows.FindByBook(new[] { dale.Id }).Single(edition => edition.Monitored).Id;
                var decoyPins = editionRows.FindByBook(new[] { foreign.Id, ebook.Id }).ToDictionary(edition => edition.BookId, edition => edition.Id);
                var originalFryPin = fry == null ? (int?)null : editionRows.FindByBook(new[] { fry.Id }).Single().Id;
                var list = new ImportListDefinition
                {
                    Id = 1, Name = "Narrations", EnableAutomaticAdd = true, ShouldMonitor = ImportListMonitorType.SpecificBook,
                    ShouldMonitorExisting = true, ShouldSearch = false, RootFolderPath = @"C:\Audio".AsOsAgnostic(),
                    MonitoringOptions = new ImportListMonitoringOptions { AddMissingAuthors = false },
                    Settings = new HardcoverLibraryImportListSettings { MonitorAudiobooks = true, MonitorEbooks = false }
                };
                var metadataRequests = new ConcurrentBag<(string Method, string Id, BookMediaType Media)>();
                var metadata = Proxy<IProvideBookInfo>((method, args) =>
                {
                    var requestedId = args[0]?.ToString();
                    var requestedMedia = (BookMediaType)args[1];
                    metadataRequests.Add((method.Name, requestedId, requestedMedia));
                    if (method.Name != nameof(IProvideBookInfo.GetEditionInfo) ||
                        requestedId is not ("hc:edition:201" or "hc:edition:999"))
                    {
                        throw new BookNotFoundException(requestedId);
                    }

                    var narrator = requestedId == "hc:edition:201" ? "Jim Dale" : "Stephen Fry";
                    return Tuple.Create(author.HardcoverAuthorId, new Book
                    {
                        Title = dale.Title, HardcoverBookId = dale.HardcoverBookId, Author = author,
                        MediaType = requestedMedia, Editions = new List<Edition> { new()
                        {
                            ForeignEditionId = requestedId, HardcoverEditionId = requestedId, ReadingFormatId = 2,
                            NarratorNames = new List<string> { narrator }, Narrator = narrator
                        } }
                    }, new List<Author> { author });
                });
                var pendingRequests = 0;
                var pending = Proxy<IPendingAuthorImportService>((method, args) =>
                {
                    pendingRequests++;
                    throw new AssertionException("A compatible local cast must not queue author import.");
                });
                int? stableFryBookId = fry?.Id;
                int? stableFryEditionId = originalFryPin;
                for (var pass = 0; pass < 3; pass++)
                {
                    var editions = new EditionService(editionRows, events, logger);
                    IBookService books = null;
                    var authorService = new AuthorService(authors, events, paths, roots, commands, new CacheManager(), bookRows,
                        files, logger, null, null, new Lazy<IBookService>(() => books), mainDatabase: database, aliases);
                    books = new BookService(bookRows, editions, events, authorService, files, roots,
                        new SeriesBookLinkRepository(database, events), null, logger, providerAliasService: aliases);
                    var reports = new[] { "hc:edition:201", "hc:edition:999" }.Select(id => new ImportListItemInfo
                    {
                        ImportListId = list.Id, Author = author.Name, AuthorProviderId = author.HardcoverAuthorId,
                        Book = dale.Title, BookProviderId = dale.HardcoverBookId, EditionProviderId = id, HardcoverReadingFormatId = 2
                    }).ToList();
                    var provider = Proxy<IImportList>((method, _) => method.Name == "get_Definition" ? list : throw new AssertionException(method.Name));
                    var factory = Proxy<IImportListFactory>((method, args) => method.Name switch
                    {
                        nameof(IImportListFactory.Get) => list,
                        nameof(IImportListFactory.GetInstance) => provider,
                        _ => throw new AssertionException(method.Name)
                    });
                    var fetcher = Proxy<IFetchAndParseImportList>((method, args) => method.Name == nameof(IFetchAndParseImportList.FetchSingleList)
                        ? reports : throw new AssertionException(method.Name));
                    var exclusions = new ImportListExclusionService(new ImportListExclusionRepository(database, events), logger);
                    new ImportListSyncService(factory, exclusions, fetcher, metadata, authorService, books, editions, pending,
                        roots, new RootFolderSettingsResolver(roots), events, commands, logger,
                        new ImportListBookIdentityCacheRepository(database, events)).Execute(new ImportListSyncCommand(list.Id));

                    var persisted = books.GetBooksByAuthor(author.Id);
                    var audio = persisted.Where(book => book.MediaType == BookMediaType.Audiobook).ToList();
                    Assert.That(audio, Has.Count.EqualTo(2));
                    var savedDale = audio.Single(book => book.Id == dale.Id);
                    var savedFry = audio.Single(book => BookEditionIdentity.GetMonitoredEdition(book).Narrator == "Stephen Fry");
                    var selectedFry = BookEditionIdentity.GetMonitoredEdition(savedFry);
                    stableFryBookId ??= savedFry.Id;
                    stableFryEditionId ??= selectedFry.Id;
                    Assert.Multiple(() =>
                    {
                        Assert.That(bookRows.All(), Has.Count.EqualTo(4));
                        Assert.That(savedDale.AnyEditionOk, Is.False);
                        Assert.That(BookEditionIdentity.GetMonitoredEdition(savedDale).Id, Is.EqualTo(dalePin));
                        Assert.That(BookEditionIdentity.GetMonitoredEdition(savedDale).ManualAdd, Is.True);
                        Assert.That(savedFry.Id, Is.EqualTo(stableFryBookId));
                        Assert.That(selectedFry.Id, Is.EqualTo(stableFryEditionId));
                        Assert.That(savedFry.AnyEditionOk, Is.False);
                        Assert.That(selectedFry.ManualAdd, Is.True);
                        Assert.That(selectedFry.ForeignEditionId, Is.Not.EqualTo("hc:edition:999"));
                        Assert.That(audio.All(book => book.IsMonitored() && book.Editions.Count(edition => edition.Monitored) == 1), Is.True);
                        Assert.That(metadataRequests.Select(request => request.Method), Is.All.EqualTo(nameof(IProvideBookInfo.GetEditionInfo)));
                        Assert.That(metadataRequests.Select(request => request.Id), Is.EquivalentTo(new[] { "hc:edition:201", "hc:edition:999" }));
                        Assert.That(metadataRequests.Select(request => request.Media), Is.All.EqualTo(BookMediaType.Audiobook));
                        Assert.That(pendingRequests, Is.Zero);
                        Assert.That(completed, Has.Count.EqualTo(pass + 1));
                        Assert.That(queuedCommands, Is.All.TypeOf<DownloadBookMediaCommand>());
                        Assert.That(queuedCommands.OfType<DownloadBookMediaCommand>().SelectMany(command => command.BookIds),
                            Is.EquivalentTo(existingFry ? Array.Empty<int>() : new[] { savedFry.Id }));
                    });
                    foreach (var decoy in new[] { foreign, ebook })
                    {
                        var stored = bookRows.Get(decoy.Id);
                        var selected = editionRows.FindByBook(new[] { decoy.Id }).Single(edition => edition.Monitored);
                        Assert.That(stored.AuthorId, Is.EqualTo(decoy.AuthorId));
                        Assert.That(stored.MediaType, Is.EqualTo(decoy.MediaType));
                        Assert.That(stored.AnyEditionOk, Is.False);
                        Assert.That(stored.AudiobookMonitored, Is.EqualTo(decoy.AudiobookMonitored));
                        Assert.That(stored.EbookMonitored, Is.EqualTo(decoy.EbookMonitored));
                        Assert.That(selected.Id, Is.EqualTo(decoyPins[decoy.Id]));
                        Assert.That(selected.ManualAdd, Is.True);
                    }
                    var cast = cache.All().Single(row => row.SourceProviderId == "edition:" + BookEditionIdentity.NormalizeRequestedEditionProviderId("hc:edition:999"));
                    Assert.That(cast.BookProviderId, Is.EqualTo(dale.HardcoverBookId));
                    Assert.That(cast.AuthorProviderId, Is.EqualTo(author.HardcoverAuthorId));
                    Assert.That(cast.EditionHasNarration, Is.True);
                    Assert.That(cast.EditionNarratorNames, Is.EqualTo(new[] { "Stephen Fry" }));
                    Assert.That(editionRows.All().Any(edition => edition.ForeignEditionId == "hc:edition:999"), Is.False);
                    TestContext.WriteLine("{0}: pass {1}, Dale local row {2}/edition {3}, Fry local row {4}/edition {5}, 4 books, metadata reads {6}",
                        postgres ? "PostgreSQL" : "SQLite", pass + 1, dale.Id, dalePin, savedFry.Id, selectedFry.Id, metadataRequests.Count);
                }
            });
        }

        [TestCase(false, "exact-edition", 1)]
        [TestCase(true, "exact-edition", 1)]
        [TestCase(false, "wrong-work", 1)]
        [TestCase(true, "wrong-work", 1)]
        [TestCase(false, "conflicting-edition", 1)]
        [TestCase(true, "conflicting-edition", 1)]
        [TestCase(false, "opposite-media", 0)]
        [TestCase(true, "opposite-media", 0)]
        [TestCase(false, "fresh-cache", 0)]
        [TestCase(true, "fresh-cache", 0)]
        [TestCase(false, "cached-conflicting-edition", 0)]
        [TestCase(true, "cached-conflicting-edition", 0)]
        [TestCase(false, "expired-cache", 1)]
        [TestCase(true, "expired-cache", 1)]
        [TestCase(false, "changed-owner", 1)]
        [TestCase(true, "changed-owner", 1)]
        [TestCase(false, "cached-edition-other-owner", 0)]
        [TestCase(true, "cached-edition-other-owner", 0)]
        [TestCase(false, "remote-primary", 1)]
        [TestCase(true, "remote-primary", 1)]
        [TestCase(false, "remote-primary-cohort", 1)]
        [TestCase(true, "remote-primary-cohort", 1)]
        [TestCase(false, "edition-only-primary", 1)]
        [TestCase(true, "edition-only-primary", 1)]
        [TestCase(false, "ambiguous-unavailable", 1)]
        [TestCase(true, "ambiguous-unavailable", 1)]
        [TestCase(false, "same-owner-copies", 0)]
        [TestCase(true, "same-owner-copies", 0)]
        [TestCase(false, "cached-edition-wrong-work", 1)]
        [TestCase(true, "cached-edition-wrong-work", 1)]
        public void indexed_source_and_cached_owner_should_require_current_same_media_work_evidence(bool postgres, string scenario, int expectedMetadataReads)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var logger = LogManager.GetCurrentClassLogger();
                var events = Proxy<IEventAggregator>((_, _) => null);
                var commandCalls = new ConcurrentBag<string>();
                var commands = Proxy<IManageCommandQueue>((method, _) =>
                {
                    commandCalls.Add(method.Name);
                    throw new AssertionException("Unchanged pinned rows must not queue commands.");
                });
                var authorRows = new AuthorRepository(database, events, logger);
                var bookRows = new BookRepository(database, events);
                var editionRows = new EditionRepository(database, events);
                var fileRows = new MediaFileRepository(database, events);
                var files = Proxy<IMediaFileService>((method, args) => method.Name == nameof(IMediaFileService.GetFilesByBooks)
                    ? fileRows.GetFilesByBooks((List<int>)args[0]) : throw new AssertionException(method.Name));
                var aliases = new ProviderAliasService(new ProviderAliasRepository(database, events));
                var roots = new StubRootFolderService(BuildAudiobookRoot(@"C:\Audio".AsOsAgnostic()), BuildEbookRoot(@"C:\Ebooks".AsOsAgnostic()));
                var cache = new ImportListBookIdentityCacheRepository(database, events);
                var cachedAt = DateTime.UtcNow.AddMinutes(-2);
                Author AddAuthor(string name, string providerId) => authorRows.Insert(new Author
                {
                    Name = name, CleanName = name.Replace(" ", "").ToLowerInvariant(), SortName = name,
                    TitleSlug = name.Replace(" ", "-").ToLowerInvariant(), HardcoverAuthorId = providerId,
                    Path = (@"C:\Audio\" + name).AsOsAgnostic(), AudiobookRootFolderPath = @"C:\Audio".AsOsAgnostic(),
                    EbookRootFolderPath = @"C:\Ebooks".AsOsAgnostic(), AudiobookMonitored = true, EbookMonitored = true,
                    LastInfoSync = cachedAt.AddMinutes(scenario == "expired-cache" ? 1 : -1), Added = cachedAt
                });
                var primary = AddAuthor("Source Primary", "hc:777");
                var other = AddAuthor("Other Owner", "hc:888");
                Book AddBook(Author owner, string slug, string workId, BookMediaType media, string editionId)
                {
                    var book = bookRows.Insert(new Book
                    {
                        AuthorId = owner.Id, Title = "Requested Work", CleanTitle = "requestedwork", TitleSlug = slug,
                        HardcoverBookId = workId, MediaType = media, AnyEditionOk = false,
                        AudiobookMonitored = media == BookMediaType.Audiobook, EbookMonitored = media == BookMediaType.Ebook, Added = cachedAt
                    });
                    editionRows.Insert(new Edition
                    {
                        BookId = book.Id, Title = book.Title, ForeignEditionId = editionId, HardcoverEditionId = editionId,
                        ReadingFormatId = media == BookMediaType.Audiobook ? 2 : 4,
                        Narrator = (scenario is "cached-edition-other-owner" or "cached-conflicting-edition") && owner.Id == other.Id ? "Jim Dale" : "Stephen Fry",
                        NarratorNames = new List<string> { (scenario is "cached-edition-other-owner" or "cached-conflicting-edition") && owner.Id == other.Id ? "Jim Dale" : "Stephen Fry" },
                        Language = "eng", Monitored = true, ManualAdd = true
                    });
                    return book;
                }
                var primaryWorkId = scenario == "changed-owner" ? "hc:1002" : "hc:1001";
                AddBook(primary, "primary-work", primaryWorkId, BookMediaType.Audiobook,
                    scenario == "cached-conflicting-edition" ? "hc:edition:999" : "hc:edition:201");
                var otherMedia = scenario == "opposite-media" ? BookMediaType.Ebook : BookMediaType.Audiobook;
                var otherEditionId = scenario is "conflicting-edition" or "opposite-media" ? "hc:edition:201" :
                    scenario is "cached-edition-other-owner" or "cached-conflicting-edition" ? "hc:edition:999" : "hc:edition:302";
                if (scenario is "remote-primary" or "remote-primary-cohort" or "edition-only-primary" or "ambiguous-unavailable")
                {
                    otherEditionId = "hc:edition:999";
                }
                AddBook(scenario == "same-owner-copies" ? primary : other, "other-work", "hc:1001", otherMedia, otherEditionId);
                if (scenario is "wrong-work" or "cached-edition-wrong-work")
                {
                    AddBook(primary, "different-work", "hc:1002", BookMediaType.Audiobook,
                        scenario == "wrong-work" ? "hc:edition:401" : "hc:edition:999");
                }
                var useCache = scenario.Contains("cache") || scenario == "changed-owner";
                var sourceEditionId = useCache || scenario is "remote-primary" or "remote-primary-cohort" or "edition-only-primary" or "ambiguous-unavailable"
                    ? "hc:edition:999" : "hc:edition:201";
                var report = new ImportListItemInfo
                {
                    ImportListId = 1, Book = "Requested Work", BookProviderId = scenario == "edition-only-primary" ? null : scenario == "wrong-work" ? "hc:1002" : "hc:1001",
                    EditionProviderId = sourceEditionId, Author = other.Name, AuthorProviderId = other.HardcoverAuthorId
                };
                if (useCache)
                {
                    cache.Insert(new ImportListBookIdentityCache
                    {
                        SourceProviderId = "edition:" + BookEditionIdentity.NormalizeRequestedEditionProviderId(sourceEditionId),
                        BookProviderId = "hc:1001", AuthorProviderId = primary.HardcoverAuthorId, Book = report.Book, Author = primary.Name,
                        EditionHasNarration = true, EditionNarratorNames = new List<string> { "Stephen Fry" }, CreatedAt = cachedAt, UpdatedAt = cachedAt
                    });
                }
                var list = new ImportListDefinition
                {
                    Id = 1, Name = "Source Ownership", EnableAutomaticAdd = true, ShouldMonitor = ImportListMonitorType.SpecificBook,
                    ShouldMonitorExisting = true, ShouldSearch = false, RootFolderPath = @"C:\Audio".AsOsAgnostic(),
                    MonitoringOptions = new ImportListMonitoringOptions { AddMissingAuthors = false },
                    Settings = new HardcoverLibraryImportListSettings { MonitorAudiobooks = true, MonitorEbooks = false }
                };
                var metadataRequests = new ConcurrentBag<(string Method, string Id, BookMediaType Media)>();
                var returnsPrimary = scenario is "exact-edition" or "remote-primary" or "remote-primary-cohort" or "edition-only-primary";
                var metadata = Proxy<IProvideBookInfo>((method, args) =>
                {
                    metadataRequests.Add((method.Name, args[0]?.ToString(), (BookMediaType)args[1]));
                    if (!returnsPrimary || method.Name != nameof(IProvideBookInfo.GetEditionInfo) || args[0]?.ToString() != sourceEditionId)
                    {
                        throw new BookNotFoundException(sourceEditionId);
                    }

                    var resolved = new Book
                    {
                        HardcoverBookId = "hc:1001", Title = report.Book, Author = primary, MediaType = BookMediaType.Audiobook,
                        Editions = new List<Edition> { new() { ForeignEditionId = sourceEditionId,
                            HardcoverEditionId = sourceEditionId, ReadingFormatId = 2,
                            Narrator = "Stephen Fry", NarratorNames = new List<string> { "Stephen Fry" } } }
                    };
                    return Tuple.Create(primary.HardcoverAuthorId, resolved, new List<Author> { primary });
                });
                var pendingCalls = new ConcurrentBag<string>();
                var pending = Proxy<IPendingAuthorImportService>((method, _) =>
                {
                    pendingCalls.Add(method.Name);
                    throw new AssertionException("Unproved identity must not enqueue author import.");
                });
                var editions = new EditionService(editionRows, events, logger);
                IBookService books = null;
                var authors = new AuthorService(authorRows, events, new StubAuthorPathBuilder(), roots, commands, new CacheManager(), bookRows,
                    files, logger, null, null, new Lazy<IBookService>(() => books), mainDatabase: database, aliases);
                books = new BookService(bookRows, editions, events, authors, files, roots,
                    new SeriesBookLinkRepository(database, events), null, logger, providerAliasService: aliases);
                var beforePins = editionRows.All().ToDictionary(edition => edition.Id, edition => (edition.BookId, edition.Monitored, edition.ManualAdd));
                var beforeBooks = bookRows.All().ToDictionary(book => book.Id,
                    book => (book.AuthorId, book.MediaType, book.AnyEditionOk, book.AudiobookMonitored, book.EbookMonitored));
                var provider = Proxy<IImportList>((method, _) => method.Name == "get_Definition" ? list : throw new AssertionException(method.Name));
                var factory = Proxy<IImportListFactory>((method, _) => method.Name switch
                {
                    nameof(IImportListFactory.Get) => list,
                    nameof(IImportListFactory.GetInstance) => provider,
                    _ => throw new AssertionException(method.Name)
                });
                var reports = Enumerable.Range(0, scenario == "remote-primary-cohort" ? 21 : 1)
                    .Select(_ => new ImportListItemInfo
                    {
                        ImportListId = report.ImportListId, Book = report.Book, BookProviderId = report.BookProviderId,
                        EditionProviderId = report.EditionProviderId, Author = report.Author, AuthorProviderId = report.AuthorProviderId
                    }).ToList();
                report = reports[0];
                var fetcher = Proxy<IFetchAndParseImportList>((method, _) => method.Name == nameof(IFetchAndParseImportList.FetchSingleList)
                    ? reports : throw new AssertionException(method.Name));
                var exclusions = new ImportListExclusionService(new ImportListExclusionRepository(database, events), logger);
                new ImportListSyncService(factory, exclusions, fetcher, metadata, authors, books, editions, pending, roots,
                    new RootFolderSettingsResolver(roots), events, commands, logger, cache).Execute(new ImportListSyncCommand(list.Id));
                Assert.Multiple(() =>
                {
                    Assert.That(metadataRequests, Has.Count.EqualTo(expectedMetadataReads));
                    Assert.That(metadataRequests.Select(request => request.Method), Is.All.EqualTo(nameof(IProvideBookInfo.GetEditionInfo)));
                    Assert.That(metadataRequests.Select(request => request.Id), Is.All.EqualTo(sourceEditionId));
                    Assert.That(metadataRequests.Select(request => request.Media), Is.All.EqualTo(BookMediaType.Audiobook));
                    Assert.That(pendingCalls, Is.Empty);
                    Assert.That(commandCalls, Is.Empty);
                    var expectedOwner = scenario == "changed-owner" ? other.HardcoverAuthorId :
                        expectedMetadataReads == 0 || scenario == "wrong-work" || returnsPrimary ? primary.HardcoverAuthorId : null;
                    Assert.That(reports.Select(item => item.AuthorProviderId), Is.All.EqualTo(expectedOwner));
                    Assert.That(report.EditionHasNarration, Is.EqualTo(expectedMetadataReads == 0 || returnsPrimary ? (bool?)true : null));
                    Assert.That(report.EditionNarratorNames ?? new List<string>(),
                        Is.EqualTo(expectedMetadataReads == 0 || returnsPrimary ? new[] { "Stephen Fry" } : Array.Empty<string>()));
                    Assert.That(editionRows.All().ToDictionary(edition => edition.Id, edition => (edition.BookId, edition.Monitored, edition.ManualAdd)), Is.EquivalentTo(beforePins));
                    Assert.That(bookRows.All().ToDictionary(book => book.Id,
                        book => (book.AuthorId, book.MediaType, book.AnyEditionOk, book.AudiobookMonitored, book.EbookMonitored)), Is.EquivalentTo(beforeBooks));
                });
                TestContext.WriteLine("{0}: {1}, metadata reads {2}, current pins unchanged", postgres ? "PostgreSQL" : "SQLite", scenario, metadataRequests.Count);
            });
        }
    }
}
