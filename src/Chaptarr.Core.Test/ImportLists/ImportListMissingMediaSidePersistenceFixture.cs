using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Chaptarr.Core.Test.Books;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using Newtonsoft.Json;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Queue;
using NzbDrone.Core.RootFolders;
using static Chaptarr.Core.Test.Common.InterfaceProxy;
using static Chaptarr.Core.Test.Books.AuthorLibraryServiceSpecificBookMonitoringFixture;

namespace Chaptarr.Core.Test.ImportLists
{
    [TestFixture]
    [NonParallelizable]
    public partial class ImportListMissingMediaSidePersistenceFixture
    {
        [TestCase(false)]
        [TestCase(true)]
        public void configured_audio_side_should_copy_a_requested_narration_with_add_off(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var library = new StoredListLibrary(database);
                var source = CatalogBook(BookMediaType.Audiobook, "Listed narration", "123");
                var selected = source.Editions.Single();
                selected.Narrator = "Jim Dale";
                selected.Monitored = true;
                selected.ManualAdd = true;
                source.SetMonitored(true);
                var requested = new Edition
                {
                    Title = source.Title, ForeignEditionId = "gr:456", GoodreadsEditionId = 456,
                    ReadingFormatId = 2, Narrator = "Stephen Fry"
                };
                source.Editions.Add(requested);
                library.Save(source);
                var report = library.Report(source, 0);
                report.EditionProviderId = "gr:456";
                report.EditionHasNarration = true;
                report.EditionNarratorNames = new() { "Stephen Fry" };

                library.Sync(source, BookMediaType.Audiobook, new() { report });

                var saved = library.Books.GetBooksByAuthor(library.Author.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(saved, Has.Count.EqualTo(2));
                    Assert.That(saved.All(book => book.MediaType == BookMediaType.Audiobook), Is.True);
                    Assert.That(library.Books.GetBook(source.Id).Editions.Single(edition => edition.Monitored).Narrator, Is.EqualTo("Jim Dale"));
                    var copy = saved.Single(book => book.Id != source.Id);
                    Assert.That(copy.Editions.Single(edition => edition.Monitored).Narrator, Is.EqualTo("Stephen Fry"));
                    Assert.That(copy.IsMonitored(), Is.True);
                    Assert.That(library.Commands.OfType<BookSearchCommand>().Single().BookIds, Is.EqualTo(new[] { copy.Id }));
                    Assert.That(library.Pending.GetAll(), Is.Empty);
                    Assert.That(library.Authors.All(), Has.Count.EqualTo(1));
                    Assert.That(library.Authors.Get(library.Author.Id).AudiobookMonitored, Is.False);
                });
            });
        }

        [TestCase(false, BookMediaType.Audiobook, false)]
        [TestCase(false, BookMediaType.Audiobook, true)]
        [TestCase(false, BookMediaType.Ebook, false)]
        [TestCase(false, BookMediaType.Ebook, true)]
        [TestCase(true, BookMediaType.Audiobook, false)]
        [TestCase(true, BookMediaType.Audiobook, true)]
        [TestCase(true, BookMediaType.Ebook, false)]
        [TestCase(true, BookMediaType.Ebook, true)]
        [TestCase(false, BookMediaType.Ebook, true, true)]
        [TestCase(true, BookMediaType.Ebook, true, true)]
        [TestCase(false, BookMediaType.Audiobook, false, false, false)]
        [TestCase(false, BookMediaType.Audiobook, true, false, false)]
        [TestCase(false, BookMediaType.Ebook, false, false, false)]
        [TestCase(false, BookMediaType.Ebook, true, false, false)]
        [TestCase(true, BookMediaType.Audiobook, false, false, false)]
        [TestCase(true, BookMediaType.Audiobook, true, false, false)]
        [TestCase(true, BookMediaType.Ebook, false, false, false)]
        [TestCase(true, BookMediaType.Ebook, true, false, false)]
        public void list_missing_side_intent_should_survive_pending_storage_and_actual_author_library(bool postgres,
            BookMediaType side, bool monitored, bool existingSearch = false, bool addMissing = true)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var logger = LogManager.GetCurrentClassLogger();
                var events = Proxy<IEventAggregator>((method, args) => null);
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
                    Name = "Existing Author", Overview = string.Empty, CleanName = "existingauthor", SortName = "existingauthor",
                    SortNameLastFirst = "Author, Existing", TitleSlug = "existing-author", Path = @"C:\Existing\Existing Author".AsOsAgnostic(),
                    Added = DateTime.UtcNow, GoodreadsAuthorId = "gr:562254",
                    AddOptions = existingSearch ? new AddAuthorOptions { SearchForMissingBooks = true } : null
                };
                if (side == BookMediaType.Audiobook)
                {
                    author.EbookQualityProfileId = 9;
                    author.EbookMetadataProfileId = 8;
                    author.EbookRootFolderPath = @"C:\ExistingEbooks".AsOsAgnostic();
                    author.EbookMonitored = true;
                    author.EbookMonitorNewItems = NewItemMonitorTypes.None;
                    author.EbookTags = new HashSet<int> { 7 };
                }
                else
                {
                    author.AudiobookQualityProfileId = 9;
                    author.AudiobookMetadataProfileId = 8;
                    author.AudiobookRootFolderPath = @"C:\ExistingAudio".AsOsAgnostic();
                    author.AudiobookMonitored = true;
                    author.AudiobookMonitorNewItems = NewItemMonitorTypes.None;
                    author.AudiobookTags = new HashSet<int> { 7 };
                }
                authors.Insert(author);
                var otherSide = side == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook;
                var other = CatalogBook(otherSide, "Selected Work", "123");
                other.AuthorId = author.Id;
                other.Author = author;
                other.SetMonitored(true);
                var otherEditions = other.Editions;
                bookRows.Insert(other);
                otherEditions.ForEach(edition => { edition.BookId = other.Id; edition.Monitored = true; });
                editions.InsertMany(otherEditions);
                var otherEditionId = otherEditions.Single().Id;
                var requests = new PendingAuthorImportRepository(database, events);
                var pending = new PendingAuthorImportService(requests, authorService, events, logger, aliases);
                ImportListSyncServiceSearchOnAddFixture.BuildMissingMediaSideService(author, other, side, monitored,
                    authorService, books, editions, pending, roots, queue, addMissingAuthors: addMissing)
                    .Execute(new NzbDrone.Core.ImportLists.ImportListSyncCommand(1));

                var storedRequest = pending.GetByProviderId(author.GoodreadsAuthorId);
                if (!addMissing)
                {
                    Assert.Multiple(() =>
                    {
                        Assert.That(storedRequest, Is.Null, "Missing formats require explicit list permission.");
                        Assert.That(authors.All(), Has.Count.EqualTo(1));
                        Assert.That(books.GetBooksByAuthor(author.Id), Has.Count.EqualTo(1));
                        Assert.That(commands, Is.Empty);
                        var saved = authors.Get(author.Id);
                        Assert.That(side == BookMediaType.Audiobook ? saved.AudiobookQualityProfileId : saved.EbookQualityProfileId, Is.Null);
                        Assert.That(side == BookMediaType.Audiobook ? saved.AudiobookMetadataProfileId : saved.EbookMetadataProfileId, Is.Null);
                        Assert.That(side == BookMediaType.Audiobook ? saved.AudiobookRootFolderPath : saved.EbookRootFolderPath, Is.Null);
                    });
                    return;
                }

                Assert.That(storedRequest, Is.Not.Null, "An enabled list must persist its missing-format request.");
                Assert.That(storedRequest.HasAudiobook(), Is.EqualTo(side == BookMediaType.Audiobook));
                Assert.That(storedRequest.HasEbook(), Is.EqualTo(side == BookMediaType.Ebook));
                storedRequest.NextAttemptAt = DateTime.UtcNow.AddMinutes(-1);
                requests.Update(storedRequest);
                var remote = new Author
                {
                    Name = author.Name, GoodreadsAuthorId = author.GoodreadsAuthorId,
                    Books = new List<Book> { CatalogBook(side, "Selected Work", "123"), CatalogBook(side, "Other Work", "124"), CatalogBook(otherSide, "Selected Work", "123") },
                    Series = new List<Series>()
                };
                var profiles = new StubMetadataProfileService();
                var library = new AuthorLibraryService(authorService, new StubAuthorInfo(remote), books, null, editions,
                    null, profiles, new TestQualityProfileService(), paths, roots, queue, events, pending, database,
                    new ImportListExclusionService(new ImportListExclusionRepository(database, events), logger),
                    new EditionMetadataProfileFilter(new TestTermMatcherService()), null, logger, new EditionSelector(logger));
                new ProcessPendingImportsCommandHandler(pending, library, books,
                    Proxy<IProvideBookInfo>((method, args) => throw new AssertionException("A complete author blob must not require a direct work rescue.")),
                    queue, events, logger, aliases).Execute(new ProcessPendingImportsCommand());

                var remaining = pending.GetByProviderId(author.GoodreadsAuthorId);
                Assert.That(remaining, Is.Null, remaining?.LastError);
                var savedAuthor = authors.Get(author.Id);
                var savedBooks = books.GetBooksByAuthor(author.Id);
                var selected = savedBooks.Single(book => book.MediaType == side && book.GoodreadsWorkId == "gr:123");
                var unselected = savedBooks.Single(book => book.MediaType == side && book.GoodreadsWorkId == "gr:124");
                var unchanged = savedBooks.Single(book => book.MediaType == otherSide);
                Assert.Multiple(() =>
                {
                    Assert.That(savedBooks.Count, Is.EqualTo(3));
                    Assert.That(selected.IsMonitored(), Is.True);
                    Assert.That(unselected.IsMonitored(), Is.False);
                    Assert.That(BookEditionIdentity.GetMonitoredEdition(selected), Is.Not.Null);
                    Assert.That(side == BookMediaType.Audiobook ? savedAuthor.AudiobookQualityProfileId : savedAuthor.EbookQualityProfileId, Is.EqualTo(side == BookMediaType.Audiobook ? 3 : 5));
                    Assert.That(side == BookMediaType.Audiobook ? savedAuthor.AudiobookMetadataProfileId : savedAuthor.EbookMetadataProfileId, Is.EqualTo(side == BookMediaType.Audiobook ? 4 : 6));
                    Assert.That(side == BookMediaType.Audiobook ? savedAuthor.AudiobookRootFolderPath : savedAuthor.EbookRootFolderPath, Is.EqualTo(side == BookMediaType.Audiobook ? @"C:\Audio".AsOsAgnostic() : @"C:\Ebooks".AsOsAgnostic()));
                    Assert.That(side == BookMediaType.Audiobook ? savedAuthor.AudiobookMonitored : savedAuthor.EbookMonitored, Is.EqualTo(monitored));
                    Assert.That(side == BookMediaType.Audiobook ? savedAuthor.AudiobookMonitorNewItems : savedAuthor.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.New));
                    Assert.That(side == BookMediaType.Audiobook ? savedAuthor.EbookQualityProfileId : savedAuthor.AudiobookQualityProfileId, Is.EqualTo(9));
                    Assert.That(side == BookMediaType.Audiobook ? savedAuthor.EbookMetadataProfileId : savedAuthor.AudiobookMetadataProfileId, Is.EqualTo(8));
                    Assert.That(side == BookMediaType.Audiobook ? savedAuthor.EbookRootFolderPath : savedAuthor.AudiobookRootFolderPath, Is.EqualTo(side == BookMediaType.Audiobook ? @"C:\ExistingEbooks".AsOsAgnostic() : @"C:\ExistingAudio".AsOsAgnostic()));
                    Assert.That(side == BookMediaType.Audiobook ? savedAuthor.EbookMonitored : savedAuthor.AudiobookMonitored, Is.True);
                    Assert.That(side == BookMediaType.Audiobook ? savedAuthor.EbookMonitorNewItems : savedAuthor.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
                    Assert.That(unchanged.Id, Is.EqualTo(other.Id));
                    Assert.That(unchanged.IsMonitored(), Is.True);
                    Assert.That(BookEditionIdentity.GetMonitoredEdition(unchanged)?.Id, Is.EqualTo(otherEditionId));
                    Assert.That(profiles.FilterProfileIds, Does.Contain(side == BookMediaType.Audiobook ? 4 : 6));
                    Assert.That(side == BookMediaType.Audiobook ? savedAuthor.EbookTags : savedAuthor.AudiobookTags, Is.EquivalentTo(new[] { 7 }));
                    Assert.That(commands.OfType<RefreshAuthorCommand>().Single().AuthorId, Is.EqualTo(author.Id));
                });
                var heldAuthorSearch = savedAuthor.AddOptions.SearchForMissingBooks;
                var bookAdded = new BookAddedService(new CacheManager(), queue, books, logger);
                bookAdded.Handle(new BookInfoRefreshedEvent(savedAuthor,
                    savedBooks.Where(book => book.Id != unchanged.Id).ToList(), new List<Book> { unchanged }, new List<Book>()));
                var scanHandler = new AuthorScannedHandler(new BookMonitoredService(authorService, books, logger),
                    authorService, queue, bookAdded, events, logger, books);
                scanHandler.Handle(new AuthorScannedEvent(savedAuthor));
                var afterScan = books.GetBooksByAuthor(author.Id);
                Assert.That(afterScan.Single(book => book.Id == selected.Id).IsMonitored(), Is.True);
                Assert.That(afterScan.Single(book => book.Id == unchanged.Id).IsMonitored(), Is.True);
                Assert.That(afterScan.Single(book => book.Id == unselected.Id).IsMonitored(), Is.False);
                var missingSearches = commands.OfType<MissingBookSearchCommand>().ToList();
                var directSearches = commands.OfType<BookSearchCommand>().ToList();
                var selectedCoveredByHeldSearch = existingSearch && selected.IsMonitoredWithAuthor();
                TestContext.WriteLine($"{database.DatabaseType}/{side}: scanned author searches {missingSearches.Count}; missing targets " + string.Join(",", bookRows.GetMissingBookSearchTargets(null, author.Id).Select(target => target.BookId)));
                Assert.Multiple(() =>
                {
                    Assert.That(missingSearches, Has.Count.EqualTo(existingSearch ? 1 : 0));
                    Assert.That(storedRequest.SearchForMissingBooks, Is.False);
                    Assert.That(heldAuthorSearch, Is.EqualTo(existingSearch));
                    Assert.That(directSearches.SelectMany(command => command.BookIds),
                        Is.EqualTo(selectedCoveredByHeldSearch ? Array.Empty<int>() : new[] { selected.Id }));
                    if (!selectedCoveredByHeldSearch)
                    {
                        Assert.That(directSearches.Single().Trigger, Is.Not.EqualTo(CommandTrigger.Manual));
                    }
                });
                if (existingSearch)
                {
                    Assert.That(missingSearches.Single().AuthorId, Is.EqualTo(author.Id));
                    Assert.That(bookRows.GetMissingBookSearchTargets(null, author.Id).Select(book => book.BookId),
                        Is.EquivalentTo(new[] { selected.Id, unchanged.Id }));
                }
                var searchedBooks = new List<Book>();
                var indexer = Proxy<IIndexer>((method, args) =>
                {
                    if (method.Name == "get_Definition") return new IndexerDefinition { Name = "Fixture", Tags = new HashSet<int>() };
                    Assert.That(method.Name, Is.EqualTo(nameof(IIndexer.Fetch)));
                    searchedBooks.AddRange(((SearchCriteriaBase)args[0]).Books);
                    return Task.FromResult<IList<ReleaseInfo>>(new List<ReleaseInfo>());
                });
                var releaseSearch = new ReleaseSearchService(
                    Proxy<IIndexerFactory>((method, args) =>
                    {
                        Assert.That(method.Name, Is.EqualTo(nameof(IIndexerFactory.AutomaticSearchEnabled)));
                        return new List<IIndexer> { indexer };
                    }), books, authorService, null,
                    Proxy<IMakeDownloadDecision>((method, args) =>
                    {
                        Assert.That(method.Name, Is.EqualTo(nameof(IMakeDownloadDecision.GetSearchDecision)));
                        return new List<DownloadDecision>();
                    }), logger);
                var searchService = new BookSearchService(releaseSearch, books, null,
                    Proxy<IQueueService>((method, args) =>
                    {
                        Assert.That(method.Name, Is.EqualTo(nameof(IQueueService.GetQueue)));
                        return new List<NzbDrone.Core.Queue.Queue>();
                    }),
                    Proxy<IProcessDownloadDecisions>((method, args) =>
                    {
                        Assert.That(method.Name, Is.EqualTo(nameof(IProcessDownloadDecisions.ProcessDecisions)));
                        return Task.FromResult(new ProcessedDecisions(new(), new(), new()));
                    }), logger);
                foreach (var command in commands)
                {
                    if (command is BookSearchCommand exactSearch)
                    {
                        searchService.Execute(exactSearch);
                    }
                    if (command is MissingBookSearchCommand heldSearch)
                    {
                        searchService.Execute(heldSearch);
                    }
                }
                Assert.Multiple(() =>
                {
                    Assert.That(searchedBooks.Count(book => book.Id == selected.Id), Is.EqualTo(monitored ? 1 : 0));
                    Assert.That(searchedBooks.Count(book => book.Id == unchanged.Id), Is.EqualTo(existingSearch ? 1 : 0));
                    Assert.That(searchedBooks.Count(book => book.Id == unselected.Id), Is.Zero);
                    Assert.That(searchedBooks.Select(book => book.Id),
                        Is.EquivalentTo((monitored ? new[] { selected.Id } : Array.Empty<int>())
                            .Concat(existingSearch ? new[] { unchanged.Id } : Array.Empty<int>())));
                    if (monitored) Assert.That(searchedBooks.Single(book => book.Id == selected.Id).MediaType, Is.EqualTo(side));
                    if (existingSearch)
                    {
                        Assert.That(searchedBooks.Single(book => book.Id == unchanged.Id).MediaType, Is.EqualTo(otherSide));
                    }
                });
                TestContext.WriteLine($"{database.DatabaseType} selected local book {selected.Id}/{side}, gate {monitored}: exact searches {commands.OfType<BookSearchCommand>().Count()}, prior author search {existingSearch}, author searches {missingSearches.Count}");
                var commandCount = commands.Count;
                var refreshedAuthor = authors.Get(author.Id);
                bookAdded.Handle(new BookInfoRefreshedEvent(refreshedAuthor, new List<Book>(), afterScan, new List<Book>()));
                scanHandler.Handle(new AuthorScannedEvent(refreshedAuthor));
                Assert.That(commands.Count, Is.EqualTo(commandCount));
                ImportListSyncServiceSearchOnAddFixture.BuildMissingMediaSideService(savedAuthor, unchanged, side, monitored,
                    authorService, books, editions, pending, roots, queue).Execute(new NzbDrone.Core.ImportLists.ImportListSyncCommand(1));
                Assert.That(pending.GetByProviderId(author.GoodreadsAuthorId), Is.Null);
                Assert.That(commands.Count, Is.EqualTo(commandCount));
                Assert.That(books.GetBooksByAuthor(author.Id).Count, Is.EqualTo(3));
            });
        }

        [TestCase(false, BookMediaType.Audiobook)]
        [TestCase(true, BookMediaType.Audiobook)]
        [TestCase(false, BookMediaType.Ebook)]
        [TestCase(true, BookMediaType.Ebook)]
        [TestCase(false, BookMediaType.Audiobook, true)]
        [TestCase(true, BookMediaType.Audiobook, true)]
        [TestCase(false, BookMediaType.Audiobook, false, true)]
        [TestCase(true, BookMediaType.Audiobook, false, true)]
        [TestCase(false, BookMediaType.Audiobook, false, false, true)]
        [TestCase(false, BookMediaType.Ebook, false, false, true)]
        [TestCase(true, BookMediaType.Audiobook, false, false, true)]
        [TestCase(true, BookMediaType.Ebook, false, false, true)]
        public void saved_list_aggregate_should_batch_searches_and_retry_a_rejected_batch(bool postgres, BookMediaType side, bool heldAuthorSearch = false, bool alreadyFulfilled = false, bool scanBeforeRetry = false)
        {
            const bool monitored = true;
            var existingSearch = heldAuthorSearch;
            DualDatabaseFixture.Run(postgres, database =>
            {
                var logger = LogManager.GetCurrentClassLogger();
                var events = Proxy<IEventAggregator>((method, args) => null);
                var commands = new List<Command>();
                var attemptedSearches = new List<BookSearchCommand>();
                var queue = Proxy<IManageCommandQueue>((method, args) =>
                {
                    ((Command)args[0]).Trigger = (CommandTrigger)args[2];
                    if (args[0] is BookSearchCommand search)
                    {
                        attemptedSearches.Add(search);
                        if (attemptedSearches.Count == 1) throw new InvalidOperationException("Simulated queue rejection");
                    }
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
                    Name = "Existing Author", Overview = string.Empty, CleanName = "existingauthor", SortName = "existingauthor",
                    SortNameLastFirst = "Author, Existing", TitleSlug = "existing-author", Path = @"C:\Existing\Existing Author".AsOsAgnostic(),
                    Added = DateTime.UtcNow, GoodreadsAuthorId = "gr:562254",
                    AddOptions = existingSearch ? new AddAuthorOptions { SearchForMissingBooks = true } : null
                };
                if (side == BookMediaType.Audiobook)
                {
                    author.EbookQualityProfileId = 9;
                    author.EbookMetadataProfileId = 8;
                    author.EbookRootFolderPath = @"C:\ExistingEbooks".AsOsAgnostic();
                    author.EbookMonitored = true;
                    author.EbookMonitorNewItems = NewItemMonitorTypes.None;
                    author.EbookTags = new HashSet<int> { 7 };
                }
                else
                {
                    author.AudiobookQualityProfileId = 9;
                    author.AudiobookMetadataProfileId = 8;
                    author.AudiobookRootFolderPath = @"C:\ExistingAudio".AsOsAgnostic();
                    author.AudiobookMonitored = true;
                    author.AudiobookMonitorNewItems = NewItemMonitorTypes.None;
                    author.AudiobookTags = new HashSet<int> { 7 };
                }
                if (side == BookMediaType.Audiobook) author.AudiobookMonitored = false;
                else author.EbookMonitored = false;
                authors.Insert(author);
                var otherSide = side == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook;
                var other = CatalogBook(otherSide, "Selected Work", "123");
                other.AuthorId = author.Id;
                other.Author = author;
                other.SetMonitored(true);
                var otherEditions = other.Editions;
                bookRows.Insert(other);
                otherEditions.ForEach(edition => { edition.BookId = other.Id; edition.Monitored = true; });
                editions.InsertMany(otherEditions);
                var otherEditionId = otherEditions.Single().Id;
                var requests = new PendingAuthorImportRepository(database, events);
                var pending = new PendingAuthorImportService(requests, authorService, events, logger, aliases);
                ImportListSyncServiceSearchOnAddFixture.BuildMissingMediaSideService(author, other, side, monitored,
                    authorService, books, editions, pending, roots, queue).Execute(new NzbDrone.Core.ImportLists.ImportListSyncCommand(1));
                var stored = pending.GetByProviderId(author.GoodreadsAuthorId);
                var requestedIds = alreadyFulfilled ? new[] { "gr:123" } : new[] { "gr:123", "gr:125" };
                stored.SearchForMissingBooks = true;
                if (side == BookMediaType.Audiobook)
                {
                    stored.AudiobookBooksToMonitor = JsonConvert.SerializeObject(requestedIds);
                    stored.AudiobookBooksToSearch = null;
                    stored.AudiobookNarrations = JsonConvert.SerializeObject(requestedIds.Select(id => new RequestedNarration
                    {
                        WorkProviderId = id, EditionProviderId = "hc:edition:" + id.Substring(3) + "1",
                        NarratorNames = new List<string> { "Fixture Narrator" }, Search = false
                    }));
                }
                else
                {
                    stored.EbookBooksToMonitor = JsonConvert.SerializeObject(requestedIds);
                    stored.EbookBooksToSearch = null;
                }
                stored.NextAttemptAt = DateTime.UtcNow.AddMinutes(-1);
                requests.Update(stored);
                int? fulfilledPin = null;
                if (alreadyFulfilled)
                {
                    var fulfilled = CatalogBook(side, "Selected Work", "123");
                    fulfilled.AuthorId = author.Id;
                    fulfilled.Author = author;
                    fulfilled.AnyEditionOk = false;
                    fulfilled.AddOptions = new AddBookOptions { AddType = BookAddType.Manual };
                    fulfilled.SetMonitored(true);
                    var fulfilledEditions = fulfilled.Editions;
                    bookRows.Insert(fulfilled);
                    fulfilledEditions.ForEach(edition => { edition.BookId = fulfilled.Id; edition.Monitored = true; edition.ManualAdd = true; });
                    editions.InsertMany(fulfilledEditions);
                    fulfilledPin = fulfilledEditions.Single().Id;
                }

                void Drain(IPendingAuthorImportService persistedPending)
                {
                    var remote = new Author
                    {
                        Name = author.Name, GoodreadsAuthorId = author.GoodreadsAuthorId,
                        Books = new List<Book> { CatalogBook(side, "Selected Work", "123"), CatalogBook(side, "Second Listed Work", "125"),
                            CatalogBook(side, "Other Work", "124"), CatalogBook(otherSide, "Selected Work", "123") },
                        Series = new List<Series>()
                    };
                    var library = new AuthorLibraryService(authorService, new StubAuthorInfo(remote), books, null, editions,
                        null, new StubMetadataProfileService(), new TestQualityProfileService(), paths, roots, queue, events,
                        persistedPending, database, new ImportListExclusionService(new ImportListExclusionRepository(database, events), logger),
                        new EditionMetadataProfileFilter(new TestTermMatcherService()), null, logger, new EditionSelector(logger));
                    new ProcessPendingImportsCommandHandler(persistedPending, library, books,
                        Proxy<IProvideBookInfo>((method, args) => throw new AssertionException("Complete author metadata must not use a work rescue.")),
                        queue, events, logger, aliases).Execute(new ProcessPendingImportsCommand());
                }

                Drain(pending);
                if (alreadyFulfilled)
                {
                    Assert.That(pending.GetByProviderId(author.GoodreadsAuthorId), Is.Null);
                    var fulfilled = books.GetBooksByAuthor(author.Id).Single(book => book.MediaType == side);
                    Assert.That(BookEditionIdentity.GetMonitoredEdition(fulfilled)?.Id, Is.EqualTo(fulfilledPin));
                    Assert.That(attemptedSearches, Is.Empty, "The wanted writer must suppress searches for an already fulfilled narration.");
                    new AuthorScannedHandler(new BookMonitoredService(authorService, books, logger), authorService, queue,
                        new BookAddedService(new CacheManager(), queue, books, logger), events, logger, books).Handle(new AuthorScannedEvent(authors.Get(author.Id)));
                    Assert.That(commands.OfType<BookSearchCommand>(), Is.Empty);
                    Assert.That(commands.OfType<MissingBookSearchCommand>(), Is.Empty);
                    return;
                }
                var retry = pending.GetByProviderId(author.GoodreadsAuthorId);
                Assert.That(retry, Is.Not.Null, "The rejected search must remain durable for another attempt.");
                var savedBooks = books.GetBooksByAuthor(author.Id);
                var first = savedBooks.Single(book => book.MediaType == side && book.GoodreadsWorkId == "gr:123");
                var second = savedBooks.Single(book => book.MediaType == side && book.GoodreadsWorkId == "gr:125");
                Assert.Multiple(() =>
                {
                    Assert.That(retry.SearchForMissingBooks, Is.False);
                    Assert.That(JsonConvert.DeserializeObject<List<string>>(side == BookMediaType.Audiobook ? retry.AudiobookBooksToSearch : retry.EbookBooksToSearch),
                        Is.EqualTo(new[] { "gr:123", "gr:125" }));
                    Assert.That(attemptedSearches.Single().BookIds, Is.EqualTo(new[] { first.Id, second.Id }));
                    Assert.That(commands.OfType<BookSearchCommand>(), Is.Empty);
                    Assert.That(attemptedSearches.Single().Trigger, Is.Not.EqualTo(CommandTrigger.Manual));
                    Assert.That(first.IsMonitored() && second.IsMonitored(), Is.True);
                });
                if (side == BookMediaType.Audiobook)
                {
                    var narrations = JsonConvert.DeserializeObject<List<RequestedNarration>>(retry.AudiobookNarrations);
                    Assert.That(narrations.Select(narration => narration.WorkProviderId), Is.EquivalentTo(new[] { "gr:123", "gr:125" }));
                    Assert.That(narrations.All(narration => narration.Search), Is.True);
                    Assert.That(narrations.All(narration => narration.NarratorNames.SequenceEqual(new[] { "Fixture Narrator" })), Is.True);
                }

                if (scanBeforeRetry)
                {
                    new AuthorScannedHandler(new BookMonitoredService(authorService, books, logger), authorService, queue,
                        new BookAddedService(new CacheManager(), queue, books, logger), events, logger, books)
                        .Handle(new AuthorScannedEvent(authors.Get(author.Id)));
                    Assert.That(commands.OfType<BookSearchCommand>().Count(), Is.EqualTo(2));
                    Assert.That(commands.OfType<BookSearchCommand>().All(search => search.Trigger != CommandTrigger.Manual), Is.True);
                    var fetched = new List<int>();
                    var runner = RecordingBookSearch.Create(books, authorService, logger,
                        criteria => fetched.AddRange(criteria.Books.Select(book => book.Id)));
                    foreach (var search in commands.OfType<BookSearchCommand>()) runner.Execute(search);
                    Assert.That(fetched, Is.Empty);
                }

                retry.NextAttemptAt = DateTime.UtcNow.AddMinutes(-1);
                requests.Update(retry);
                var restartedPending = new PendingAuthorImportService(requests, authorService, events, logger, aliases);
                Drain(restartedPending);
                Assert.That(restartedPending.GetByProviderId(author.GoodreadsAuthorId), Is.Null);
                var persistedAuthor = authors.Get(author.Id);
                Assert.That(side == BookMediaType.Audiobook ? persistedAuthor.AudiobookMonitored : persistedAuthor.EbookMonitored, Is.False);
                var bookAdded = new BookAddedService(new CacheManager(), queue, books, logger);
                new AuthorScannedHandler(new BookMonitoredService(authorService, books, logger), authorService, queue, bookAdded, events, logger, books)
                    .Handle(new AuthorScannedEvent(persistedAuthor));
                Assert.Multiple(() =>
                {
                    Assert.That(attemptedSearches.SelectMany(search => search.BookIds), Is.EquivalentTo(Enumerable.Repeat(new[] { first.Id, second.Id }, scanBeforeRetry ? 3 : 2).SelectMany(ids => ids)));
                    Assert.That(commands.OfType<BookSearchCommand>().SelectMany(search => search.BookIds), Is.EquivalentTo(Enumerable.Repeat(new[] { first.Id, second.Id }, scanBeforeRetry ? 2 : 1).SelectMany(ids => ids)));
                    Assert.That(commands.OfType<MissingBookSearchCommand>().ToList(), Has.Count.EqualTo(heldAuthorSearch ? 1 : 0));
                    Assert.That(books.GetBooksByAuthor(author.Id), Has.Count.EqualTo(4));
                    Assert.That(books.GetBooksByAuthor(author.Id).Single(book => book.MediaType == otherSide).Id, Is.EqualTo(other.Id));
                    Assert.That(BookEditionIdentity.GetMonitoredEdition(books.GetBook(other.Id))?.Id, Is.EqualTo(otherEditionId));
                });
                if (heldAuthorSearch)
                {
                    Assert.That(bookRows.GetMissingBookSearchTargets(null, author.Id).Select(target => target.BookId), Is.EqualTo(new[] { other.Id }));
                }
                TestContext.WriteLine($"{database.DatabaseType}/{side}: saved aggregate normalized, one two-book search batch accepted after retry");
            });
        }

        [TestCase(false, BookMediaType.Audiobook, false)]
        [TestCase(true, BookMediaType.Audiobook, false)]
        [TestCase(false, BookMediaType.Ebook, false)]
        [TestCase(true, BookMediaType.Ebook, false)]
        [TestCase(false, BookMediaType.Audiobook, true)]
        [TestCase(true, BookMediaType.Audiobook, true)]
        [TestCase(false, BookMediaType.Ebook, true)]
        [TestCase(true, BookMediaType.Ebook, true)]
        public void generic_present_list_work_should_be_protected_once_without_changing_edition_intent(bool postgres, BookMediaType side, bool pinned)
        {
            var readersByWorkload = new List<List<int>>();
            foreach (var count in new[] { 1, 25 })
            {
                DualDatabaseFixture.Run(postgres, database =>
                {
                    var library = new StoredListLibrary(database);
                    var selected = Enumerable.Range(1, count).Select(index =>
                    {
                        var book = CatalogBook(side, "Requested Work " + index, index.ToString());
                        book.AnyEditionOk = !pinned;
                        book.Editions.Single().ManualAdd = pinned;
                        book.Editions.Single().Monitored = true;
                        book.AddOptions = new AddBookOptions { AddType = BookAddType.Automatic };
                        library.Save(book);
                        return book;
                    }).ToList();
                    var opposite = CatalogBook(side == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook, selected[0].Title, "1");
                    library.Save(opposite);
                    var reports = selected.SelectMany(book => Enumerable.Range(0, 3).Select(intent => library.Report(book, intent))).ToList();
                    var exclusions = new List<ImportListExclusion>
                    {
                        new() { ForeignId = library.Author.GoodreadsAuthorId, MediaType = opposite.MediaType },
                        new() { ForeignId = selected[0].GoodreadsWorkId, MediaType = opposite.MediaType }
                    };
                    for (var run = 0; run < 3; run++) library.Sync(selected[0], side, reports, exclusions: exclusions, search: false);
                    readersByWorkload.Add(library.SyncReaderCounts.ToList());
                    var saved = library.Books.GetBooks(selected.Select(book => book.Id));
                    var untouched = library.Books.GetBook(opposite.Id);
                    TestContext.WriteLine($"{database.DatabaseType}/{side}: {count} works / {reports.Count} generic source reports over 3 syncs, {library.AddOptionsBatches.Count} AddOptions batch, {library.AddOptionsBatches.Sum(batch => batch.Count)} rows; catalog/hydration/provider batches 3/3/3; reader statements [{string.Join(",", library.SyncReaderCounts)}]; elapsed ms [{string.Join(",", library.SyncElapsedMilliseconds)}]; existing edition pins {pinned}");
                    Assert.Multiple(() =>
                    {
                        Assert.That(saved.All(book => book.AddOptions?.AddType == BookAddType.Manual), Is.True);
                        Assert.That(saved.All(book => book.IsMonitored()), Is.True);
                        Assert.That(saved.All(book => book.AnyEditionOk == !pinned), Is.True);
                        Assert.That(saved.All(book => book.Editions.Single().ManualAdd == pinned), Is.True);
                        Assert.That(saved.Select(book => book.Editions.Single().Id), Is.EquivalentTo(selected.Select(book => book.Editions.Single().Id)));
                        Assert.That(untouched.AddOptions?.AddType ?? BookAddType.Automatic, Is.EqualTo(BookAddType.Automatic));
                        Assert.That(untouched.IsMonitored(), Is.False);
                        Assert.That(library.AddOptionsBatches, Has.Count.EqualTo(1));
                        Assert.That(library.AddOptionsBatches.SingleOrDefault() ?? new List<int>(), Is.EquivalentTo(selected.Select(book => book.Id)));
                        Assert.That(library.Calls.GetValueOrDefault(nameof(IBookService.GetImportListBookIdentities)), Is.EqualTo(3));
                        Assert.That(library.Calls.GetValueOrDefault(nameof(IBookService.GetExistingBooks)), Is.EqualTo(3));
                        Assert.That(library.Calls.GetValueOrDefault(nameof(IBookService.FindBookIdsByProviderIds)), Is.EqualTo(3));
                        Assert.That(library.Calls.GetValueOrDefault(nameof(IBookService.MonitorRequestedBooks)), Is.EqualTo(1));
                        Assert.That(library.SyncReaderCounts[2], Is.EqualTo(library.SyncReaderCounts[1]));
                        Assert.That(library.MetadataReads, Is.Zero, "Stored same-media work ownership must need no remote proof.");
                        Assert.That(library.Pending.GetByProviderId(library.Author.GoodreadsAuthorId), Is.Null);
                        Assert.That(library.Commands.OfType<BookSearchCommand>(), Is.Empty);
                    });
                });
            }
            Assert.That(readersByWorkload[1], Is.EqualTo(readersByWorkload[0]),
                "Reader queries must stay at the sync batch boundary as work requests grow from 1 to 25.");
        }

        [TestCase(false, BookMediaType.Audiobook)]
        [TestCase(true, BookMediaType.Audiobook)]
        [TestCase(false, BookMediaType.Ebook)]
        [TestCase(true, BookMediaType.Ebook)]
        public void missing_generic_work_should_queue_once_then_preserve_exact_work_and_author_settings(bool postgres, BookMediaType side)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var library = new StoredListLibrary(database);
                var other = CatalogBook(side == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook, "Requested Work", "123");
                library.Save(other);
                var unrelated = CatalogBook(side, "Unrequested Work", "125");
                library.Save(unrelated);
                var first = CatalogBook(side, "Requested Work", "123");
                var second = CatalogBook(side, "Print Source Work", "124");
                var reports = new List<NzbDrone.Core.Parser.Model.ImportListItemInfo> { library.Report(first, 1), library.Report(second, 2) };
                library.Sync(other, side, reports);
                var request = library.Pending.GetByProviderId(library.Author.GoodreadsAuthorId);
                Assert.That(request, Is.Not.Null, "Keep Existing must queue absent requested works for an already configured author side.");
                Assert.That(library.MetadataReads, Is.EqualTo(2), "Each missing source needs one ownership proof.");
                Assert.Multiple(() =>
                {
                    Assert.That(library.Commands.OfType<ProcessPendingImportsCommand>().ToList(), Has.Count.EqualTo(1));
                    Assert.That(request.HasAudiobook(), Is.EqualTo(side == BookMediaType.Audiobook));
                    Assert.That(request.HasEbook(), Is.EqualTo(side == BookMediaType.Ebook));
                    Assert.That(request.AudiobookMonitorExistingMode, Is.Null);
                    Assert.That(request.EbookMonitorExistingMode, Is.Null);
                    Assert.That(JsonConvert.DeserializeObject<List<string>>(side == BookMediaType.Audiobook ? request.AudiobookBooksToMonitor : request.EbookBooksToMonitor), Is.EquivalentTo(new[] { "gr:123", "gr:124" }));
                    Assert.That(request.AudiobookNarrations, Is.Null.Or.Empty.Or.EqualTo("[]"));
                    Assert.That(side == BookMediaType.Audiobook ? request.AudiobookQualityProfileId : request.EbookQualityProfileId, Is.EqualTo(9));
                    Assert.That(side == BookMediaType.Audiobook ? request.AudiobookMetadataProfileId : request.EbookMetadataProfileId, Is.EqualTo(8));
                    Assert.That(request.SearchForMissingBooks, Is.False);
                });
                library.Process(request, new Author
                {
                    Name = library.Author.Name, GoodreadsAuthorId = library.Author.GoodreadsAuthorId,
                    Books = new List<Book> { first, second, CatalogBook(side, "Unrequested Work", "125"), CatalogBook(other.MediaType, other.Title, "123") }, Series = new List<Series>()
                });
                var remaining = library.Pending.GetByProviderId(library.Author.GoodreadsAuthorId);
                Assert.That(remaining, Is.Null, remaining?.LastError);
                var saved = library.Books.GetBooksByAuthor(library.Author.Id);
                var requested = saved.Where(book => book.MediaType == side && book.GoodreadsWorkId != "gr:125").ToList();
                var author = library.Authors.Get(library.Author.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(requested, Has.Count.EqualTo(2));
                    Assert.That(requested.All(book => book.IsMonitored() && book.AddOptions?.AddType == BookAddType.Manual), Is.True);
                    Assert.That(requested.All(book => book.AnyEditionOk && book.Editions.All(edition => !edition.ManualAdd)), Is.True);
                    Assert.That(saved.Single(book => book.Id == unrelated.Id).IsMonitored(), Is.False);
                    Assert.That(saved.Single(book => book.Id == other.Id).IsMonitored(), Is.False);
                    Assert.That(author.AudiobookMonitored, Is.False);
                    Assert.That(author.EbookMonitored, Is.False);
                    Assert.That(author.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
                    Assert.That(author.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
                    Assert.That(author.AudiobookQualityProfileId, Is.EqualTo(9));
                    Assert.That(author.EbookQualityProfileId, Is.EqualTo(9));
                    Assert.That(author.AudiobookMetadataProfileId, Is.EqualTo(8));
                    Assert.That(author.EbookMetadataProfileId, Is.EqualTo(8));
                    Assert.That(library.Commands.OfType<BookSearchCommand>().SelectMany(command => command.BookIds), Is.EquivalentTo(requested.Select(book => book.Id)));
                    Assert.That(library.Commands.OfType<BookSearchCommand>().Single().Trigger, Is.Not.EqualTo(CommandTrigger.Manual));
                    Assert.That(library.Commands.OfType<MissingBookSearchCommand>(), Is.Empty);
                });
                var writes = library.AddOptionsBatches.Count;
                var commands = library.Commands.Count;
                library.Sync(other, side, reports);
                library.Sync(other, side, reports);
                Assert.That(library.AddOptionsBatches.Count, Is.EqualTo(writes));
                Assert.That(library.Commands.Count, Is.EqualTo(commands));
                Assert.That(library.MetadataReads, Is.EqualTo(2), "Stored work ownership must not repeat metadata proof.");
                Assert.That(library.Pending.GetByProviderId(library.Author.GoodreadsAuthorId), Is.Null);
                TestContext.WriteLine($"{database.DatabaseType}/{side}: 2 missing generic works, 1 grouped queue; automatic paused targets {requested.Count}; two repeated syncs add no writes or searches.");
            });
        }

        [TestCase(false, BookMediaType.Audiobook, false, false)]
        [TestCase(true, BookMediaType.Audiobook, false, false)]
        [TestCase(false, BookMediaType.Ebook, false, false)]
        [TestCase(true, BookMediaType.Ebook, false, false)]
        [TestCase(false, BookMediaType.Audiobook, true, false)]
        [TestCase(true, BookMediaType.Audiobook, true, false)]
        [TestCase(false, BookMediaType.Ebook, true, true)]
        [TestCase(true, BookMediaType.Ebook, true, true)]
        public void generic_keep_off_and_same_side_exclusions_should_not_protect_or_queue_work(bool postgres, BookMediaType side, bool keep, bool excludeAuthor)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var library = new StoredListLibrary(database);
                var present = CatalogBook(side, "Present Work", "123");
                library.Save(present);
                var missing = CatalogBook(side, "Missing Work", "124");
                var exclusions = keep ? new List<ImportListExclusion>
                {
                    new() { ForeignId = excludeAuthor ? library.Author.GoodreadsAuthorId : present.GoodreadsWorkId, MediaType = side },
                    new() { ForeignId = excludeAuthor ? library.Author.GoodreadsAuthorId : missing.GoodreadsWorkId, MediaType = side }
                } : new List<ImportListExclusion>();
                library.Sync(present, side, new() { library.Report(present, 1), library.Report(missing, 2) }, keep, exclusions, search: false);
                var saved = library.Books.GetBook(present.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(saved.AddOptions?.AddType ?? BookAddType.Automatic, Is.EqualTo(BookAddType.Automatic));
                    Assert.That(saved.IsMonitored(), Is.False);
                    Assert.That(saved.AnyEditionOk, Is.True);
                    Assert.That(saved.Editions.All(edition => !edition.ManualAdd), Is.True);
                    Assert.That(library.AddOptionsBatches, Is.Empty);
                    Assert.That(library.Pending.GetByProviderId(library.Author.GoodreadsAuthorId), Is.Null);
                    Assert.That(library.Commands.OfType<ProcessPendingImportsCommand>(), Is.Empty);
                });
            });
        }

        [TestCase(false, true, false)]
        [TestCase(true, true, false)]
        [TestCase(false, false, false)]
        [TestCase(true, false, false)]
        [TestCase(false, true, true)]
        [TestCase(true, true, true)]
        [TestCase(false, false, true)]
        [TestCase(true, false, true)]
        public void pending_work_protection_should_preserve_non_list_mode_semantics(bool postgres, bool listOrigin, bool initialMode)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var library = new StoredListLibrary(database);
                var book = CatalogBook(BookMediaType.Audiobook, "Requested Work", "123");
                book.Editions.Single().Monitored = true;
                library.Save(book);
                var other = CatalogBook(BookMediaType.Ebook, book.Title, "123");
                library.Save(other);
                for (var run = 0; run < 2; run++)
                {
                    var config = new MonitoringConfig
                    {
                        CreateAudiobook = true, CreateEbook = false,
                        AudiobookBooksToMonitor = new() { book.GoodreadsWorkId, book.GoodreadsWorkId },
                        AudiobookMonitorExistingMode = initialMode ? MonitorTypes.None : null,
                        RequestedBy = listOrigin ? "ImportList:Generic work" : "Native work request"
                    };
                    library.Pending.EnqueueAsync(library.Author.GoodreadsAuthorId, config, config.RequestedBy).GetAwaiter().GetResult();
                    var request = library.Pending.GetByProviderId(library.Author.GoodreadsAuthorId);
                    request.NextAttemptAt = DateTime.UtcNow.AddMinutes(-1);
                    library.Requests.Update(request);
                    var handler = new ProcessPendingImportsCommandHandler(library.Pending,
                        Proxy<IAuthorLibraryService>((method, args) => System.Threading.Tasks.Task.FromResult(library.Author)), library.Books,
                        Proxy<IProvideBookInfo>((method, args) => throw new AssertionException("The stored requested work is already available.")),
                        library.Queue, library.Events, library.Logger, library.Aliases);
                    handler.Execute(new ProcessPendingImportsCommand());
                    Assert.That(library.Pending.GetByProviderId(library.Author.GoodreadsAuthorId), Is.Null);
                }
                var saved = library.Books.GetBook(book.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(saved.AddOptions?.AddType ?? BookAddType.Automatic, Is.EqualTo(listOrigin || initialMode ? BookAddType.Manual : BookAddType.Automatic));
                    Assert.That(saved.IsMonitored(), Is.True);
                    Assert.That(saved.AnyEditionOk, Is.True);
                    Assert.That(saved.Editions.All(edition => !edition.ManualAdd), Is.True);
                    Assert.That(library.Books.GetBook(other.Id).AddOptions?.AddType ?? BookAddType.Automatic, Is.EqualTo(BookAddType.Automatic));
                    Assert.That(library.AddOptionsBatches, Has.Count.EqualTo(listOrigin || initialMode ? 1 : 0));
                    Assert.That(library.Authors.Get(library.Author.Id).AudiobookMonitored, Is.False);
                });
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void first_media_side_setup_should_merge_missing_generic_work_on_the_configured_side(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var library = new StoredListLibrary(database);
                library.Author.EbookQualityProfileId = null;
                library.Author.EbookMetadataProfileId = null;
                library.Author.EbookRootFolderPath = null;
                library.Author.EbookMonitored = null;
                library.Author.EbookMonitorNewItems = null;
                library.Authors.Update(library.Author);
                var unrelated = CatalogBook(BookMediaType.Audiobook, "Unrequested Work", "125");
                library.Save(unrelated);
                var audio = CatalogBook(BookMediaType.Audiobook, "Requested Work", "123");
                var reports = new List<NzbDrone.Core.Parser.Model.ImportListItemInfo> { library.Report(audio, 1) };
                ImportListSyncServiceSearchOnAddFixture.BuildMissingMediaSideService(library.Author, audio, BookMediaType.Audiobook,
                    false, library.AuthorService, library.Books, library.Editions, library.Pending, library.Roots, library.Queue,
                    bothSides: true, reports: reports,
                    metadataLookup: (_, _) => System.Threading.Interlocked.Increment(ref library.MetadataReads)).Execute(new NzbDrone.Core.ImportLists.ImportListSyncCommand(1));
                Assert.That(library.MetadataReads, Is.EqualTo(1));
                var request = library.Pending.GetByProviderId(library.Author.GoodreadsAuthorId);
                Assert.That(request, Is.Not.Null);
                Assert.Multiple(() =>
                {
                    Assert.That(request.HasAudiobook(), Is.True, "The configured side's absent work must join the first-time side request.");
                    Assert.That(request.HasEbook(), Is.True);
                    Assert.That(library.Pending.GetAll(), Has.Count.EqualTo(1));
                    Assert.That(library.Commands.OfType<ProcessPendingImportsCommand>().ToList(), Has.Count.EqualTo(1));
                    Assert.That(request.AudiobookQualityProfileId, Is.EqualTo(9));
                    Assert.That(request.AudiobookMetadataProfileId, Is.EqualTo(8));
                    Assert.That(request.EbookQualityProfileId, Is.EqualTo(5));
                    Assert.That(request.EbookMetadataProfileId, Is.EqualTo(6));
                    Assert.That(request.AudiobookMonitorExistingMode, Is.Null);
                    Assert.That(JsonConvert.DeserializeObject<List<string>>(request.AudiobookBooksToMonitor ?? "[]"), Is.EqualTo(new[] { "gr:123" }));
                    Assert.That(JsonConvert.DeserializeObject<List<string>>(request.EbookBooksToMonitor ?? "[]"), Is.EqualTo(new[] { "gr:123" }));
                    Assert.That(request.AudiobookNarrations, Is.Null.Or.Empty.Or.EqualTo("[]"));
                });
                library.Process(request, new Author
                {
                    Name = library.Author.Name, GoodreadsAuthorId = library.Author.GoodreadsAuthorId,
                    Books = new List<Book> { audio, CatalogBook(BookMediaType.Ebook, audio.Title, "123"), CatalogBook(BookMediaType.Audiobook, unrelated.Title, "125") },
                    Series = new List<Series>()
                });
                var remaining = library.Pending.GetByProviderId(library.Author.GoodreadsAuthorId);
                Assert.That(remaining, Is.Null, remaining?.LastError);
                var author = library.Authors.Get(library.Author.Id);
                var books = library.Books.GetBooksByAuthor(author.Id);
                var selected = books.Where(book => book.GoodreadsWorkId == "gr:123").ToList();
                Assert.Multiple(() =>
                {
                    Assert.That(selected, Has.Count.EqualTo(2));
                    Assert.That(selected.All(book => book.AddOptions?.AddType == BookAddType.Manual && book.IsMonitored()), Is.True);
                    Assert.That(selected.All(book => book.AnyEditionOk && book.Editions.All(edition => !edition.ManualAdd)), Is.True);
                    Assert.That(books.Single(book => book.Id == unrelated.Id).IsMonitored(), Is.False);
                    Assert.That(author.AudiobookMonitored, Is.False);
                    Assert.That(author.EbookMonitored, Is.False);
                    Assert.That(author.AudiobookQualityProfileId, Is.EqualTo(9));
                    Assert.That(author.AudiobookMetadataProfileId, Is.EqualTo(8));
                    Assert.That(author.AudiobookRootFolderPath, Is.EqualTo(@"C:\ExistingAudio".AsOsAgnostic()));
                    Assert.That(author.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
                    Assert.That(author.EbookQualityProfileId, Is.EqualTo(5));
                    Assert.That(author.EbookMetadataProfileId, Is.EqualTo(6));
                    Assert.That(author.EbookRootFolderPath, Is.EqualTo(@"C:\Ebooks".AsOsAgnostic()));
                    Assert.That(author.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.New));
                    Assert.That(library.Commands.OfType<BookSearchCommand>().SelectMany(command => command.BookIds), Is.EquivalentTo(selected.Select(book => book.Id)));
                });
                TestContext.WriteLine($"{database.DatabaseType}: first ebook-side defaults and configured audiobook-side work request merged into one pending row; both selected works remain automatic editions.");
            });
        }

        [TestCase(false, BookMediaType.Audiobook, false)]
        [TestCase(true, BookMediaType.Audiobook, false)]
        [TestCase(false, BookMediaType.Ebook, false)]
        [TestCase(true, BookMediaType.Ebook, false)]
        [TestCase(false, BookMediaType.Audiobook, true)]
        [TestCase(true, BookMediaType.Audiobook, true)]
        [TestCase(false, BookMediaType.Ebook, true)]
        [TestCase(true, BookMediaType.Ebook, true)]
        public void two_generic_edition_sources_should_monitor_one_existing_work_pocket_without_an_extra_pin(bool postgres, BookMediaType side, bool pinned)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var library = new StoredListLibrary(database);
                var first = CatalogBook(side, "Requested Work", "123");
                first.Editions.Single().ForeignEditionId = "gr:223";
                first.Editions.Single().Monitored = true;
                library.Save(first);
                var second = CatalogBook(side, "Requested Work", "123");
                second.TitleSlug += "-2";
                second.Editions.Single().ForeignEditionId = "gr:224";
                second.Editions.Single().Monitored = true;
                second.Editions.Single().ManualAdd = pinned;
                second.AnyEditionOk = !pinned;
                second.SetMonitored(pinned);
                library.Save(second);
                var opposite = CatalogBook(side == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook, "Requested Work", "123");
                library.Save(opposite);
                var reports = new List<NzbDrone.Core.Parser.Model.ImportListItemInfo> { library.Report(first, 1), library.Report(second, 1) };
                reports[0].EditionProviderId = "gr:223";
                reports[1].EditionProviderId = "gr:224";
                for (var run = 0; run < 3; run++) library.Sync(first, side, reports, search: false);
                var selectedId = pinned ? second.Id : first.Id;
                var saved = library.Books.GetBooksByAuthor(library.Author.Id);
                var selected = saved.Single(book => book.Id == selectedId);
                var other = saved.Single(book => book.MediaType == side && book.Id != selectedId);
                Assert.Multiple(() =>
                {
                    Assert.That(saved, Has.Count.EqualTo(3));
                    Assert.That(saved.Where(book => book.MediaType == side && book.IsMonitored()).Select(book => book.Id), Is.EqualTo(new[] { selectedId }));
                    Assert.That(selected.AddOptions?.AddType, Is.EqualTo(BookAddType.Manual));
                    Assert.That(other.AddOptions?.AddType ?? BookAddType.Automatic, Is.EqualTo(BookAddType.Automatic));
                    Assert.That(selected.AnyEditionOk, Is.EqualTo(!pinned));
                    Assert.That(selected.Editions.Single().ManualAdd, Is.EqualTo(pinned));
                    Assert.That(other.AnyEditionOk, Is.True);
                    Assert.That(other.Editions.Single().ManualAdd, Is.False);
                    Assert.That(saved.SelectMany(book => book.Editions).Select(edition => edition.Id), Is.EquivalentTo(new[] { first.Editions.Single().Id, second.Editions.Single().Id, opposite.Editions.Single().Id }));
                    Assert.That(saved.Single(book => book.Id == opposite.Id).IsMonitored(), Is.False);
                    Assert.That(library.AddOptionsBatches, Has.Count.EqualTo(1));
                    Assert.That(library.AddOptionsBatches.Single(), Is.EqualTo(new[] { selectedId }));
                    Assert.That(library.Pending.GetByProviderId(library.Author.GoodreadsAuthorId), Is.Null);
                    Assert.That(library.Commands.OfType<BookSearchCommand>(), Is.Empty);
                });
                TestContext.WriteLine($"{database.DatabaseType}/{side}: 2 generic sources with differing automatic edition IDs selected local work row {selectedId} across 3 syncs; other same-work copy remains unmonitored; preserved explicit pin {pinned}.");
            });
        }

        [Test]
        [Combinatorial]
        public void author_only_queue_should_skip_only_satisfied_or_unsupplied_format_requests(
            [Values(false, true)] bool postgres,
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType side,
            [Values(0, 1, 2, 3, 4)] int configuration,
            [Values("ImportList:Format setup", "API")] string source)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var stored = new StoredListLibrary(database);
                if (configuration != 2)
                {
                    if (side == BookMediaType.Audiobook)
                    {
                        stored.Author.AudiobookQualityProfileId = null;
                        stored.Author.AudiobookMetadataProfileId = null;
                        stored.Author.AudiobookRootFolderPath = null;
                    }
                    else
                    {
                        stored.Author.EbookQualityProfileId = null;
                        stored.Author.EbookMetadataProfileId = null;
                        stored.Author.EbookRootFolderPath = null;
                    }
                    stored.Authors.Update(stored.Author);
                }
                var config = configuration == 0 ? new MonitoringConfig() : new MonitoringConfig
                {
                    CreateAudiobook = side == BookMediaType.Audiobook, CreateEbook = side == BookMediaType.Ebook,
                    AudiobookQualityProfileId = side == BookMediaType.Audiobook ? 3 : null,
                    AudiobookMetadataProfileId = side == BookMediaType.Audiobook ? configuration == 3 ? 0 : 4 : null,
                    AudiobookRootFolderPath = side == BookMediaType.Audiobook && configuration != 4 ? @"C:\Audio".AsOsAgnostic() : null,
                    EbookQualityProfileId = side == BookMediaType.Ebook ? 5 : null,
                    EbookMetadataProfileId = side == BookMediaType.Ebook ? configuration == 3 ? 0 : 6 : null,
                    EbookRootFolderPath = side == BookMediaType.Ebook && configuration != 4 ? @"C:\Ebooks".AsOsAgnostic() : null
                };

                var id = stored.Pending.EnqueueAsync(stored.Author.GoodreadsAuthorId, config, source).GetAwaiter().GetResult();
                var repeatId = stored.Pending.EnqueueAsync(stored.Author.GoodreadsAuthorId, config, source).GetAwaiter().GetResult();

                Assert.Multiple(() =>
                {
                    Assert.That(id > 0, Is.EqualTo(configuration == 1));
                    Assert.That(stored.Pending.GetAll(), Has.Count.EqualTo(configuration == 1 ? 1 : 0));
                    Assert.That(repeatId, Is.EqualTo(id));
                    Assert.That(stored.Calls, Is.Empty, "The author-only queue gate must not load book rows.");
                    Assert.That(stored.Commands, Is.Empty);
                    Assert.That(stored.Authors.Get(stored.Author.Id).AddOptions, Is.Null);
                    Assert.That(stored.Authors.All(), Has.Count.EqualTo(1));
                });
            });
        }

        [Test]
        [Combinatorial]
        public void missing_format_without_book_targets_should_use_existing_queue_and_initial_scan(
            [Values(false, true)] bool postgres,
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType side,
            [Values(MonitorTypes.None, MonitorTypes.All, MonitorTypes.Existing)] MonitorTypes initialMode,
            [Values(0, 1, 2)] int partialSettings)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var refreshed = new List<(string Name, int? AudiobookMetadata, int? EbookMetadata)>();
                var stored = new StoredListLibrary(database, message =>
                {
                    if (message is AuthorRefreshCompleteEvent complete)
                        refreshed.Add((complete.Author.Name, complete.Author.AudiobookMetadataProfileId, complete.Author.EbookMetadataProfileId));
                });
                var oppositeSide = side == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook;
                if (side == BookMediaType.Audiobook)
                {
                    stored.Author.AudiobookQualityProfileId = partialSettings == 0 ? null : 3;
                    stored.Author.AudiobookMetadataProfileId = partialSettings == 2 ? 0 : null;
                    stored.Author.AudiobookRootFolderPath = partialSettings == 0 ? null : @"C:\Audio".AsOsAgnostic();
                }
                else
                {
                    stored.Author.EbookQualityProfileId = partialSettings == 0 ? null : 5;
                    stored.Author.EbookMetadataProfileId = partialSettings == 2 ? 0 : null;
                    stored.Author.EbookRootFolderPath = partialSettings == 0 ? null : @"C:\Ebooks".AsOsAgnostic();
                }
                stored.Authors.Update(stored.Author);
                var opposite = CatalogBook(oppositeSide, "Existing format", "123");
                opposite.SetMonitored(true);
                stored.Save(opposite);
                var config = new MonitoringConfig
                {
                    CreateAudiobook = side == BookMediaType.Audiobook, CreateEbook = side == BookMediaType.Ebook,
                    AudiobookQualityProfileId = side == BookMediaType.Audiobook ? 3 : null,
                    AudiobookMetadataProfileId = side == BookMediaType.Audiobook ? 4 : null,
                    AudiobookRootFolderPath = side == BookMediaType.Audiobook ? @"C:\Audio".AsOsAgnostic() : null,
                    AudiobookMonitorExistingMode = side == BookMediaType.Audiobook ? initialMode : null,
                    EbookQualityProfileId = side == BookMediaType.Ebook ? 5 : null,
                    EbookMetadataProfileId = side == BookMediaType.Ebook ? 6 : null,
                    EbookRootFolderPath = side == BookMediaType.Ebook ? @"C:\Ebooks".AsOsAgnostic() : null,
                    EbookMonitorExistingMode = side == BookMediaType.Ebook ? initialMode : null,
                    SearchForMissingBooks = false
                };
                var id = stored.Pending.EnqueueAsync(stored.Author.GoodreadsAuthorId, config, "ImportList:Format setup").GetAwaiter().GetResult();
                Assert.That(id, Is.GreaterThan(0), "A supplied missing-format request must reach the existing queue without fabricated book targets.");
                var request = stored.Pending.GetByProviderId(stored.Author.GoodreadsAuthorId);
                Assert.Multiple(() =>
                {
                    Assert.That(request.AudiobookBooksToMonitor, Is.Null);
                    Assert.That(request.EbookBooksToMonitor, Is.Null);
                    Assert.That(request.AudiobookBooksToSearch, Is.Null);
                    Assert.That(request.EbookBooksToSearch, Is.Null);
                    Assert.That(request.SearchForMissingBooks, Is.False);
                });
                stored.Process(request, new Author
                {
                    Name = "Refreshed Author", GoodreadsAuthorId = stored.Author.GoodreadsAuthorId,
                    Books = new() { CatalogBook(side, "Owned work", "124"), CatalogBook(side, "Unowned work", "125"), CatalogBook(oppositeSide, "Existing format", "123") },
                    Series = new()
                });
                var author = stored.Authors.Get(stored.Author.Id);
                var catalog = stored.Books.GetBooksByAuthor(stored.Author.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(stored.Pending.GetAll(), Is.Empty);
                    Assert.That(refreshed, Is.Not.Empty);
                    Assert.That(refreshed.All(notification => notification.Name == "Refreshed Author" &&
                        (side == BookMediaType.Audiobook ? notification.AudiobookMetadata == 4 : notification.EbookMetadata == 6)), Is.True,
                        "Completion readers must see current metadata and initialized settings.");
                    Assert.That(stored.Commands.OfType<RefreshAuthorCommand>().Select(command => command.AuthorId), Is.EqualTo(new[] { author.Id }),
                        "The normal queued refresh must schedule the scan that consumes the one-time policy.");
                    Assert.That(author.AddOptions, Is.Not.Null);
                    Assert.That(side == BookMediaType.Audiobook ? author.AddOptions?.AudiobookMonitor : author.AddOptions?.EbookMonitor, Is.EqualTo(initialMode));
                    Assert.That(catalog, Has.Count.EqualTo(3));
                    Assert.That(side == BookMediaType.Audiobook ? author.AudiobookQualityProfileId : author.EbookQualityProfileId, Is.EqualTo(side == BookMediaType.Audiobook ? 3 : 5));
                    Assert.That(side == BookMediaType.Audiobook ? author.AudiobookMetadataProfileId : author.EbookMetadataProfileId, Is.EqualTo(side == BookMediaType.Audiobook ? 4 : 6));
                    Assert.That(side == BookMediaType.Audiobook ? author.EbookQualityProfileId : author.AudiobookQualityProfileId, Is.EqualTo(9));
                    Assert.That(side == BookMediaType.Audiobook ? author.EbookMetadataProfileId : author.AudiobookMetadataProfileId, Is.EqualTo(8));
                    Assert.That(side == BookMediaType.Audiobook ? author.EbookRootFolderPath : author.AudiobookRootFolderPath,
                        Is.EqualTo(side == BookMediaType.Audiobook ? @"C:\ExistingEbooks".AsOsAgnostic() : @"C:\ExistingAudio".AsOsAgnostic()));
                });
                var owned = catalog.Single(book => book.MediaType == side && book.GoodreadsWorkId == "gr:124");
                var edition = owned.Editions.First();
                edition.Monitored = true;
                stored.Editions.UpdateMany(new() { edition });
                new MediaFileRepository(database, stored.Events).Insert(new BookFile
                {
                    EditionId = edition.Id,
                    Path = (side == BookMediaType.Audiobook ? @"C:\Books\Initial monitoring fixture.m4b" : @"C:\Books\Initial monitoring fixture.epub").AsOsAgnostic(),
                    MediaType = side == BookMediaType.Audiobook ? "audiobook" : "ebook",
                    Size = 100, DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow,
                    Quality = new NzbDrone.Core.Qualities.QualityModel(), MediaInfo = new NzbDrone.Core.Parser.Model.MediaInfoModel()
                });
                var scanned = new AuthorScannedHandler(new BookMonitoredService(stored.AuthorService, stored.Books, stored.Logger),
                    stored.AuthorService, stored.Queue, Proxy<IBookAddedService>((method, args) => null), stored.Events, stored.Logger, stored.Books);
                scanned.Handle(new AuthorScannedEvent(author));
                Assert.Multiple(() =>
                {
                    Assert.That(stored.Authors.Get(author.Id).AddOptions, Is.Null);
                    Assert.That(stored.Books.GetBook(owned.Id).IsMonitored(), Is.EqualTo(initialMode != MonitorTypes.None));
                    Assert.That(stored.Books.GetBook(catalog.Single(book => book.MediaType == side && book.GoodreadsWorkId == "gr:125").Id).IsMonitored(),
                        Is.EqualTo(initialMode == MonitorTypes.All));
                    Assert.That(stored.Books.GetBook(opposite.Id).IsMonitored(), Is.True);
                    Assert.That(stored.Commands.OfType<BookSearchCommand>(), Is.Empty);
                    Assert.That(stored.Commands.OfType<MissingBookSearchCommand>(), Is.Empty);
                });
                var commandCount = stored.Commands.Count;
                scanned.Handle(new AuthorScannedEvent(stored.Authors.Get(author.Id)));
                Assert.That(stored.Commands.Count, Is.EqualTo(commandCount));
            });
        }

        [TestCase(false, BookMediaType.Audiobook, null, false, false)]
        [TestCase(true, BookMediaType.Audiobook, null, false, false)]
        [TestCase(false, BookMediaType.Audiobook, null, false, true)]
        [TestCase(true, BookMediaType.Audiobook, null, false, true)]
        [TestCase(false, BookMediaType.Audiobook, null, true, false)]
        [TestCase(true, BookMediaType.Audiobook, null, true, false)]
        [TestCase(false, BookMediaType.Audiobook, null, true, true)]
        [TestCase(true, BookMediaType.Audiobook, null, true, true)]
        [TestCase(false, BookMediaType.Ebook, null, false, false)]
        [TestCase(true, BookMediaType.Ebook, null, false, false)]
        [TestCase(false, BookMediaType.Ebook, null, false, true)]
        [TestCase(true, BookMediaType.Ebook, null, false, true)]
        [TestCase(false, BookMediaType.Ebook, null, true, false)]
        [TestCase(true, BookMediaType.Ebook, null, true, false)]
        [TestCase(false, BookMediaType.Ebook, null, true, true)]
        [TestCase(true, BookMediaType.Ebook, null, true, true)]
        [TestCase(false, BookMediaType.Audiobook, MonitorTypes.All, true, false)]
        [TestCase(true, BookMediaType.Audiobook, MonitorTypes.All, true, false)]
        [TestCase(false, BookMediaType.Audiobook, MonitorTypes.None, true, true)]
        [TestCase(true, BookMediaType.Audiobook, MonitorTypes.None, true, true)]
        [TestCase(false, BookMediaType.Audiobook, MonitorTypes.SpecificBook, true, false)]
        [TestCase(true, BookMediaType.Audiobook, MonitorTypes.SpecificBook, true, false)]
        [TestCase(false, BookMediaType.Ebook, MonitorTypes.All, true, false)]
        [TestCase(true, BookMediaType.Ebook, MonitorTypes.All, true, false)]
        [TestCase(false, BookMediaType.Ebook, MonitorTypes.None, true, true)]
        [TestCase(true, BookMediaType.Ebook, MonitorTypes.None, true, true)]
        [TestCase(false, BookMediaType.Ebook, MonitorTypes.SpecificBook, true, false)]
        [TestCase(true, BookMediaType.Ebook, MonitorTypes.SpecificBook, true, false)]
        public void initial_monitoring_intent_should_survive_persistence_and_initial_scan(bool postgres,
            BookMediaType side, MonitorTypes? initialMode, bool search, bool monitoredBeforeScan)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var stored = new StoredListLibrary(database);
                var root = new RootFolder { Name = "Nullable initial root", Path = @"C:\NullableRoot".AsOsAgnostic(),
                    FolderType = side == BookMediaType.Audiobook ? FolderType.Audiobook : FolderType.Ebook };
                var settings = new MediaTypeSettings { QualityProfileId = 3, MetadataProfileId = 4,
                    Monitored = false, MonitorExistingMode = null, MonitorNewItems = NewItemMonitorTypes.None };
                if (side == BookMediaType.Audiobook) root.SetAudiobookSettings(settings);
                else root.SetEbookSettings(settings);
                var roots = new RootFolderRepository(database, stored.Events);
                roots.Insert(root);
                root = roots.Get(root.Id);
                Assert.That(new RootFolderSettingsResolver(null).ResolveSettings(root, side).MonitorExistingMode, Is.Null);
                var config = new MonitoringConfig
                {
                    CreateAudiobook = side == BookMediaType.Audiobook, CreateEbook = side == BookMediaType.Ebook,
                    AudiobookRootFolderPath = side == BookMediaType.Audiobook ? root.Path : null,
                    EbookRootFolderPath = side == BookMediaType.Ebook ? root.Path : null,
                    AudiobookMonitored = side == BookMediaType.Audiobook ? true : null,
                    EbookMonitored = side == BookMediaType.Ebook ? true : null,
                    AudiobookMonitorExistingMode = side == BookMediaType.Audiobook ? initialMode : null,
                    EbookMonitorExistingMode = side == BookMediaType.Ebook ? initialMode : null,
                    MonitorMode = initialMode, SearchForMissingBooks = search
                };
                var library = new AuthorLibraryService(stored.AuthorService, new StubAuthorInfo(new Author()), stored.Books,
                    null, stored.Editions, null, new StubMetadataProfileService(), new TestQualityProfileService(),
                    stored.Paths, new StubRootFolderService(root), stored.Queue, stored.Events, stored.Pending, database,
                    new ImportListExclusionService(new ImportListExclusionRepository(database, stored.Events), stored.Logger),
                    new EditionMetadataProfileFilter(new TestTermMatcherService()), null, stored.Logger, new EditionSelector(stored.Logger));
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                typeof(AuthorLibraryService).GetMethod("NormalizeMonitoringConfigFromExplicitRootFolders", flags)
                    .Invoke(library, new object[] { config, null });
                Assert.That(side == BookMediaType.Audiobook ? config.AudiobookMonitorExistingMode : config.EbookMonitorExistingMode, Is.EqualTo(initialMode));
                typeof(AuthorLibraryService).GetMethod("ApplyMonitoringConfig", flags).Invoke(library, new object[] { stored.Author, config });
                stored.Authors.Update(stored.Author);
                var requested = CatalogBook(side, "Requested Work", "123");
                requested.SetMonitored(true);
                requested.AddOptions = new AddBookOptions { AddType = BookAddType.Manual };
                requested.Editions.Single().Monitored = true;
                stored.Save(requested);
                var other = CatalogBook(side, "Other Work", "124");
                other.Editions.Single().Monitored = true;
                stored.Save(other);
                stored.Books.SetMonitored(new[] { other.Id }, monitoredBeforeScan);
                var opposite = CatalogBook(side == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook, "Requested Work", "123");
                opposite.SetMonitored(true);
                stored.Save(opposite);
                var bookAdded = Proxy<IBookAddedService>((method, args) => null);
                var handler = new AuthorScannedHandler(new BookMonitoredService(stored.AuthorService, stored.Books, stored.Logger),
                    stored.AuthorService, stored.Queue, bookAdded, stored.Events, stored.Logger, stored.Books);
                handler.Handle(new AuthorScannedEvent(stored.Authors.Get(stored.Author.Id)));
                var expectedOther = initialMode == MonitorTypes.All || initialMode != MonitorTypes.None && monitoredBeforeScan;
                Assert.Multiple(() =>
                {
                    Assert.That(stored.Books.GetBook(requested.Id).IsMonitored(), Is.True);
                    Assert.That(stored.Books.GetBook(other.Id).IsMonitored(), Is.EqualTo(expectedOther));
                    Assert.That(stored.Books.GetBook(opposite.Id).IsMonitored(), Is.True);
                    Assert.That(stored.Authors.Get(stored.Author.Id).AddOptions, Is.Null);
                    Assert.That(stored.Commands.OfType<MissingBookSearchCommand>().Select(command => command.AuthorId),
                        Is.EquivalentTo(search ? new[] { stored.Author.Id } : Array.Empty<int>()));
                    Assert.That(stored.BookRows.GetMissingBookSearchTargets(null, stored.Author.Id).Select(book => book.BookId),
                        Is.EquivalentTo(expectedOther ? new[] { requested.Id, other.Id } : new[] { requested.Id }));
                });
                var commandCount = stored.Commands.Count;
                handler.Handle(new AuthorScannedEvent(stored.Authors.Get(stored.Author.Id)));
                Assert.That(stored.Commands.Count, Is.EqualTo(commandCount), "Initial search must run once.");
            });
        }

        private sealed class StoredListLibrary
        {
            public readonly NzbDrone.Core.Datastore.IMainDatabase Database;
            public readonly Logger Logger = LogManager.GetCurrentClassLogger();
            public readonly IEventAggregator Events = Proxy<IEventAggregator>((method, args) => null);
            public readonly IMediaFileService Files = Proxy<IMediaFileService>((method, args) =>
                method.ReturnType == typeof(List<BookFile>) ? new List<BookFile>() :
                method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null);
            public readonly List<Command> Commands = new();
            public readonly Dictionary<string, int> Calls = new();
            public readonly List<List<int>> AddOptionsBatches = new();
            public readonly List<string> ReaderStatements = new();
            public readonly List<int> SyncReaderCounts = new();
            public readonly List<long> SyncElapsedMilliseconds = new();
            public int MetadataReads;
            public bool RejectBookSearches;
            public readonly IManageCommandQueue Queue;
            public readonly AuthorRepository Authors;
            public readonly BookRepository BookRows;
            public readonly EditionService Editions;
            public readonly ProviderAliasService Aliases;
            public readonly StubRootFolderService Roots;
            public readonly StubAuthorPathBuilder Paths = new();
            public readonly AuthorService AuthorService;
            public readonly IBookService Books;
            public readonly PendingAuthorImportRepository Requests;
            public readonly PendingAuthorImportService Pending;
            public readonly Author Author;

            public StoredListLibrary(NzbDrone.Core.Datastore.IMainDatabase database, Action<object> onEvent = null, IMediaFileService files = null)
            {
                if (files != null) Files = files;
                if (onEvent != null)
                    Events = Proxy<IEventAggregator>((method, args) =>
                    {
                        if (method.Name == nameof(IEventAggregator.PublishEvent)) onEvent(args[0]);
                        return null;
                    });
                Database = Proxy<NzbDrone.Core.Datastore.IMainDatabase>((method, args) =>
                {
                    var result = method.Invoke(database, args);
                    return method.Name == "OpenConnection"
                        ? CommandObserver.Wrap((System.Data.IDbConnection)result, command => ReaderStatements.Add(command.CommandText)) : result;
                });
                var storedDatabase = Database;
                Queue = Proxy<IManageCommandQueue>((method, args) =>
                {
                    ((Command)args[0]).Trigger = (CommandTrigger)args[2];
                    if (RejectBookSearches && args[0] is BookSearchCommand) throw new InvalidOperationException("Simulated search queue rejection");
                    Commands.Add((Command)args[0]);
                    return new CommandModel { Id = Commands.Count, Body = (Command)args[0] };
                });
                Authors = new AuthorRepository(storedDatabase, Events, Logger);
                BookRows = new BookRepository(storedDatabase, Events);
                Aliases = new ProviderAliasService(new ProviderAliasRepository(storedDatabase, Events));
                Editions = new EditionService(new EditionRepository(storedDatabase, Events), Events, Logger);
                Roots = new StubRootFolderService(BuildAudiobookRoot(@"C:\Audio".AsOsAgnostic()), BuildEbookRoot(@"C:\Ebooks".AsOsAgnostic()),
                    BuildAudiobookRoot(@"C:\ExistingAudio".AsOsAgnostic()), BuildEbookRoot(@"C:\ExistingEbooks".AsOsAgnostic()));
                AuthorService = new AuthorService(Authors, Events, Paths, Roots, Queue, new CacheManager(), BookRows, null, Logger,
                    null, null, new Lazy<IBookService>(() => Books), mainDatabase: storedDatabase, Aliases);
                var realBooks = new BookService(BookRows, Editions, Events, AuthorService,
                    Files, null, new SeriesBookLinkRepository(storedDatabase, Events), null,
                    Logger, providerAliasService: Aliases);
                Books = Proxy<IBookService>((method, args) =>
                {
                    Calls[method.Name] = Calls.GetValueOrDefault(method.Name) + 1;
                    if (method.Name == nameof(IBookService.SetAddOptions))
                        AddOptionsBatches.Add(((IEnumerable<Book>)args[0]).Select(book => book.Id).ToList());
                    return method.Invoke(realBooks, args);
                });
                Author = new Author
                {
                    Name = "Existing Author", Overview = string.Empty, CleanName = "existingauthor", SortName = "existingauthor", SortNameLastFirst = "Author, Existing",
                    TitleSlug = "existing-author", Path = @"C:\Existing\Existing Author".AsOsAgnostic(), Added = DateTime.UtcNow,
                    GoodreadsAuthorId = "gr:562254", AudiobookQualityProfileId = 9, EbookQualityProfileId = 9,
                    AudiobookMetadataProfileId = 8, EbookMetadataProfileId = 8,
                    AudiobookRootFolderPath = @"C:\ExistingAudio".AsOsAgnostic(), EbookRootFolderPath = @"C:\ExistingEbooks".AsOsAgnostic(),
                    AudiobookMonitored = false, EbookMonitored = false,
                    AudiobookMonitorNewItems = NewItemMonitorTypes.None, EbookMonitorNewItems = NewItemMonitorTypes.None
                };
                Authors.Insert(Author);
                Requests = new PendingAuthorImportRepository(storedDatabase, Events);
                Pending = new PendingAuthorImportService(Requests, AuthorService, Events, Logger, Aliases);
            }

            public void Save(Book book)
            {
                book.AuthorId = Author.Id;
                book.Author = Author;
                var editions = book.Editions;
                BookRows.Insert(book);
                editions.ForEach(edition => edition.BookId = book.Id);
                Editions.InsertMany(editions);
            }

            public NzbDrone.Core.Parser.Model.ImportListItemInfo Report(Book book, int intent)
            {
                return new()
                {
                    ImportListId = 1, AuthorProviderId = Author.GoodreadsAuthorId, Author = Author.Name,
                    BookProviderId = book.GoodreadsWorkId, Book = book.Title,
                    EditionProviderId = intent == 0 ? null : "gr:edition:" + book.GoodreadsWorkId.Substring(3),
                    IsGenericBookRequest = intent == 1,
                    EditionHasNarration = intent == 2 ? false : null,
                    HardcoverReadingFormatId = intent == 2 ? 1 : null
                };
            }

            public void Sync(Book anchor, BookMediaType side, List<NzbDrone.Core.Parser.Model.ImportListItemInfo> reports,
                bool keep = true, List<ImportListExclusion> exclusions = null, bool search = true)
            {
                var readersBefore = ReaderStatements.Count;
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                ImportListSyncServiceSearchOnAddFixture.BuildMissingMediaSideService(Author, anchor, side, false, AuthorService,
                    Books, Editions, Pending, Roots, Queue, reports: reports, keep: keep, exclusions: exclusions, search: search,
                    metadataLookup: (_, _) => System.Threading.Interlocked.Increment(ref MetadataReads), addMissingAuthors: false)
                    .Execute(new NzbDrone.Core.ImportLists.ImportListSyncCommand(1));
                SyncReaderCounts.Add(ReaderStatements.Count - readersBefore);
                SyncElapsedMilliseconds.Add(elapsed.ElapsedMilliseconds);
            }

            public void Process(PendingAuthorImport request, Author remote, IMetadataProfileService profileService = null)
            {
                request.NextAttemptAt = DateTime.UtcNow.AddMinutes(-1);
                Requests.Update(request);
                var authorInfo = new StubAuthorInfo(remote);
                var profiles = profileService ?? new StubMetadataProfileService();
                var exclusions = new ImportListExclusionService(new ImportListExclusionRepository(Database, Events), Logger);
                var selector = new EditionSelector(Logger);
                var filter = new EditionMetadataProfileFilter(new TestTermMatcherService());
                var refreshEditions = new RefreshEditionService(Editions,
                    Proxy<IMetadataTagService>((method, args) => null), null, Database, Logger);
                var refreshBooks = new RefreshBookService(Books, AuthorService, Roots, Editions, authorInfo, null,
                    refreshEditions, Files, null, Events, null, selector, filter,
                    Proxy<NzbDrone.Core.MediaCover.IMapCoversToLocal>((method, args) => null), Logger, mainDatabase: null);
                var settings = Proxy<NzbDrone.Core.Configuration.IConfigService>((method, args) =>
                    method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null);
                var refreshAuthor = new RefreshAuthorService(MetadataHealthGateStub.Ready(), authorInfo, AuthorService, Books, Editions, profiles, refreshBooks, null,
                    Events, Queue, Files, new global::NzbDrone.Core.History.HistoryRepository(Database, Events), Roots, null, new MonitorNewBookService(Logger), settings,
                    exclusions, null, null, null, Logger, editionSelector: selector, mainDatabase: Database, bookRepository: new BookRepository(Database, Events), authorRepository: new AuthorRepository(Database, Events, Logger));
                var library = new AuthorLibraryService(AuthorService, authorInfo, Books, null, Editions, null,
                    profiles, new TestQualityProfileService(), Paths, Roots, Queue, Events, Pending, Database,
                    exclusions, filter, null, Logger, selector, refreshAuthor);
                new ProcessPendingImportsCommandHandler(Pending, library, Books,
                    Proxy<IProvideBookInfo>((method, args) => throw new AssertionException("The complete author catalog must be used once.")),
                    Queue, Events, Logger, Aliases).Execute(new ProcessPendingImportsCommand());
            }
        }

        private static Book CatalogBook(BookMediaType side, string title, string work)
        {
            return new Book
            {
                Title = title, Overview = string.Empty, CleanTitle = title.ToLowerInvariant(), TitleSlug = title.ToLowerInvariant().Replace(" ", "-"),
                MediaType = side, AnyEditionOk = true, GoodreadsWorkId = "gr:" + work, HardcoverBookId = "hc:" + work,
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
