using System;
using System.Collections.Generic;
using System.Data;
using System.Reflection;
using System.Linq;
using Chaptarr.Api.V1.Books;
using Chaptarr.Api.V1.ProviderIds;
using Chaptarr.Core.Test.Books;
using Chaptarr.Core.Test.Common;
using Chaptarr.Http.Middleware;
using FluentValidation;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.BookInfo;
using NzbDrone.Core.MetadataSource.Goodreads;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Validation;
using NzbDrone.SignalR;

using BookResource = Chaptarr.Api.V1.Books.BookResource;

using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    [NonParallelizable]
    public class BookApiIdentityFixture
    {
        private sealed class Harness
        {
            public readonly AuthorRepository Authors;
            public readonly BookRepository Books;
            public readonly EditionRepository Editions;
            public readonly ImportListExclusionRepository Exclusions;
            public readonly BookService BookService;
            public readonly EditionService EditionService;
            public readonly IAuthorService AuthorService;
            public readonly IMediaFileService Files;
            public readonly MediaFileRepository FileRepository;
            public readonly ProviderAliasService Aliases;
            public readonly IEventAggregator Events;
            public readonly IManageCommandQueue Queue;
            public readonly List<Command> Commands = new();
            public readonly List<List<int>> FileBatches = new();
            public readonly Author Author;
            public int RemoteLookups;
            public bool FailSave;
            public bool RejectSingleFileLookups;

            public Harness(IMainDatabase database)
            {
                var logger = LogManager.GetCurrentClassLogger();
                Events = Proxy<IEventAggregator>((method, args) => null);
                Authors = new AuthorRepository(database, Events, logger);
                Books = new BookRepository(database, Events);
                Editions = new EditionRepository(database, Events);
                Exclusions = new ImportListExclusionRepository(database, Events);
                Author = Authors.Insert(new Author
                {
                    Path = @"C:\Audio\Fixture Author", TitleSlug = "fixture-author",
                    Name = "Fixture Author", CleanName = "fixtureauthor", SortName = "fixtureauthor",
                    SortNameLastFirst = "Author, Fixture", HardcoverAuthorId = "hc:9001", Added = DateTime.UtcNow,
                    AudiobookRootFolderPath = @"C:\Audio", AudiobookQualityProfileId = 1, AudiobookMetadataProfileId = 1,
                    EbookRootFolderPath = @"C:\Ebooks", EbookQualityProfileId = 2, EbookMetadataProfileId = 2,
                    AudiobookMonitored = false, EbookMonitored = false
                });
                AuthorService = Proxy<IAuthorService>((method, args) => method.Name switch
                {
                    nameof(IAuthorService.GetAuthor) => Authors.Get((int)args[0]),
                    nameof(IAuthorService.GetAuthors) => Authors.Get((IEnumerable<int>)args[0]).ToList(),
                    nameof(IAuthorService.GetExistingAuthors) => Authors.FindExisting((IEnumerable<int>)args[0]),
                    nameof(IAuthorService.FindByProviderId) => Author,
                    _ => throw new AssertionException("Unexpected author change or fetch: " + method.Name)
                });
                FileRepository = new MediaFileRepository(database, Events);
                Aliases = new ProviderAliasService(new ProviderAliasRepository(database, Events));
                Files = Proxy<IMediaFileService>((method, args) =>
                {
                    if (method.Name == nameof(IMediaFileService.GetFilesByBooks))
                    {
                        FileBatches.Add(((List<int>)args[0]).ToList());
                        return FileRepository.GetFilesByBooks((List<int>)args[0]);
                    }
                    if (method.Name == nameof(IMediaFileService.GetFilesByBook) ||
                        method.Name == nameof(IMediaFileService.GetFilesByEdition))
                    {
                        if (RejectSingleFileLookups)
                        {
                            throw new AssertionException("Unexpected per-row file lookup: " + method.Name);
                        }
                        return new List<BookFile>();
                    }
                    throw new AssertionException("Unexpected file operation: " + method.Name);
                });
                EditionService = new EditionService(Editions, Events, logger);
                BookService = new BookService(Books, EditionService, Events, AuthorService, Files,
                    null, new SeriesBookLinkRepository(database, Events), null, logger, providerAliasService: Aliases);
                Queue = new CommandQueueManager(Proxy<ICommandRepository>((method, args) =>
                {
                    if (method.Name != nameof(ICommandRepository.Insert))
                    {
                        throw new AssertionException(method.Name);
                    }
                    var command = (CommandModel)args[0];
                    command.Id = Commands.Count + 1;
                    Commands.Add(command.Body);
                    return command;
                }), null, null, logger);
            }

            public Book AddBook(BookMediaType mediaType, string workId = "hc:1001", string editionId = "hc:2001",
                bool monitored = false, string narrator = "Fixture Narrator")
            {
                var book = Books.Insert(new Book
                {
                    AuthorId = Author.Id, Title = "Stored title", CleanTitle = "storedtitle", Overview = "Stored description",
                    TitleSlug = Guid.NewGuid().ToString("N"), HardcoverBookId = workId, MediaType = mediaType,
                    Added = DateTime.UtcNow, ReleaseDate = new DateTime(2020, 1, 2), AnyEditionOk = false,
                    AudiobookMonitored = mediaType == BookMediaType.Audiobook && monitored,
                    EbookMonitored = mediaType == BookMediaType.Ebook && monitored
                });
                Editions.Insert(new Edition
                {
                    BookId = book.Id, Title = "Stored edition", ForeignEditionId = editionId,
                    Monitored = true, ManualAdd = true, ReadingFormatId = mediaType == BookMediaType.Audiobook ? 2 : 3,
                    NarratorNames = new List<string> { narrator }, Language = "eng"
                });
                return book;
            }

            public BookController ImportController(bool rejectSingleBookLookups = false)
            {
                var logger = LogManager.GetCurrentClassLogger();
                IBookService service = FailSave || rejectSingleBookLookups ? Proxy<IBookService>((method, args) =>
                {
                    if (rejectSingleBookLookups && method.Name == nameof(IBookService.GetBook))
                    {
                        throw new AssertionException("Unexpected per-row book lookup");
                    }
                    if (FailSave && method.Name == nameof(IBookService.SetAddOptions))
                    {
                        throw new InvalidOperationException("Injected persistence failure");
                    }
                    return method.Invoke(BookService, args);
                }) : BookService;
                var add = new AddBookService(AuthorService, null, service, null, null, null,
                    new ImportListExclusionService(Exclusions, logger), null, null, null, null, null,
                    EditionService, new EditionSelector(logger), new EditionMetadataProfileFilter(new TestTermMatcherService()),
                    Proxy<IMetadataProfileService>((method, args) => method.Name switch
                    {
                        nameof(IMetadataProfileService.Exists) => true,
                        nameof(IMetadataProfileService.Get) => new MetadataProfile { Id = (int)args[0] },
                        _ => throw new AssertionException(method.Name)
                    }), logger, Queue);
                return new BookController(AuthorService, service, add, EditionService, new EditionSelector(logger),
                    Proxy<ISeriesBookLinkService>((m, a) => new List<SeriesBookLink>()),
                    Proxy<NzbDrone.Core.AuthorStats.IAuthorStatisticsService>((m, a) => a.Length == 0
                        ? new List<NzbDrone.Core.AuthorStats.AuthorStatistics>()
                        : new NzbDrone.Core.AuthorStats.AuthorStatistics()),
                    Files, Proxy<IMapCoversToLocal>((m, a) => null), null,
                    Proxy<IBroadcastSignalRMessage>((m, a) => false), Queue, Events, null, null, null,
                    new QualityProfileExistsValidator(new TestQualityProfileService()),
                    new MetadataProfileExistsValidator(new TestMetadataProfileService()), logger)
                { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
            }

            public BookLookupController LookupController(Logger logger = null, ISearchForNewBook searchProxy = null)
            {
                return new BookLookupController(searchProxy ?? Proxy<ISearchForNewBook>((m, a) =>
                {
                    RemoteLookups++;
                    return new List<Book>();
                }), Proxy<IMapCoversToLocal>((m, a) => null), BookService, EditionService, Files,
                    Proxy<IMediaCoverProxy>((m, a) => null), providerAliasService: Aliases, logger: logger)
                { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
            }
        }

        [TestCase(false, BookMediaType.Audiobook)]
        [TestCase(true, BookMediaType.Audiobook)]
        [TestCase(false, BookMediaType.Ebook)]
        [TestCase(true, BookMediaType.Ebook)]
        public void importing_existing_book_preserves_metadata_pin_author_pause_and_monitor_none(bool postgres, BookMediaType mediaType)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var book = h.AddBook(mediaType);
                var sibling = h.AddBook(mediaType, "hc:1002", "hc:2002");
                var opposite = h.AddBook(mediaType == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook);
                var selected = h.EditionService.GetEditionsByBook(book.Id).Single();
                h.Editions.Insert(new Edition { BookId = book.Id, Title = "Other edition", ForeignEditionId = "hc:2003" });
                var oppositeExclusion = h.Exclusions.Insert(new ImportListExclusion
                { ForeignId = "hc:1001", Name = "Other format", MediaType = opposite.MediaType });
                var controller = h.ImportController();
                foreach (var mode in new[] { "none", "specificBook", "none" })
                {
                    var exclusion = h.Exclusions.Insert(new ImportListExclusion { ForeignId = "hc:1001", Name = "Fixture", MediaType = mediaType });
                    var result = controller.ImportBook(new BookImportResource
                    {
                        ForeignAuthorId = "hc:9001", ForeignBookId = "hc:1001", ForeignEditionId = "hc:2003",
                        MediaType = mediaType == BookMediaType.Audiobook ? "audiobook" : "ebook",
                        AuthorMonitoring = new BookImportAuthorMonitoring { Monitor = mode, SearchForMissing = mode == "specificBook" }
                    }).GetAwaiter().GetResult();
                    Assert.That(result, Is.TypeOf<CreatedResult>());
                    var stored = h.Books.Get(book.Id);
                    Assert.Multiple(() =>
                    {
                        Assert.That(stored.Title, Is.EqualTo(book.Title));
                        Assert.That(stored.CleanTitle, Is.EqualTo(book.CleanTitle));
                        Assert.That(stored.Overview, Is.EqualTo(book.Overview));
                        Assert.That(stored.ReleaseDate, Is.EqualTo(book.ReleaseDate));
                        Assert.That(stored.AnyEditionOk, Is.False);
                        Assert.That(stored.IsMonitored(), Is.EqualTo(h.Commands.Any()));
                        Assert.That(h.Books.Get(sibling.Id).IsMonitored(), Is.False);
                        Assert.That(h.Books.Get(opposite.Id).IsMonitored(), Is.False);
                        Assert.That(h.Authors.Get(h.Author.Id).AudiobookMonitored, Is.False);
                        Assert.That(h.Authors.Get(h.Author.Id).EbookMonitored, Is.False);
                        Assert.That(h.EditionService.GetEditionsByBook(book.Id).Single(e => e.Monitored).Id, Is.EqualTo(selected.Id));
                        Assert.That(h.Editions.Get(selected.Id).ManualAdd, Is.True);
                        Assert.That(h.Exclusions.All().Any(e => e.Id == exclusion.Id), Is.False);
                        Assert.That(h.Exclusions.All().Any(e => e.Id == oppositeExclusion.Id), Is.True);
                    });
                }
                var command = h.Commands.Single() as BookSearchCommand;
                Assert.That(command, Is.Not.Null);
                Assert.That(command.BookIds, Is.EqualTo(new[] { book.Id }));
                Assert.That(command.Trigger, Is.EqualTo(CommandTrigger.Manual));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void failed_existing_import_keeps_exclusions_and_duplicate_rows_return_choices(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var first = h.AddBook(BookMediaType.Audiobook);
                h.Exclusions.Insert(new ImportListExclusion { ForeignId = "hc:1001", Name = "Fixture", MediaType = BookMediaType.Audiobook });
                var request = new BookImportResource
                {
                    ForeignAuthorId = "hc:9001", ForeignBookId = "hc:1001", MediaType = "audiobook",
                    AuthorMonitoring = new BookImportAuthorMonitoring { Monitor = "none" }
                };
                h.FailSave = true;
                var failure = Assert.Throws<ValidationException>(() => h.ImportController().ImportBook(request).GetAwaiter().GetResult());
                Assert.That(failure.Message, Does.Contain("Injected persistence failure"));
                Assert.That(h.Exclusions.All(), Has.Count.EqualTo(1));
                Assert.That(h.Books.Get(first.Id).Title, Is.EqualTo("Stored title"));
                h.FailSave = false;
                var second = h.AddBook(BookMediaType.Audiobook, narrator: "Other Narrator");
                var response = h.ImportController().ImportBook(request).GetAwaiter().GetResult() as ObjectResult;
                Assert.That(response?.StatusCode, Is.EqualTo(409));
                Assert.That(((ProviderAmbiguityResource)response.Value).Candidates.Select(c => c.Id), Is.EquivalentTo(new[] { first.Id, second.Id }));
                Assert.That(h.Exclusions.All(), Has.Count.EqualTo(1));
                Assert.That(h.Commands, Is.Empty);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void lookup_resolves_legacy_and_qualified_edition_ids_and_returns_each_copy_in_scope(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var first = h.AddBook(BookMediaType.Audiobook, editionId: "gr:243729281-audiobook", narrator: "First Narrator");
                var second = h.AddBook(BookMediaType.Audiobook, editionId: "gr:243729281", narrator: "Second Narrator");
                var ebook = h.AddBook(BookMediaType.Ebook, editionId: "gr:243729281-ebook");
                foreach (var book in new[] { first, second })
                {
                    h.Editions.Insert(new Edition { BookId = book.Id, Title = "HC edition", ForeignEditionId = "hc:edition:2001-audiobook" });
                }
                var lookup = h.LookupController();
                foreach (var term in new[] { "gr-ed:243729281", "gr-ed:243729281-audiobook", "hc:edition:2001", "hc-ed:2001", "hc:edition:2001-audiobook" })
                {
                    h.FileBatches.Clear();
                    var result = lookup.Search(term, "audiobook").Value;
                    Assert.That(result.Select(b => b.Id), Is.EqualTo(new[] { first.Id, second.Id }), term);
                    Assert.That(result.Select(b => b.Narrator), Is.EqualTo(new[] { "First Narrator", "Second Narrator" }), term);
                    Assert.That(result.All(b => b.Editions.Count == 2), Is.True);
                    Assert.That(h.FileBatches.Count, Is.LessThanOrEqualTo(2));
                    Assert.That(h.FileBatches.SelectMany(ids => ids), Is.EquivalentTo(new[] { first.Id, second.Id, ebook.Id }),
                        "Each local row's files must be hydrated once across the response and summaries");
                    Assert.That(h.RemoteLookups, Is.Zero);
                }
                Assert.That(lookup.Search("gr-ed:243729281").Value, Has.Count.EqualTo(3));
            });
        }

        [TestCase(false, BookMediaType.Audiobook)]
        [TestCase(true, BookMediaType.Audiobook)]
        [TestCase(false, BookMediaType.Ebook)]
        [TestCase(true, BookMediaType.Ebook)]
        public void goodreads_work_lookup_keeps_work_namespace_and_all_native_copies(bool postgres, BookMediaType mediaType)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var work = h.AddBook(mediaType, "hc:1", "gr:898777");
                var copy = h.AddBook(mediaType, "hc:1", "gr:898777", monitored: true);
                var otherMediaType = mediaType == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook;
                var opposite = h.AddBook(otherMediaType, "hc:1", "gr:898777");
                foreach (var book in new[] { work, copy, opposite })
                {
                    book.GoodreadsWorkId = "gr:883963";
                    book.Title = "Lolita";
                    h.Books.Update(book);
                }
                var edition = h.AddBook(mediaType, "hc:258207", "gr:883963-" + mediaType.ToString().ToLowerInvariant());
                edition.GoodreadsWorkId = "gr:100379381";
                edition.Title = "Letters from Father Christmas";
                h.Books.Update(edition);
                var lookup = h.LookupController();
                var scope = mediaType.ToString().ToLowerInvariant();
                Assert.That(lookup.Search("gr:883963", scope).Value.Select(book => book.Id), Is.EqualTo(new[] { work.Id, copy.Id }));
                Assert.That(lookup.Search("gr:883963").Value.Select(book => book.Id), Is.EqualTo(new[] { work.Id, copy.Id, opposite.Id }));
                Assert.That(lookup.Search("gr-ed:883963", scope).Value.Select(book => book.Id), Is.EqualTo(new[] { edition.Id }));
                lookup.HttpContext.Items[ReadarrFacadeContext.ItemKey] = new ReadarrFacadeContext("gr", scope, "/readarr/gr/" + scope);
                Assert.That(lookup.Search("work:883963", otherMediaType.ToString().ToLowerInvariant()).Value.Select(book => book.Id),
                    Is.EqualTo(new[] { copy.Id, work.Id }));
                Assert.That(lookup.Search("edition:883963").Value.Select(book => book.Id), Is.EqualTo(new[] { edition.Id }));
                var controller = h.ImportController(rejectSingleBookLookups: true);
                controller.HttpContext.Items[ReadarrFacadeContext.ItemKey] = new ReadarrFacadeContext("gr", scope, "/readarr/gr/" + scope);
                foreach (var (book, seconds) in new[] { (work, 3600), (copy, 7200) })
                {
                    h.FileRepository.Insert(new BookFile
                    {
                        EditionId = h.EditionService.GetEditionsByBook(book.Id).Single().Id,
                        Path = @"C:\Audio\Fixture Author\" + book.Id + (mediaType == BookMediaType.Ebook ? ".epub" : ".m4b"),
                        Size = 1024, DurationSeconds = seconds, MediaType = scope, DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow,
                        Quality = new NzbDrone.Core.Qualities.QualityModel(), MediaInfo = new NzbDrone.Core.Parser.Model.MediaInfoModel()
                    });
                }

                h.FileBatches.Clear();
                h.RejectSingleFileLookups = true;
                var facade = controller.GetBooks(null, null, null, titleSlug: "883963", mediaType: scope);
                Assert.That(facade.Select(book => book.Id), Is.EqualTo(new[] { work.Id, copy.Id }));
                Assert.That(facade.Select(book => book.HasFiles), Is.EqualTo(new[] { true, true }));
                Assert.That(facade.Select(book => book.DurationMinutes), Is.EqualTo(new[] { 60d, 120d }));
                Assert.That(h.FileBatches, Has.Count.EqualTo(1));
                Assert.That(h.FileBatches.Single(), Is.EqualTo(new[] { work.Id, copy.Id }));
                Assert.That(controller.GetBooks(null, null, null, titleSlug: work.Id.ToString(), mediaType: scope), Is.Empty);
                Assert.That(h.RemoteLookups, Is.Zero);
                Assert.That(h.Commands, Is.Empty);
            });
        }

        [TestCase(false, BookMediaType.Audiobook)]
        [TestCase(true, BookMediaType.Audiobook)]
        [TestCase(false, BookMediaType.Ebook)]
        [TestCase(true, BookMediaType.Ebook)]
        public void work_import_and_facade_slug_exclude_colliding_edition_owners(bool postgres, BookMediaType mediaType)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var work = h.AddBook(mediaType, "hc:1", "gr:898777");
                work.GoodreadsWorkId = "gr:883963";
                work.Title = "Lolita";
                h.Books.Update(work);
                var collision = h.AddBook(mediaType, "hc:258207", "gr:883963-" + mediaType.ToString().ToLowerInvariant());
                collision.GoodreadsWorkId = "gr:100379381";
                collision.Title = "Letters from Father Christmas";
                h.Books.Update(collision);
                h.Aliases.ReplaceAliases("Book", collision.Id, "edition", new[] { "gr:883963" });
                var oppositeType = mediaType == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook;
                var opposite = h.AddBook(oppositeType, "hc:1", "gr:898777");
                opposite.GoodreadsWorkId = work.GoodreadsWorkId;
                h.Books.Update(opposite);
                var controller = h.ImportController();
                var scope = mediaType.ToString().ToLowerInvariant();
                var request = new BookImportResource
                {
                    ForeignAuthorId = "hc:9001", ForeignBookId = "gr:883963", MediaType = scope,
                    AuthorMonitoring = new BookImportAuthorMonitoring { Monitor = "none" }
                };
                var result = controller.ImportBook(request).GetAwaiter().GetResult();
                Assert.That(result, Is.TypeOf<CreatedResult>());
                Assert.That((int)((CreatedResult)result).Value.GetType().GetProperty("id").GetValue(((CreatedResult)result).Value), Is.EqualTo(work.Id));
                h.Aliases.ReplaceAliases("Book", collision.Id, "work", new[] { "gr:898777" });
                var model = h.BookService.GetBook(work.Id);
                model.GoodreadsBookId = "gr:898777";
                Assert.That(ProviderAmbiguityHelper.GetBookAmbiguity(h.BookService, model, "book", null), Is.Null);
                var copy = h.AddBook(mediaType, "hc:1", "gr:898777", monitored: true);
                copy.GoodreadsWorkId = work.GoodreadsWorkId;
                h.Books.Update(copy);
                var ambiguous = controller.ImportBook(request).GetAwaiter().GetResult() as ObjectResult;
                Assert.That(ambiguous?.StatusCode, Is.EqualTo(409));
                Assert.That(((ProviderAmbiguityResource)ambiguous.Value).Candidates.Select(book => book.Id), Is.EqualTo(new[] { work.Id, copy.Id }));
                controller.HttpContext.Items[ReadarrFacadeContext.ItemKey] = new ReadarrFacadeContext("gr", scope, "/readarr/gr/" + scope);
                Assert.That(controller.GetBooks(null, null, null, titleSlug: "883963", mediaType: scope).Select(book => book.Id),
                    Is.EqualTo(new[] { work.Id, copy.Id }));
                Assert.That(h.LookupController().Search("gr-ed:883963", scope).Value.Select(book => book.Id), Is.EqualTo(new[] { collision.Id }));
                Assert.That(h.Books.Get(collision.Id).IsMonitored(), Is.False);
                Assert.That(h.Books.Get(opposite.Id).IsMonitored(), Is.False);
                Assert.That(h.Commands, Is.Empty);
            });
        }

        [TestCase(false, BookMediaType.Audiobook)]
        [TestCase(true, BookMediaType.Audiobook)]
        [TestCase(false, BookMediaType.Ebook)]
        [TestCase(true, BookMediaType.Ebook)]
        public void edition_import_ambiguity_uses_only_edition_namespace(bool postgres, BookMediaType mediaType)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var book = h.AddBook(mediaType, "hc:258207", "gr:883963");
                book.GoodreadsWorkId = "gr:100379381";
                book.Title = "Letters from Father Christmas";
                h.Books.Update(book);
                var otherWork = h.AddBook(mediaType, "hc:1", "gr:898777");
                otherWork.GoodreadsWorkId = "gr:883963";
                otherWork.Title = "Lolita";
                h.Books.Update(otherWork);
                var controller = h.ImportController();
                var request = new BookImportResource
                {
                    ForeignAuthorId = "hc:9001", ForeignBookId = "gr:100379381", ForeignEditionId = "gr:883963",
                    MediaType = mediaType.ToString().ToLowerInvariant(),
                    AuthorMonitoring = new BookImportAuthorMonitoring { Monitor = "none" }
                };
                var result = controller.ImportBook(request).GetAwaiter().GetResult();
                Assert.That(result, Is.TypeOf<CreatedResult>());
                Assert.That((int)((CreatedResult)result).Value.GetType().GetProperty("id").GetValue(((CreatedResult)result).Value), Is.EqualTo(book.Id));
                var otherOwner = h.AddBook(mediaType, "hc:2", "gr:883963", narrator: "Other Narrator");
                var ambiguous = controller.ImportBook(request).GetAwaiter().GetResult() as ObjectResult;
                Assert.That(ambiguous?.StatusCode, Is.EqualTo(409));
                var resource = (ProviderAmbiguityResource)ambiguous.Value;
                Assert.That(resource.Field, Is.EqualTo("foreignEditionId"));
                Assert.That(resource.Candidates.Select(candidate => candidate.Id), Is.EqualTo(new[] { book.Id, otherOwner.Id }));
                Assert.That(h.Books.Get(otherWork.Id).IsMonitored(), Is.False);
                Assert.That(h.Commands, Is.Empty);
            });
        }

        [TestCase(false, "gb")]
        [TestCase(true, "gb")]
        [TestCase(false, "az")]
        [TestCase(true, "az")]
        public void product_identifiers_retain_all_edition_owners_in_native_book_ambiguity(bool postgres, string provider)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var first = h.AddBook(BookMediaType.Audiobook, "hc:1", "gr:10");
                var second = h.AddBook(BookMediaType.Audiobook, "hc:2", "gr:20");
                var opposite = h.AddBook(BookMediaType.Ebook, "hc:3", "gr:30");
                foreach (var book in new[] { first, second, opposite })
                {
                    var edition = h.EditionService.GetEditionsByBook(book.Id).Single();
                    edition.GoogleBooksEditionId = provider == "gb" ? "volume1" : null;
                    edition.Asin = provider == "az" ? "B001" : null;
                    h.Editions.Update(edition);
                }

                var model = h.BookService.GetBook(first.Id);
                if (provider == "gb") model.GoogleBooksId = "volume1";
                else model.ASIN = "B001";
                var ambiguity = ProviderAmbiguityHelper.GetBookAmbiguity(h.BookService, model, "book", null);
                Assert.That(ambiguity, Is.Not.Null);
                Assert.That(ambiguity.ProviderId, Is.EqualTo(provider == "gb" ? "gb:volume1" : "az:B001"));
                Assert.That(ambiguity.Candidates.Select(candidate => candidate.Id), Is.EqualTo(new[] { first.Id, second.Id }));
            });
        }

        [Test]
        public void edition_ambiguity_requires_complete_lookup_evidence()
        {
            var service = Proxy<IBookService>((method, args) =>
            {
                Assert.That(method.Name, Is.EqualTo(nameof(IBookService.FindBookIdsByProviderIds)));
                var key = ((IEnumerable<(string Scope, string Provider, string Id)>)args[0]).Single();
                ((Action<(string Scope, string Provider, string Id)>)args[1])(key);
                Assert.Fail("Incomplete identity lookup must stop the request");
                return null;
            });
            Assert.Throws<InvalidOperationException>(() => ProviderAmbiguityHelper.GetBookAmbiguity(
                service, "gr", "883963", BookMediaType.Audiobook, "foreignEditionId", null, scope: "edition"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void goodreads_work_lookup_preserves_alias_scope_and_absence(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var work = h.AddBook(BookMediaType.Ebook, "hc:1", "gr:2");
                var edition = h.AddBook(BookMediaType.Ebook, "hc:3", "gr:4-ebook");
                h.Aliases.ReplaceAliases("Book", work.Id, "work", new[] { "gr:777" });
                h.Aliases.ReplaceAliases("Book", edition.Id, "edition", new[] { "gr:777" });
                var lookup = h.LookupController();
                Assert.That(lookup.Search("gr:777", "ebook").Value.Select(book => book.Id), Is.EqualTo(new[] { work.Id }));
                Assert.That(lookup.Search("gr-ed:777", "ebook").Value.Select(book => book.Id), Is.EqualTo(new[] { edition.Id }));
                Assert.That(lookup.Search("gr:4", "ebook").Value, Is.Empty);
                Assert.That(lookup.Search("gr-ed:4", "audiobook").Value, Is.Empty);
                Assert.That(lookup.Search("gr:4").Value, Is.Empty);
                Assert.That(lookup.Search("hc:888").Value, Is.Empty);
                Assert.That(lookup.Search("gr-ed:888").Value, Is.Empty);
                Assert.That(h.RemoteLookups, Is.EqualTo(5));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void edition_lookup_keeps_dedicated_columns_and_legacy_foreign_ids_without_duplicates(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var book = h.AddBook(BookMediaType.Audiobook, editionId: "gr:501-audiobook");
                var legacy = h.EditionService.GetEditionsByBook(book.Id).Single();
                var dedicated = h.Editions.Insert(new Edition { BookId = book.Id, Title = "Dedicated", GoodreadsEditionId = 501, HardcoverEditionId = "502" });
                var both = h.Editions.Insert(new Edition { BookId = book.Id, Title = "Both", ForeignEditionId = "hc:502", HardcoverEditionId = "hc:502" });
                h.Editions.Insert(new Edition { BookId = book.Id, Title = "Other provider", ForeignEditionId = "gr:502" });
                foreach (var term in new[] { "501", "gr:501", "gr-ed:501" })
                {
                    Assert.That(h.EditionService.GetEditionsByProviderAndId("gr", term).Select(e => e.Id),
                        Is.EqualTo(new[] { legacy.Id, dedicated.Id }), term);
                }
                foreach (var term in new[] { "502", "hc:502", "edition:502", "hc:edition:502", "hc-ed:502" })
                {
                    Assert.That(h.EditionService.GetEditionsByProviderAndId("hc", term).Select(e => e.Id),
                        Is.EqualTo(new[] { dedicated.Id, both.Id }), term);
                }
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void hardcover_work_and_edition_numbers_are_separate_namespaces(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var work = h.AddBook(BookMediaType.Audiobook, "hc:123", "hc:999");
                var edition = h.AddBook(BookMediaType.Audiobook, "hc:456", "hc:123");
                var lookup = h.LookupController();
                Assert.That(lookup.Search("hc:123", "audiobook").Value.Select(b => b.Id), Is.EqualTo(new[] { work.Id }));
                Assert.That(lookup.Search("hc:edition:123", "audiobook").Value.Select(b => b.Id), Is.EqualTo(new[] { edition.Id }));
                Assert.That(h.RemoteLookups, Is.Zero);
                Assert.That(lookup.Search("hc:999", "audiobook").Value, Is.Empty);
                Assert.That(lookup.Search("hc:edition:456", "audiobook").Value, Is.Empty);
                Assert.That(h.RemoteLookups, Is.EqualTo(2));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void missing_explicit_edition_never_resolves_to_a_same_numbered_work(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var work = h.AddBook(BookMediaType.Audiobook, "hc:123", "hc:999");
                work.GoodreadsWorkId = "gr:123";
                h.Books.Update(work);
                var lookup = h.LookupController();
                foreach (var term in new[] { "hc-ed:123", "hc:edition:123", "gr-ed:123" })
                {
                    Assert.That(lookup.Search(term, "audiobook").Value, Is.Empty, term);
                }
                Assert.That(h.RemoteLookups, Is.EqualTo(3));
                Assert.That(lookup.Search("hc:123", "audiobook").Value.Select(book => book.Id), Is.EqualTo(new[] { work.Id }));
                Assert.That(lookup.Search("gr:123", "audiobook").Value.Select(book => book.Id), Is.EqualTo(new[] { work.Id }));
                Assert.That(h.RemoteLookups, Is.EqualTo(3));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void numeric_column_candidate_must_still_match_the_requested_provider_identity(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var book = h.AddBook(BookMediaType.Audiobook);
                var edition = h.Editions.Insert(new Edition { BookId = book.Id, Title = "Canonical identity", GoodreadsEditionId = 501 });
                Assert.That(h.Editions.FindAllByGoodreadsEditionId(501).Select(row => row.Id), Is.EqualTo(new[] { edition.Id }));
                Assert.That(BookEditionIdentity.EditionMatchesProviderId(edition, "gr:000501"), Is.False);
                Assert.That(h.EditionService.GetEditionsByProviderAndId("gr", "000501"), Is.Empty);
                Assert.That(h.EditionService.GetEditionsByProviderAndId("gr", "501").Select(row => row.Id), Is.EqualTo(new[] { edition.Id }));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void orphan_edition_logs_original_lookup_term_and_keeps_remote_fallback(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                h.Editions.Insert(new Edition { BookId = 999999, Title = "Orphan edition", ForeignEditionId = "hc:404" });
                using var factory = new LogFactory();
                var target = new MemoryTarget { Layout = "${level}|${message}|${exception:format=message}" };
                var config = new LoggingConfiguration();
                config.AddRule(LogLevel.Warn, LogLevel.Fatal, target);
                factory.Configuration = config;
                var lookup = h.LookupController(factory.GetLogger(nameof(BookLookupController)));
                Assert.That(lookup.Search("hc-ed:404", "audiobook").Value, Is.Empty);
                Assert.That(h.RemoteLookups, Is.EqualTo(1));
                Assert.That(target.Logs, Has.Count.EqualTo(1));
                Assert.That(target.Logs.Single(), Does.StartWith("Warn|").And.Contain("hc-ed:404").And.Contain("falling back"));
                Assert.That(target.Logs.Single(), Does.Contain("missing local book"));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void missing_edition_and_alias_owners_do_not_discard_valid_copies(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var book = h.AddBook(BookMediaType.Audiobook, "hc:1", "gr:404-audiobook");
                h.Editions.Insert(new Edition { BookId = 999999, Title = "Orphan edition", ForeignEditionId = "gr:404" });
                h.Aliases.ReplaceAliases("Book", book.Id, "edition", new[] { "gr:405" });
                h.Aliases.ReplaceAliases("Book", 999999, "edition", new[] { "gr:405" });
                h.Aliases.ReplaceAliases("Book", book.Id, "work", new[] { "gr:406" });
                h.Aliases.ReplaceAliases("Book", 999999, "work", new[] { "gr:406" });
                var lookup = h.LookupController();
                foreach (var term in new[] { "gr-ed:404", "gr-ed:405", "gr:406" })
                {
                    var response = lookup.Search(term, "audiobook").Value;
                    Assert.That(response.Select(resource => resource.Id), Is.EqualTo(new[] { book.Id }), term);
                    Assert.That(response.Single().Author.AuthorName, Is.EqualTo(h.Author.Name));
                }
                Assert.That(h.RemoteLookups, Is.Zero);
            });
        }
        [TestCase(false)]
        [TestCase(true)]
        public void edition_alias_lookup_batches_provider_sets_without_scope_or_entity_leaks(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var commands = new List<string>();
                var observed = Proxy<IMainDatabase>((method, args) => method.Name == nameof(IMainDatabase.OpenConnection)
                    ? CommandObserver.Wrap<IDbConnection>(database.OpenConnection(), command => commands.Add(command.CommandText))
                    : method.Invoke(database, args));
                var h = new Harness(observed);
                var book = h.AddBook(BookMediaType.Ebook);
                var aliases = Enumerable.Range(1, 2000).Select(i => new ProviderAlias
                {
                    EntityType = "Book", EntityId = book.Id, Scope = "edition",
                    Provider = i % 3 == 0 ? "gr" : i % 3 == 1 ? "hc" : "az",
                    NormalizedProviderId = i % 3 == 2 ? "B" + i : i.ToString(),
                    CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
                }).ToList();
                var repository = new ProviderAliasRepository(observed, h.Events);
                repository.InsertMany(aliases);
                repository.Insert(new ProviderAlias { EntityType = "Author", EntityId = h.Author.Id,
                    Scope = "edition", Provider = "hc", NormalizedProviderId = "1" });
                repository.Insert(new ProviderAlias { EntityType = "Book", EntityId = book.Id,
                    Scope = "work", Provider = "hc", NormalizedProviderId = "1" });
                var ids = aliases.Select(alias => alias.Provider + ":" + alias.NormalizedProviderId).ToArray();
                commands.Clear();
                var rows = h.Aliases.FindBookAliases("edition", ids.Concat(ids));
                Assert.That(rows.Select(row => row.Id), Is.EquivalentTo(aliases.Select(row => row.Id)));
                Assert.That(commands.Count, Is.LessThanOrEqualTo(postgres ? 1 : 3));
                Assert.That(commands.All(sql => sql.Split("UNION ALL").Length <= 3), Is.True);
                Assert.That(commands.All(sql => sql.Contains("\"EntityType\" = @entityType") &&
                    sql.Contains(postgres ? "= ANY(@providerIds" : "IN (")), Is.True);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void secondary_edition_lookup_maps_real_resources_and_reuses_scoped_alias_index(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var commands = new List<string>();
                var observed = Proxy<IMainDatabase>((method, args) => method.Name == nameof(IMainDatabase.OpenConnection)
                    ? CommandObserver.Wrap<IDbConnection>(database.OpenConnection(), command => commands.Add(command.CommandText))
                    : method.Invoke(database, args));
                var h = new Harness(observed);
                var httpCalls = 0;
                var http = Proxy<NzbDrone.Common.Http.IHttpClient>((method, args) =>
                {
                    Assert.That(method.Name, Is.EqualTo("Get"));
                    httpCalls++;
                    var request = (NzbDrone.Common.Http.HttpRequest)args[0];
                    return new NzbDrone.Common.Http.HttpResponse(request,
                        new NzbDrone.Common.Http.HttpHeader { ContentType = "application/json" },
                        MetadataSource.BookInfo.BookInfoProxyCanonicalLookupFixture.SecondaryEditionAliasesPayload,
                        System.Net.HttpStatusCode.OK);
                });
                var proxy = MetadataSource.BookInfo.BookInfoProxyCanonicalLookupFixture.CreateProxy(http);
                var controller = h.LookupController(searchProxy: proxy);
                foreach (var type in new[] { BookMediaType.Ebook, BookMediaType.Audiobook })
                {
                    var local = h.AddBook(type, "hc:312460", "hc:edition:12250068-" + type.ToString().ToLowerInvariant());
                    commands.Clear();
                    var response = controller.Search("gr-ed:44767458", type.ToString().ToLowerInvariant()).Value.Single();
                    Assert.That(response.MediaType, Is.EqualTo(type.ToString().ToLowerInvariant()));
                    var selected = response.Editions.Single(e => e.ForeignEditionId == "hc:edition:12250068-" + type.ToString().ToLowerInvariant());
                    Assert.That(selected.GoodreadsEditionId, Is.Null);
                    Assert.That((type == BookMediaType.Ebook ? response.LocalEbookBooks : response.LocalAudiobookBooks)
                        .Select(copy => copy.Id), Does.Contain(local.Id));
                    Assert.That(commands.Count, Is.LessThanOrEqualTo(16));
                }
                foreach (var provider in new[] { "gr", "hc" })
                {
                    var context = new DefaultHttpContext();
                    context.Request.Path = "/readarr/" + provider + "/ebook/api/v1/book/lookup";
                    controller.ControllerContext = new ControllerContext { HttpContext = context };
                    List<BookResource> response = null;
                    new ServarrMediaTypeScopeMiddleware(c =>
                    {
                        response = controller.Search("gr-ed:44767458", "audiobook").Value;
                        return System.Threading.Tasks.Task.CompletedTask;
                    }).InvokeAsync(context).GetAwaiter().GetResult();
                    Assert.That(response.Single().MediaType, Is.EqualTo("ebook"));
                    Assert.That(response.Single().Editions.First().ForeignEditionId,
                        Is.EqualTo(provider == "gr" ? "1000049" : "12250068"));
                }
                Assert.That(httpCalls, Is.EqualTo(4));
                var ebook = h.Books.All().Single(b => b.MediaType == BookMediaType.Ebook);
                var audiobook = h.Books.All().Single(b => b.MediaType == BookMediaType.Audiobook);
                var collision = h.AddBook(BookMediaType.Ebook, "hc:44767458", "hc:edition:77777");
                h.Aliases.ReplaceAliases("Book", ebook.Id, "edition", new[] { "gr:44767458", "hc:edition:44767458" });
                h.Aliases.ReplaceAliases("Book", audiobook.Id, "edition", new[] { "gr:44767458", "hc:edition:44767458" });
                h.Aliases.ReplaceAliases("Book", collision.Id, "work", new[] { "gr:44767458", "hc:44767458" });
                controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
                var singleCopyReads = 0;
                foreach (var term in new[] { "gr-ed:44767458", "hc-ed:44767458", "hc:edition:44767458" })
                {
                    commands.Clear();
                    var response = controller.Search(term, "ebook").Value;
                    Assert.That(response.Select(book => book.Id), Is.EqualTo(new[] { ebook.Id }));
                    Assert.That(httpCalls, Is.EqualTo(4));
                    Assert.That(commands.Count, Is.LessThanOrEqualTo(16));
                    if (term == "gr-ed:44767458") singleCopyReads = commands.Count;
                }
                var expectedCopies = new List<int> { ebook.Id };
                for (var i = 0; i < 20; i++)
                {
                    var copy = h.AddBook(BookMediaType.Ebook, "hc:312460", "hc:edition:12250068-ebook");
                    expectedCopies.Add(copy.Id);
                    h.Aliases.ReplaceAliases("Book", copy.Id, "edition", new[] { "gr:44767458" });
                }
                commands.Clear();
                Assert.That(controller.Search("gr-ed:44767458", "ebook").Value.Select(book => book.Id), Is.EqualTo(expectedCopies));
                Assert.That(commands.Count, Is.EqualTo(singleCopyReads), "Copies must not multiply database reads");
                Assert.That(httpCalls, Is.EqualTo(4));
                Assert.That(h.Commands, Is.Empty);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void text_lookup_keeps_url_scope_preview_identity_and_batch_reads(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var commands = new List<string>();
                var observed = Proxy<IMainDatabase>((method, args) => method.Name == nameof(IMainDatabase.OpenConnection)
                    ? CommandObserver.Wrap<IDbConnection>(database.OpenConnection(), command => commands.Add(command.CommandText))
                    : method.Invoke(database, args));
                var h = new Harness(observed);
                var author = new AuthorJsonResource { Id = 58, Name = "Frank Herbert" };
                var previews = new List<SearchJsonResource>
                {
                    new() { WorkId = 3634639, BookId = 44767458, Title = "Dune (Dune, #1)", Author = author },
                    new() { WorkId = 3634570, BookId = 44492285, Title = "Dune Messiah (Dune #2)", Author = author }
                };
                var searches = 0;
                var goodreads = Proxy<IGoodreadsSearchProxy>((method, args) =>
                {
                    Assert.That(method.Name, Is.EqualTo(nameof(IGoodreadsSearchProxy.Search)));
                    Assert.That(args[0], Is.EqualTo("dune"));
                    searches++;
                    return previews;
                });
                var config = Proxy<IConfigService>((method, args) => method.Name == "get_MetadataServerUrl"
                    ? "http://metadata" : throw new AssertionException(method.Name));
                var logger = LogManager.GetCurrentClassLogger();
                var proxy = new BookInfoProxy(null, null, goodreads, null, null, null, null, null, config,
                    new MetadataRequestBuilder(config), logger, new CacheManager(),
                    new MetadataServerHealthGate(config, new MetadataServerHealthService(logger), logger));
                var controller = h.LookupController(searchProxy: proxy);
                foreach (var type in new[] { BookMediaType.Audiobook, BookMediaType.Ebook })
                {
                    var book = h.AddBook(type, "hc:1001", "gr:44767458", narrator: "Local Narrator");
                    book.GoodreadsWorkId = "gr:3634639";
                    h.Books.Update(book);
                }
                var baselineCount = 0;
                foreach (var path in new[] { "/api/v1/book/lookup", "/ebook/api/v1/book/lookup", "/audiobook/api/v1/book/lookup",
                    "/readarr/hc/ebook/api/v1/book/lookup", "/readarr/gr/ebook/api/v1/book/lookup" })
                {
                    foreach (var query in new[] { "", "audiobook", "ebook" })
                    {
                        var context = new DefaultHttpContext();
                        context.Request.Path = path;
                        context.Request.QueryString = new QueryString("?term=dune" + (query.Length == 0 ? "" : "&mediaType=" + query));
                        controller.ControllerContext = new ControllerContext { HttpContext = context };
                        List<BookResource> response = null;
                        commands.Clear(); h.FileBatches.Clear();
                        new ServarrMediaTypeScopeMiddleware(c =>
                        {
                            response = controller.Search("dune", c.Request.Query["mediaType"]).Value;
                            return System.Threading.Tasks.Task.CompletedTask;
                        }).InvokeAsync(context).GetAwaiter().GetResult();
                        var scope = context.GetReadarrFacadeContext()?.MediaType ?? query;
                        var expected = scope == "ebook" ? "ebook" : "audiobook";
                        Assert.Multiple(() =>
                        {
                            Assert.That(response, Has.Count.EqualTo(2), path + " " + query);
                            Assert.That(response.All(book => book.MediaType == expected), Is.True);
                            Assert.That(response[0].GoodreadsWorkId, Is.EqualTo("gr:3634639"));
                            if (context.GetReadarrFacadeContext()?.IsHardcover == true)
                            {
                                Assert.That(response[0].Editions, Is.Empty, "A Goodreads preview must not fabricate a Hardcover edition id");
                            }
                            else
                            {
                                Assert.That(response[0].Editions.Single().GoodreadsEditionId, Is.EqualTo(44767458));
                            }
                            Assert.That(response[0].LocalAudiobookBooks, Has.Count.EqualTo(1));
                            Assert.That(response[0].LocalEbookBooks, Has.Count.EqualTo(1));
                            Assert.That(response[0].LocalAudiobookBooks.Single().Narrator, Is.EqualTo("Local Narrator"));
                            Assert.That(h.FileBatches, Has.Count.EqualTo(1));
                            Assert.That(commands.Count, Is.LessThanOrEqualTo(12));
                        });
                        baselineCount = commands.Count;
                    }
                }

                previews.AddRange(Enumerable.Range(0, 30).Select(i => new SearchJsonResource
                { WorkId = 5000000 + i, BookId = 6000000 + i, Title = "Additional preview " + i, Author = author }));
                controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
                commands.Clear();
                Assert.That(controller.Search("dune", "ebook").Value, Has.Count.EqualTo(32));
                Assert.That(commands, Has.Count.EqualTo(baselineCount), "Preview count must not multiply database reads");
                Assert.That(searches, Is.EqualTo(16), "One autocomplete request per API lookup");
                Assert.That(h.Commands, Is.Empty, "Metadata lookup must not queue acquisition or refresh");
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void lookup_maps_distinct_works_and_all_copies_with_constant_query_count(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var commands = new List<string>();
                var observed = Proxy<IMainDatabase>((method, args) => method.Name == nameof(IMainDatabase.OpenConnection)
                    ? CommandObserver.Wrap<IDbConnection>(database.OpenConnection(), command => commands.Add(command.CommandText))
                    : method.Invoke(database, args));
                var h = new Harness(observed);
                var first = h.AddBook(BookMediaType.Audiobook, "hc:111", "gr:211-audiobook", narrator: "First Narrator");
                var ebook = h.AddBook(BookMediaType.Ebook, "hc:111", "gr:211-ebook");
                var other = h.AddBook(BookMediaType.Audiobook, "hc:112", "hc:212", narrator: "Other Narrator");
                var aliasOnly = h.AddBook(BookMediaType.Audiobook, "hc:113", "hc:213");
                h.Aliases.ReplaceAliases("Book", aliasOnly.Id, "work", new[] { "hc:111" });
                h.Aliases.ReplaceAliases("Author", h.Author.Id, "author", new[] { "hc:111" });
                h.FileRepository.Insert(new BookFile
                {
                    EditionId = h.EditionService.GetEditionsByBook(first.Id).Single().Id,
                    Path = @"C:\Audio\Fixture Author\Owned.m4b", Size = 100, DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow,
                    Quality = new NzbDrone.Core.Qualities.QualityModel(), MediaInfo = new NzbDrone.Core.Parser.Model.MediaInfoModel()
                });
                var controller = h.LookupController();
                var mapper = typeof(BookLookupController).GetMethod("MapToResource", BindingFlags.Instance | BindingFlags.NonPublic);
                List<BookResource> Map(IEnumerable<Book> books)
                {
                    commands.Clear(); h.FileBatches.Clear();
                    return ((IEnumerable<BookResource>)mapper.Invoke(controller, new object[] { books, null, null })).ToList();
                }
                var searchFirst = new Book { HardcoverBookId = "hc:111", Editions = h.EditionService.GetEditionsByBook(first.Id) };
                var searchOther = new Book { HardcoverBookId = "hc:112", Editions = new List<Edition>() };
                var initial = Map(new[] { searchFirst, searchOther });
                var initialCount = commands.Count;
                var copies = Enumerable.Range(0, 26).Select(_ => h.AddBook(BookMediaType.Audiobook, "hc:111", "gr:211-audiobook")).ToList();
                var results = Map(new[] { searchFirst, searchOther }.Concat(copies));
                TestContext.Progress.WriteLine($"Local-copy lookup ({(postgres ? "PostgreSQL" : "SQLite")}): two results {initialCount} SQL statements; 28 results {commands.Count} SQL statements");
                Assert.Multiple(() =>
                {
                    Assert.That(commands, Has.Count.EqualTo(initialCount), "Copies must not multiply provider, edition or file reads");
                    Assert.That(commands.Count, Is.LessThanOrEqualTo(12));
                    Assert.That(h.FileBatches, Has.Count.EqualTo(1));
                    Assert.That(results[0].LocalAudiobookBooks.Select(book => book.Id),
                        Is.EqualTo(new[] { first.Id, aliasOnly.Id }.Concat(copies.Select(book => book.Id)).OrderBy(id => id)));
                    Assert.That(results[0].LocalEbookBooks.Select(book => book.Id), Is.EqualTo(new[] { ebook.Id }));
                    Assert.That(results[0].LocalAudiobookBooks.Single(book => book.Id == first.Id).HasFiles, Is.True);
                    Assert.That(results[0].LocalAudiobookBooks.Single(book => book.Id == aliasOnly.Id).HasFiles, Is.False);
                    Assert.That(results[0].LocalAudiobookBooks.Single(book => book.Id == first.Id).Narrator, Is.EqualTo("First Narrator"));
                    Assert.That(results[1].LocalAudiobookBooks.Select(book => book.Id), Is.EqualTo(new[] { other.Id }));
                    Assert.That(results[1].LocalEbookBooks, Is.Empty);
                    Assert.That(initial[0].LocalAudiobookBooks.Select(book => book.Id), Is.EqualTo(new[] { first.Id, aliasOnly.Id }));
                });
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void batched_provider_lookup_preserves_single_lookup_results_and_json_fallback(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var audio = h.AddBook(BookMediaType.Audiobook, "hc:101", "gr:201-audiobook");
                h.AddBook(BookMediaType.Ebook, "hc:101", "gr:201-ebook");
                var second = h.AddBook(BookMediaType.Audiobook, "hc:102", "hc:edition:202-audiobook");
                var editions = h.EditionService.GetEditionsByBook(audio.Id);
                editions[0].Asin = "B001";
                editions[0].AudibleASIN = "B002";
                editions[0].OpenLibraryEditionId = "OL1M";
                editions[0].GoogleBooksEditionId = "google1";
                h.Editions.UpdateMany(editions);
                var edition = h.EditionService.GetEditionsByBook(second.Id).Single();
                edition.Asins = new List<string> { "B003", null, "b004", "B001" };
                h.Editions.Update(edition);
                foreach (var (provider, ids) in new[]
                {
                    ("hc", new[] { "101", "102", "202", "edition:202", "999" }),
                    ("gr", new[] { "201", "000201", "999" }),
                    ("ol", new[] { "OL1M", "OL2M" }),
                    ("gb", new[] { "google1", "google2" }),
                    ("az", new[] { "B001", "B002", "B003", "B004", "B005" })
                })
                {
                    var batchedEditions = h.EditionService.GetEditionsByProviderIds(provider, ids);
                    foreach (var id in ids)
                    {
                        Assert.That(batchedEditions[id].Select(item => item.Id).OrderBy(value => value),
                            Is.EqualTo(h.EditionService.GetEditionsByProviderAndId(provider, id).Select(item => item.Id).OrderBy(value => value)), provider + ":" + id);
                    }
                    var keys = ids.Where(id => !id.Contains(":"))
                        .SelectMany(id => new[] { "work", "edition" }.Select(scope =>
                            (Scope: scope, Provider: provider, Id: ProviderIdHelper.Canonicalize(id, provider)))).ToArray();
                    var batchedBooks = h.BookService.FindBookIdsByProviderIds(keys);
                    foreach (var key in keys)
                    {
                        var singles = key.Scope == "work"
                            ? h.BookService.FindAllByWorkProviderId(key.Provider, key.Id, BookMediaType.Audiobook)
                                .Concat(h.BookService.FindAllByWorkProviderId(key.Provider, key.Id, BookMediaType.Ebook)).Select(book => book.Id)
                            : h.EditionService.GetEditionsByProviderAndId(key.Provider, ProviderIdHelper.StripPrefix(key.Id)).Select(row => row.BookId);
                        Assert.That(batchedBooks[key].OrderBy(id => id), Is.EqualTo(singles.Distinct().OrderBy(id => id)), key.ToString());
                    }
                }
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void local_copy_summaries_keep_work_and_edition_collisions_separate(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var requested = h.AddBook(BookMediaType.Audiobook, "hc:1", "gr:898777-audiobook");
                requested.GoodreadsWorkId = "gr:883963";
                h.Books.Update(requested);
                var ebook = h.AddBook(BookMediaType.Ebook, "hc:1", "gr:898777-ebook");
                ebook.GoodreadsWorkId = "gr:883963";
                h.Books.Update(ebook);
                var wrongEdition = h.AddBook(BookMediaType.Audiobook, "hc:258207", "gr:883963-audiobook");
                wrongEdition.GoodreadsWorkId = "gr:100379381";
                h.Books.Update(wrongEdition);
                var wrongWork = h.AddBook(BookMediaType.Audiobook, "hc:2", "gr:3");
                wrongWork.GoodreadsWorkId = "gr:898777";
                h.Books.Update(wrongWork);
                h.Aliases.ReplaceAliases("Book", wrongEdition.Id, "edition", new[] { "gr:883963" });
                h.Aliases.ReplaceAliases("Book", wrongWork.Id, "work", new[] { "gr:898777" });
                h.Aliases.ReplaceAliases("Author", h.Author.Id, "author", new[] { "gr:883963" });
                var response = h.LookupController().Search("gr-ed:898777", "audiobook").Value.Single();
                Assert.That(response.Id, Is.EqualTo(requested.Id));
                Assert.That(response.LocalAudiobookBooks.Select(book => book.Id), Is.EqualTo(new[] { requested.Id }));
                Assert.That(response.LocalEbookBooks.Select(book => book.Id), Is.EqualTo(new[] { ebook.Id }));
                var runtime = h.BookService.GetExistingBooks(new[] { requested.Id }).Single();
                runtime.ForeignEditionId = "gr:898777";
                var mapper = typeof(BookLookupController).GetMethod("MapToResource", BindingFlags.Instance | BindingFlags.NonPublic);
                var mapped = ((IEnumerable<BookResource>)mapper.Invoke(h.LookupController(), new object[] { new[] { runtime }, null, null })).Single();
                Assert.That(mapped.LocalAudiobookBooks.Select(book => book.Id), Is.EqualTo(new[] { requested.Id }));
                Assert.That(mapped.LocalEbookBooks.Select(book => book.Id), Is.EqualTo(new[] { ebook.Id }));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void alias_lookup_failure_warns_and_preserves_column_backed_local_copies(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var book = h.AddBook(BookMediaType.Audiobook, "hc:1", "hc:2");
                using var factory = new LogFactory();
                var target = new MemoryTarget { Layout = "${level}|${message}|${exception:format=message}" };
                var config = new LoggingConfiguration();
                config.AddRule(LogLevel.Warn, LogLevel.Fatal, target);
                factory.Configuration = config;
                var aliases = Proxy<IProviderAliasService>((method, args) =>
                {
                    if (method.Name == nameof(IProviderAliasService.FindBookAliases) && (string)args[0] == "work")
                    {
                        throw new InvalidOperationException("Injected alias-index failure");
                    }
                    return method.Invoke(h.Aliases, args);
                });
                var service = new BookService(h.Books, h.EditionService, h.Events, h.AuthorService, h.Files,
                    null, new SeriesBookLinkRepository(database, h.Events), null, factory.GetLogger(nameof(BookService)), providerAliasService: aliases);
                var workKey = (Scope: "work", Provider: "hc", Id: "hc:1");
                var editionKey = (Scope: "edition", Provider: "hc", Id: "hc:2");
                var incomplete = new List<(string Scope, string Provider, string Id)>();
                var matches = service.FindBookIdsByProviderIds(new[] { workKey, editionKey }, incomplete.Add);
                Assert.That(matches[workKey], Is.EqualTo(new[] { book.Id }));
                Assert.That(matches[editionKey], Is.EqualTo(new[] { book.Id }));
                Assert.That(incomplete, Is.EqualTo(new[] { workKey }));
                target.Logs.Clear();
                var controller = new BookLookupController(Proxy<ISearchForNewBook>((m, a) => new List<Book> { book }),
                    Proxy<IMapCoversToLocal>((m, a) => null), service, h.EditionService, h.Files,
                    Proxy<IMediaCoverProxy>((m, a) => null), providerAliasService: h.Aliases)
                { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
                var response = controller.Search("Requested title", "audiobook").Value.Single();
                Assert.That(response.LocalAudiobookBooks.Select(copy => copy.Id), Is.EqualTo(new[] { book.Id }));
                Assert.That(target.Logs, Has.Count.EqualTo(1));
                Assert.That(target.Logs.Single(), Does.StartWith("Warn|").And.Contain("Injected alias-index failure"));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void search_reuses_exact_author_evidence_without_losing_distinct_resources(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var commands = new List<string>();
                var observed = Proxy<IMainDatabase>((method, args) => method.Name == nameof(IMainDatabase.OpenConnection)
                    ? CommandObserver.Wrap<IDbConnection>(database.OpenConnection(), command => commands.Add(command.CommandText))
                    : method.Invoke(database, args));
                var h = new Harness(observed);
                var second = h.Authors.Insert(new NzbDrone.Core.Books.Author
                {
                    Name = "Second Author", CleanName = "secondauthor", SortName = "secondauthor", SortNameLastFirst = "Author, Second",
                    TitleSlug = "second-author", Path = @"C:\Audio\Second Author", Added = DateTime.UtcNow
                });
                h.Aliases.ReplaceAliases("Author", h.Author.Id, "author", new[] { "hc:9001" });
                h.Aliases.ReplaceAliases("Author", second.Id, "author", new[] { "hc:9002" });
                var controller = new Chaptarr.Api.V1.Search.SearchController(null,
                    Proxy<NzbDrone.Core.Organizer.IBuildFileNames>((m, a) => "Author folder"),
                    Proxy<IMapCoversToLocal>((m, a) => null), Proxy<IMediaCoverProxy>((m, a) => null),
                    h.AuthorService, h.BookService, h.Aliases);
                var mapper = typeof(Chaptarr.Api.V1.Search.SearchController).GetMethod("MapToResource", BindingFlags.Instance | BindingFlags.NonPublic);
                List<Chaptarr.Api.V1.Search.SearchResource> Map(int copies)
                {
                    var authors = Enumerable.Range(0, copies).Select(index => new NzbDrone.Core.Books.Author
                    { Name = "Display spelling " + index, HardcoverAuthorId = "hc:9001" }).ToList();
                    authors.Add(new NzbDrone.Core.Books.Author { Name = "Same display name", HardcoverAuthorId = "hc:9999",
                        RemoteProviderIds = new HashSet<string> { "hc:9001" } });
                    authors.Add(new NzbDrone.Core.Books.Author { Name = "Same display name", HardcoverAuthorId = "hc:9999",
                        RemoteProviderIds = new HashSet<string> { "hc:9002" } });
                    authors.Add(new NzbDrone.Core.Books.Author { Name = "Unidentified one" });
                    authors.Add(new NzbDrone.Core.Books.Author { Name = "Unidentified two" });
                    commands.Clear();
                    return ((IEnumerable<Chaptarr.Api.V1.Search.SearchResource>)mapper.Invoke(controller, new object[] { authors, null, null })).ToList();
                }
                var single = Map(1);
                var initialReads = commands.Count;
                var many = Map(21);
                TestContext.Progress.WriteLine($"Author lookup ({(postgres ? "PostgreSQL" : "SQLite")}): one repeated author {initialReads} SQL statements; 21 repeated authors {commands.Count} SQL statements");
                Assert.That(commands, Has.Count.EqualTo(initialReads), "Repeated author evidence must not multiply database reads");
                Assert.That(many, Has.Count.EqualTo(25));
                Assert.That(many.Take(21).Select(resource => resource.Author.Id), Is.All.EqualTo(h.Author.Id));
                Assert.That(many.Take(21).Select(resource => resource.Author.AuthorName),
                    Is.EqualTo(Enumerable.Range(0, 21).Select(index => "Display spelling " + index)));
                Assert.That(many.Skip(21).Select(resource => resource.Author.Id), Is.EqualTo(new[] { h.Author.Id, second.Id, 0, 0 }));
                Assert.That(single.Skip(1).Select(resource => resource.Author.Id), Is.EqualTo(new[] { h.Author.Id, second.Id, 0, 0 }));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void author_lookup_failures_warn_once_per_identity_and_keep_other_provider_fallback(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                using var factory = new LogFactory();
                var target = new MemoryTarget { Layout = "${level}|${message}|${exception:format=message}" };
                var config = new LoggingConfiguration();
                config.AddRule(LogLevel.Warn, LogLevel.Fatal, target);
                factory.Configuration = config;
                var aliases = Proxy<IProviderAliasService>((method, args) =>
                {
                    if (method.Name == nameof(IProviderAliasService.FindAuthorIds))
                    {
                        throw new InvalidOperationException("Injected author alias failure");
                    }
                    return method.Invoke(h.Aliases, args);
                });
                var authors = Proxy<IAuthorService>((method, args) =>
                {
                    Assert.That(method.Name, Is.EqualTo(nameof(IAuthorService.FindByProviderId)));
                    if ((string)args[0] == "hc")
                    {
                        throw new InvalidOperationException("Injected author column failure");
                    }
                    Assert.That((string)args[0], Is.EqualTo("gr"));
                    return h.Authors.Get(h.Author.Id);
                });
                var controller = new Chaptarr.Api.V1.Search.SearchController(null,
                    Proxy<NzbDrone.Core.Organizer.IBuildFileNames>((m, a) => "Author folder"),
                    Proxy<IMapCoversToLocal>((m, a) => null), Proxy<IMediaCoverProxy>((m, a) => null),
                    authors, h.BookService, aliases, factory.GetLogger(nameof(Chaptarr.Api.V1.Search.SearchController)));
                var results = Enumerable.Range(0, 21).Select(_ => new NzbDrone.Core.Books.Author
                { Name = "Requested author", HardcoverAuthorId = "hc:9001", GoodreadsAuthorId = "gr:9001" }).ToList();
                var mapper = typeof(Chaptarr.Api.V1.Search.SearchController).GetMethod("MapToResource", BindingFlags.Instance | BindingFlags.NonPublic);
                var resources = ((IEnumerable<Chaptarr.Api.V1.Search.SearchResource>)mapper.Invoke(controller, new object[] { results, null, null })).ToList();
                Assert.That(resources, Has.Count.EqualTo(21));
                Assert.That(resources.Select(resource => resource.Author.Id), Is.All.EqualTo(h.Author.Id));
                Assert.That(target.Logs, Has.Count.EqualTo(2));
                Assert.That(target.Logs.All(log => log.StartsWith("Warn|")), Is.True);
                Assert.That(target.Logs[0], Does.Contain("Injected author alias failure").And.Contain("provider-column lookup remains available"));
                Assert.That(target.Logs[1], Does.Contain("Injected author column failure").And.Contain("provider hc"));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void work_lookup_hydrates_series_once_for_all_returned_copies(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var commands = new List<string>();
                var observed = Proxy<IMainDatabase>((method, args) => method.Name == nameof(IMainDatabase.OpenConnection)
                    ? CommandObserver.Wrap<IDbConnection>(database.OpenConnection(), command => commands.Add(command.CommandText))
                    : method.Invoke(database, args));
                var h = new Harness(observed);
                var links = new SeriesBookLinkRepository(database, h.Events);
                var series = new SeriesRepository(database, h.Events, links, LogManager.GetCurrentClassLogger())
                    .Insert(new Series { Title = "Series fixture", TitleSlug = "series-fixture", GoodreadsSeriesId = "gr:301" });
                void Add()
                {
                    var book = h.AddBook(BookMediaType.Audiobook);
                    links.Insert(new SeriesBookLink { BookId = book.Id, SeriesId = series.Id, Position = "1" });
                }
                Add();
                var controller = h.LookupController();
                commands.Clear();
                var initial = controller.Search("hc:1001", "audiobook").Value;
                var count = commands.Count;
                for (var i = 0; i < 26; i++)
                {
                    Add();
                }
                commands.Clear();
                var results = controller.Search("hc:1001", "audiobook").Value;
                Assert.That(results, Has.Count.EqualTo(27));
                Assert.That(commands, Has.Count.EqualTo(count));
                Assert.That(results.All(book => book.SeriesTitle.Contains(series.Title)), Is.True);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void unscoped_work_lookup_loads_both_media_rows_in_one_rich_batch(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var commands = new List<string>();
                var observed = Proxy<IMainDatabase>((method, args) => method.Name == nameof(IMainDatabase.OpenConnection)
                    ? CommandObserver.Wrap<IDbConnection>(database.OpenConnection(), command => commands.Add(command.CommandText))
                    : method.Invoke(database, args));
                var h = new Harness(observed);
                var audio = h.AddBook(BookMediaType.Audiobook, "hc:900", "gr:901", narrator: "Audio Narrator");
                var ebook = h.AddBook(BookMediaType.Ebook, "hc:900", "gr:901", narrator: "Ebook Narrator");
                h.FileRepository.Insert(new BookFile
                {
                    EditionId = h.EditionService.GetEditionsByBook(ebook.Id).Single().Id,
                    Path = @"C:\Ebooks\Fixture Author\Owned.epub", Size = 100, DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow,
                    Quality = new NzbDrone.Core.Qualities.QualityModel(), MediaInfo = new NzbDrone.Core.Parser.Model.MediaInfoModel()
                });
                foreach (var book in new[] { audio, ebook })
                {
                    book.GoodreadsWorkId = "gr:900";
                    h.Books.Update(book);
                }
                var controller = h.LookupController();
                commands.Clear();
                var scoped = controller.Search("gr:900", "audiobook").Value;
                var scopedReads = commands.Count;
                commands.Clear();
                var all = controller.Search("gr:900").Value;
                TestContext.Progress.WriteLine($"Work lookup ({(postgres ? "PostgreSQL" : "SQLite")}): scoped {scopedReads} SQL statements; both media {commands.Count} SQL statements");
                Assert.That(commands, Has.Count.EqualTo(scopedReads), "Omitting media scope must not reload the same evidence and rich relationships for each enum");
                Assert.That(scoped.Select(book => book.Id), Is.EqualTo(new[] { audio.Id }));
                Assert.That(all.Select(book => book.Id), Is.EqualTo(new[] { audio.Id, ebook.Id }));
                Assert.That(all.All(book => book.Author.AuthorName == h.Author.Name && book.Editions.Count == 1), Is.True);
                Assert.That(all.All(book => book.LocalAudiobookBooks.Single().Id == audio.Id && book.LocalEbookBooks.Single().Id == ebook.Id), Is.True);
                Assert.That(all[0].Narrator, Is.EqualTo("Audio Narrator"));
                Assert.That(scoped.Single().LocalEbookBooks.Single().HasFiles, Is.True);
                Assert.That(scoped.Single().LocalAudiobookBooks.Single().HasFiles, Is.False);
                Assert.That(all.All(book => book.LocalEbookBooks.Single().HasFiles), Is.True);
                Assert.That(h.RemoteLookups, Is.Zero);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void rich_work_lookup_keeps_secondary_work_aliases_in_every_local_summary(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var books = new List<Book>();
                foreach (var media in new[] { BookMediaType.Audiobook, BookMediaType.Ebook })
                {
                    books.Add(h.AddBook(media, "hc:900", "hc:edition:901"));
                    var secondary = h.AddBook(media, "hc:902", "hc:edition:903");
                    h.Aliases.ReplaceAliases("Book", secondary.Id, "work", new[] { "hc:900" });
                    books.Add(secondary);
                }
                var resources = h.LookupController().Search("hc:900", "audiobook").Value;
                Assert.That(resources.Select(book => book.Id), Is.EqualTo(books.Where(book => book.MediaType == BookMediaType.Audiobook).Select(book => book.Id)));
                foreach (var resource in resources)
                {
                    Assert.That(resource.LocalAudiobookBooks.Select(book => book.Id), Is.EqualTo(books.Where(book => book.MediaType == BookMediaType.Audiobook).Select(book => book.Id)));
                    Assert.That(resource.LocalEbookBooks.Select(book => book.Id), Is.EqualTo(books.Where(book => book.MediaType == BookMediaType.Ebook).Select(book => book.Id)));
                }
                Assert.That(h.RemoteLookups, Is.Zero);
                Assert.That(h.Commands, Is.Empty);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void asin_queries_use_existing_partial_indexes_and_keep_media_scope(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                string sql = null;
                DynamicParameters parameters = null;
                var observed = Proxy<IMainDatabase>((method, args) => method.Name == nameof(IMainDatabase.OpenConnection)
                    ? CommandObserver.Wrap<IDbConnection>(database.OpenConnection(), command =>
                    {
                        if (!command.CommandText.Contains("@asin", StringComparison.Ordinal))
                        {
                            return;
                        }

                        sql = command.CommandText;
                        parameters = new DynamicParameters();
                        foreach (IDataParameter parameter in command.Parameters)
                        {
                            parameters.Add(parameter.ParameterName, parameter.Value);
                        }
                    }) : method.Invoke(database, args));
                var h = new Harness(observed);
                var audio = h.AddBook(BookMediaType.Audiobook);
                var ebook = h.AddBook(BookMediaType.Ebook);
                var selected = h.EditionService.GetEditionsByBook(audio.Id).Single();
                selected.Asin = "B001"; h.Editions.Update(selected);
                selected = h.EditionService.GetEditionsByBook(ebook.Id).Single();
                selected.AudibleASIN = "B001"; h.Editions.Update(selected);
                h.Editions.InsertMany(Enumerable.Range(0, 10000).Select(i => new Edition
                { BookId = audio.Id, Title = "Plan fixture", ForeignEditionId = "hc:" + (10000 + i) }).ToList());
                using var connection = database.OpenConnection();
                if (postgres)
                {
                    connection.Execute("ANALYZE \"Editions\"");
                }
                Assert.That(h.Editions.FindAllByAsin("b001", BookMediaType.Audiobook).Select(item => item.BookId), Is.EqualTo(new[] { audio.Id }));
                var plan = string.Join("\n", connection.Query<string>("EXPLAIN " + (postgres ? string.Empty : "QUERY PLAN ") + sql, parameters));
                if (!postgres)
                {
                    plan = string.Join("\n", connection.Query(sql: "EXPLAIN QUERY PLAN " + sql, param: parameters).Select(row => (string)row.detail));
                }
                Assert.That(plan, Does.Contain(postgres ? "ix_editions_asin_upper" : "IX_Editions_Asin_Partial").IgnoreCase);
                Assert.That(plan, Does.Contain(postgres ? "ix_editions_audibleasin_upper" : "IX_Editions_AudibleASIN_Partial").IgnoreCase);
                var batched = h.EditionService.GetEditionsByProviderIds("az", new[] { "B001", "B002" });
                Assert.That(batched["B001"].Select(item => item.BookId), Is.EquivalentTo(new[] { audio.Id, ebook.Id }));
            });
        }
    }
}
