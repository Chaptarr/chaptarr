using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.Books
{
    public partial class BookServiceWantedNarratorSeriesLinksFixture
    {
        [TestCase(false, BookMediaType.Audiobook, false)]
        [TestCase(true, BookMediaType.Audiobook, false)]
        [TestCase(false, BookMediaType.Ebook, false)]
        [TestCase(true, BookMediaType.Ebook, false)]
        [TestCase(false, BookMediaType.Audiobook, true)]
        [TestCase(true, BookMediaType.Audiobook, true)]
        [TestCase(false, BookMediaType.Ebook, true)]
        [TestCase(true, BookMediaType.Ebook, true)]
        [TestCase(false, BookMediaType.Audiobook, true, true)]
        [TestCase(true, BookMediaType.Audiobook, true, true)]
        [TestCase(false, BookMediaType.Ebook, true, true)]
        [TestCase(true, BookMediaType.Ebook, true, true)]
        [TestCase(false, BookMediaType.Ebook, true, false, true)]
        [TestCase(true, BookMediaType.Ebook, true, false, true)]
        [TestCase(false, BookMediaType.Audiobook, false, false, false, true)]
        [TestCase(true, BookMediaType.Audiobook, false, false, false, true)]
        [TestCase(false, BookMediaType.Ebook, false, false, false, true)]
        [TestCase(true, BookMediaType.Ebook, false, false, false, true)]
        [TestCase(false, BookMediaType.Audiobook, true, false, false, false, true)]
        [TestCase(true, BookMediaType.Audiobook, true, false, false, false, true)]
        [TestCase(false, BookMediaType.Ebook, true, false, false, false, true)]
        [TestCase(true, BookMediaType.Ebook, true, false, false, false, true)]
        public void generic_list_work_should_survive_author_catalog_filters_and_refresh_without_edition_intent(
            bool postgres, BookMediaType side, bool existing, bool pinned = false, bool strayDisabledAudioTarget = false,
            bool unsupportedEdition = false, bool missingRequested = false)
        {
            RunWithDatabase(postgres, database =>
            {
                var logger = LogManager.GetCurrentClassLogger();
                var events = new StubEventAggregator();
                var profiles = new MetadataProfileRepository(database, events);
                var profile = profiles.Insert(new MetadataProfile
                {
                    Name = "English dated works", AllowedLanguages = "eng", SkipMissingDate = true,
                    MinPopularity = 0, Ignored = new List<string>()
                });
                var authors = new AuthorRepository(database, events, logger);
                var authorService = InterfaceProxy.Proxy<IAuthorService>((method, args) => method.Name switch
                {
                    nameof(IAuthorService.FindByProviderId) => authors.All().FirstOrDefault(),
                    nameof(IAuthorService.GetAuthor) => authors.Get((int)args[0]),
                    nameof(IAuthorService.GetExistingAuthors) => authors.Get((IEnumerable<int>)args[0]).ToList(),
                    nameof(IAuthorService.GetAllAuthors) => authors.All().ToList(),
                    nameof(IAuthorService.GetAuthors) => authors.Get((IEnumerable<int>)args[0]).ToList(),
                    nameof(IAuthorService.AddAuthor) => authors.Insert((Author)args[0]),
                    nameof(IAuthorService.UpdateAuthor) => authors.Update((Author)args[0]),
                    nameof(IAuthorService.UpdateAuthorProgressiveSettings) => args[0],
                    nameof(IAuthorService.AuthorPathExists) => false,
                    nameof(IAuthorService.SetAddOptions) => SaveAuthorOptions((Author)args[0]),
                    _ => throw new NotImplementedException(method.Name)
                });
                object SaveAuthorOptions(Author author)
                {
                    authors.SetFields(author, value => value.AddOptions);
                    return null;
                }

                var root = new RootFolder { Path = @"C:\Books".AsOsAgnostic(), FolderType = FolderType.Mixed };
                var settings = new MediaTypeSettings
                {
                    QualityProfileId = 1, MetadataProfileId = profile.Id, Monitored = true,
                    MonitorExistingBooks = false, MonitorNewItems = NewItemMonitorTypes.None
                };
                root.SetAudiobookSettings(settings);
                root.SetEbookSettings(settings);
                var roots = InterfaceProxy.Proxy<IRootFolderService>((method, _) => method.Name switch
                {
                    nameof(IRootFolderService.All) => new List<RootFolder> { root },
                    nameof(IRootFolderService.GetBestRootFolder) => root,
                    _ => throw new NotImplementedException(method.Name)
                });
                var bookRows = new BookRepository(database, events);
                var editionRows = new EditionRepository(database, events);
                var editions = new EditionService(editionRows, events, logger);
                var files = new StubMediaFileService();
                var books = new BookService(bookRows, editions, events, authorService, files, roots,
                    new SeriesBookLinkRepository(database, events), new StubMultiCopySeriesService(), logger);
                var profileService = new MetadataProfileService(profiles, authorService, books, editions, files, null, roots,
                    new TestTermMatcherService(), events, logger);
                var filter = new EditionMetadataProfileFilter(new TestTermMatcherService());
                var includeEligible = false;
                Book Pocket(BookMediaType media, string work)
                {
                    var prefix = work + (media == BookMediaType.Audiobook ? "-audio" : "-ebook");
                    var readingFormat = unsupportedEdition ? 4 : media == BookMediaType.Audiobook ? 2 : 3;
                    var book = new Book
                    {
                        Title = "Requested work", Overview = string.Empty, TitleSlug = prefix, CleanTitle = "requestedwork",
                        HardcoverBookId = work, MediaType = media, AnyEditionOk = true,
                        Editions = new List<Edition>
                        {
                            new() { ForeignEditionId = prefix + "-first", Title = "French edition", ReadingFormatId = readingFormat,
                                Language = "fra", Narrator = "First Narrator", Ratings = new Ratings { Votes = 100, Value = 4 } },
                            new() { ForeignEditionId = prefix + "-second", Title = "Other French edition", ReadingFormatId = readingFormat,
                                Language = "fra", Narrator = "Second Narrator", Ratings = new Ratings { Votes = 1, Value = 4 } }
                        }
                    };
                    if (includeEligible && work == "hc:1001" && media == side)
                    {
                        book.Editions.Add(new Edition { ForeignEditionId = prefix + "-eligible", Title = "English edition",
                            ReadingFormatId = media == BookMediaType.Audiobook ? 2 : 3, Language = "eng", Narrator = "Third Narrator" });
                    }
                    if (missingRequested && work == "hc:1002")
                    {
                        book.ReleaseDate = new DateTime(2020, 1, 1);
                        book.Editions.ForEach(edition => edition.Language = "eng");
                    }
                    return book;
                }
                Author Catalog() => new()
                {
                    Name = "Requested Author", Overview = string.Empty, CleanName = "requestedauthor", TitleSlug = "requested-author",
                    HardcoverAuthorId = "hc:9001", Path = @"C:\Books\Requested Author".AsOsAgnostic(),
                    AudiobookPath = @"C:\Books\Requested Author".AsOsAgnostic(), EbookPath = @"C:\Books\Requested Author".AsOsAgnostic(),
                    Books = new List<Book>
                    {
                        Pocket(side, "hc:1001"), Pocket(side, "hc:1002"), Pocket(side, "hc:1003"),
                        Pocket(side == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook, "hc:1001")
                    },
                    Series = new List<Series>()
                };
                var authorInfo = InterfaceProxy.Proxy<IProvideAuthorInfo>((method, _) => method.Name == nameof(IProvideAuthorInfo.GetAuthorInfo)
                    ? Catalog() : throw new AssertionException(method.Name));
                var exclusions = InterfaceProxy.Proxy<IImportListExclusionService>((method, _) => method.Name switch
                {
                    nameof(IImportListExclusionService.All) => new List<ImportListExclusion>(),
                    nameof(IImportListExclusionService.FindByForeignId) => new List<ImportListExclusion>(),
                    _ => throw new NotImplementedException(method.Name)
                });
                var queue = InterfaceProxy.Proxy<IManageCommandQueue>((method, _) => method.ReturnType == typeof(CommandModel) ? new CommandModel() : null);
                var selector = new EditionSelector(logger);
                var refreshEditions = new RefreshEditionService(editions, InterfaceProxy.Proxy<IMetadataTagService>((_, _) => null), null, database, logger);
                var refreshBooks = new RefreshBookService(books, authorService, roots, editions, authorInfo, null,
                    refreshEditions, files, null, events, null, selector, filter, InterfaceProxy.Proxy<IMapCoversToLocal>((_, _) => null), logger, mainDatabase: null);
                var configService = InterfaceProxy.Proxy<IConfigService>((method, _) => method.ReturnType == typeof(bool) ? false :
                    method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null);
                var refresh = new RefreshAuthorService(MetadataHealthGateStub.Ready(), authorInfo, authorService, books, editions, profileService, refreshBooks, null,
                    events, queue, files, new global::NzbDrone.Core.History.HistoryRepository(database, events), roots, null, InterfaceProxy.Proxy<IMonitorNewBookService>((_, _) => false),
                    configService, exclusions, null, null, null, logger, editionSelector: selector, mainDatabase: database, bookRepository: new BookRepository(database, events), authorRepository: new AuthorRepository(database, events, logger));
                var pending = new PendingAuthorImportService(new PendingAuthorImportRepository(database, events), authorService, events, logger);
                var paths = InterfaceProxy.Proxy<IBuildAuthorPaths>((method, _) => method.ReturnType == typeof(string)
                    ? @"C:\Books\Requested Author".AsOsAgnostic() : null);
                var library = new AuthorLibraryService(authorService, authorInfo, books, null, editions, null, profileService,
                    new TestQualityProfileService(), paths, roots, queue, events, pending, database, exclusions, filter, null,
                    logger, selector, refresh);
                var config = new MonitoringConfig
                {
                    CreateAudiobook = side == BookMediaType.Audiobook, CreateEbook = side == BookMediaType.Ebook,
                    AudiobookRootFolderPath = root.Path, EbookRootFolderPath = root.Path,
                    AudiobookQualityProfileId = 1, EbookQualityProfileId = 1,
                    AudiobookMetadataProfileId = profile.Id, EbookMetadataProfileId = profile.Id,
                    AudiobookMonitored = true, EbookMonitored = true,
                    AudiobookMonitorExistingMode = MonitorTypes.None, EbookMonitorExistingMode = MonitorTypes.None,
                    AudiobookMonitorNewItems = NewItemMonitorTypes.None, EbookMonitorNewItems = NewItemMonitorTypes.None,
                    AudiobookBooksToMonitor = side == BookMediaType.Audiobook || strayDisabledAudioTarget ? new List<string> { "hc:1001" } : null,
                    EbookBooksToMonitor = side == BookMediaType.Ebook ? new List<string> { "hc:1001" } : null,
                    RequestedBy = "ImportList:Generic requested work"
                };
                var previousBookId = 0;
                var duplicateBookId = 0;
                if (existing)
                {
                    var author = Catalog();
                    author.Added = DateTime.UtcNow;
                    author.AudiobookRootFolderPath = root.Path;
                    author.EbookRootFolderPath = root.Path;
                    author.AudiobookQualityProfileId = 1;
                    author.EbookQualityProfileId = 1;
                    author.AudiobookMetadataProfileId = profile.Id;
                    author.EbookMetadataProfileId = profile.Id;
                    authors.Insert(author);
                    var local = Pocket(side, missingRequested ? "hc:1002" : "hc:1001");
                    local.AuthorId = author.Id;
                    local.Author = author;
                    local.Added = DateTime.UtcNow;
                    local.AnyEditionOk = !pinned;
                    var selected = local.Editions.First();
                    local.Editions = new List<Edition> { selected };
                    var saved = bookRows.Insert(local);
                    previousBookId = saved.Id;
                    selected.BookId = saved.Id;
                    selected.Monitored = true;
                    selected.ManualAdd = pinned;
                    editionRows.Insert(selected);
                    if (!pinned && !missingRequested)
                    {
                        var duplicate = Pocket(side, "hc:1001");
                        duplicate.TitleSlug += "-copy"; // Existing local copies have distinct route identities.
                        duplicate.AuthorId = author.Id;
                        duplicate.Author = author;
                        duplicate.Added = DateTime.UtcNow;
                        var duplicateEdition = duplicate.Editions.Last();
                        duplicate.Editions = new List<Edition> { duplicateEdition };
                        bookRows.Insert(duplicate);
                        duplicateBookId = duplicate.Id;
                        duplicateEdition.BookId = duplicate.Id;
                        editionRows.Insert(duplicateEdition);
                    }
                }

                var deletedBookIds = new List<int>();
                events.OnPublish = message =>
                {
                    if (message is BookDeletedEvent deleted)
                    {
                        Assert.That(duplicateBookId, Is.GreaterThan(0), "Only the unrequested duplicate may be pruned.");
                        Assert.That(deleted.Book.Id, Is.EqualTo(duplicateBookId));
                        Assert.That(deleted.Book.Id, Is.Not.EqualTo(previousBookId), "The requested work must survive.");
                        Assert.That(BookIdentity.HasUserPreservationMarker(deleted.Book), Is.False);
                        Assert.That(deleted.Book.IsMonitored(), Is.False);
                        Assert.That(deleted.DeleteFiles, Is.False);
                        deletedBookIds.Add(deleted.Book.Id);
                    }
                };

                library.AddAuthorAsync("hc:9001", config).GetAwaiter().GetResult();
                var savedAuthor = authors.All().Single();
                var savedBooks = books.GetBooksByAuthor(savedAuthor.Id);
                if (unsupportedEdition)
                {
                    Assert.That(savedBooks, Is.Empty, "Work preservation cannot invent a supported edition absent from the author catalog.");
                    return;
                }

                Assert.That(savedBooks, Has.Count.EqualTo(missingRequested || duplicateBookId > 0 ? 2 : 1), "Unrequested and opposite-media works must still obey profile filters.");
                Assert.That(savedBooks.All(book => book.MediaType == side), Is.True);
                Assert.That(savedBooks.Any(book => book.HardcoverBookId == "hc:1003" || !missingRequested && book.HardcoverBookId == "hc:1002"), Is.False);
                var requested = BookIdentity.SelectRequestedBook(savedBooks.Where(book => book.HardcoverBookId == "hc:1001"));
                var requestedBookId = requested.Id;
                void AssertOtherCopyUnchanged()
                {
                    if (duplicateBookId == 0) return;
                    var currentBooks = books.GetBooksByAuthor(savedAuthor.Id);
                    Assert.That(currentBooks.All(book => book.Id == previousBookId || book.Id == duplicateBookId), Is.True);
                    var otherCopy = currentBooks.SingleOrDefault(book => book.Id == duplicateBookId);
                    if (otherCopy == null) return;
                    Assert.Multiple(() =>
                    {
                        Assert.That(otherCopy.AddOptions.AddType, Is.EqualTo(BookAddType.Automatic));
                        Assert.That(otherCopy.AnyEditionOk, Is.True);
                        Assert.That(otherCopy.IsMonitored(), Is.False);
                        Assert.That(otherCopy.Editions.Any(edition => edition.ManualAdd), Is.False);
                        Assert.That(currentBooks.Select(book => book.Id),
                            Is.EquivalentTo(new[] { previousBookId, duplicateBookId }));
                    });
                }
                Assert.Multiple(() =>
                {
                    Assert.That(requested.MediaType, Is.EqualTo(side));
                    Assert.That(requested.HardcoverBookId, Is.EqualTo("hc:1001"));
                    Assert.That(requested.AddOptions.AddType, Is.EqualTo(pinned ? BookAddType.Automatic : BookAddType.Manual));
                    Assert.That(requested.AnyEditionOk, Is.EqualTo(!pinned));
                    Assert.That(requested.Editions, Has.Count.EqualTo(1));
                    Assert.That(requested.Editions.Single().ManualAdd, Is.EqualTo(pinned));
                    Assert.That(requested.Editions.Single().ForeignEditionId, Does.EndWith("-first"));
                });
                if (existing && !missingRequested) Assert.That(requested.Id, Is.EqualTo(previousBookId));
                if (missingRequested) Assert.That(savedBooks.Single(book => book.HardcoverBookId == "hc:1002").Id, Is.EqualTo(previousBookId));
                AssertOtherCopyUnchanged();
                refresh.ReconcileAuthorBlob(savedAuthor, Catalog());
                requested = books.GetBook(requestedBookId);
                Assert.That(requested.Id, Is.EqualTo(requestedBookId));
                Assert.That(requested.Editions.Single().ForeignEditionId, Does.EndWith("-first"));
                Assert.That(requested.Editions.Single().ManualAdd, Is.EqualTo(pinned));
                AssertOtherCopyUnchanged();

                includeEligible = true;
                refresh.ReconcileAuthorBlob(savedAuthor, Catalog());
                requested = books.GetBook(requestedBookId);
                Assert.That(BookEditionIdentity.GetMonitoredEdition(requested).ForeignEditionId,
                    Does.EndWith(pinned ? "-first" : "-eligible"));
                Assert.That(requested.AnyEditionOk, Is.EqualTo(!pinned));
                Assert.That(requested.Editions.Any(edition => edition.ManualAdd), Is.EqualTo(pinned));
                AssertOtherCopyUnchanged();
                Assert.That(deletedBookIds, Is.All.EqualTo(duplicateBookId));
                Assert.That(deletedBookIds, Has.Count.LessThanOrEqualTo(duplicateBookId > 0 ? 1 : 0));
                TestContext.WriteLine($"{database.DatabaseType}/{side}: generic catalog request existing={existing}, pin={pinned}; retained one work, ordinary selection resumes={!pinned}");
            });
        }
    }
}
