using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Chaptarr;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.ImportLists.LazyLibrarianImport;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RootFolders;
using Newtonsoft.Json;
using static Chaptarr.Core.Test.Books.AuthorLibraryServiceSpecificBookMonitoringFixture;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.ImportLists
{
    [TestFixture]
    [NonParallelizable]
    public class ImportListSourceAuthorCohortFixture
    {
        [TestCase(false, false, false)]
        [TestCase(true, false, false)]
        [TestCase(false, false, true)]
        [TestCase(true, false, true)]
        [TestCase(false, true, false)]
        [TestCase(true, true, false)]
        [TestCase(false, true, true)]
        [TestCase(true, true, true)]
        public void all_list_sync_should_merge_each_sources_format_request_for_one_author(bool postgres, bool existingAudio, bool ebookFirst)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var logger = LogManager.GetCurrentClassLogger();
                var events = Proxy<IEventAggregator>((method, args) => null);
                var author = new Author
                {
                    GoodreadsAuthorId = "gr:7001", Name = "Source Cohort Author", CleanName = "sourcecohortauthor",
                    SortName = "sourcecohortauthor", SortNameLastFirst = "Author, Source Cohort", TitleSlug = "source-cohort-author",
                    Path = @"C:\Existing\Source Cohort Author".AsOsAgnostic(), Added = DateTime.UtcNow
                };
                var localAuthors = new List<Author>();
                if (existingAudio)
                {
                    author.AudiobookQualityProfileId = 90;
                    author.AudiobookMetadataProfileId = 91;
                    new AuthorRepository(database, events, logger).Insert(author);
                    localAuthors.Add(author);
                }
                var authorService = Proxy<IAuthorService>((method, args) => method.Name switch
                {
                    nameof(IAuthorService.GetAllAuthors) => localAuthors,
                    nameof(IAuthorService.GetExistingAuthors) => localAuthors,
                    nameof(IAuthorService.FindByProviderId) => localAuthors.FirstOrDefault(),
                    _ => throw new AssertionException(method.Name)
                });
                var editionService = new EditionService(new EditionRepository(database, events), events, logger);
                var bookRepository = new BookRepository(database, events);
                if (existingAudio)
                {
                    bookRepository.Insert(new Book { AuthorId = author.Id, GoodreadsWorkId = "gr:8001", Title = "First Source Work",
                        CleanTitle = "firstsourcework", MediaType = BookMediaType.Audiobook, Added = DateTime.UtcNow });
                }
                var books = new BookService(bookRepository, editionService, events, authorService,
                    Proxy<IMediaFileService>((method, args) => new List<BookFile>()), null,
                    new SeriesBookLinkRepository(database, events), null, logger);
                var audiobookRoot = BuildAudiobookRoot(@"C:\Audio".AsOsAgnostic());
                var ebookRoot = BuildEbookRoot(@"C:\Ebooks".AsOsAgnostic());
                var roots = new StubRootFolderService(audiobookRoot, ebookRoot);
                var audio = new ImportListDefinition
                {
                    Id = 1, Name = "Audio source", Implementation = "LazyLibrarianImport", EnableAutomaticAdd = true,
                    ShouldMonitor = ImportListMonitorType.SpecificBook, ShouldMonitorExisting = false, ShouldSearch = true,
                    MonitoringOptions = new ImportListMonitoringOptions { AddMissingAuthors = true, AudiobookMonitored = false },
                    Settings = new LazyLibrarianImportSettings { MonitorAudiobooks = true, MonitorEbooks = false,
                        AudiobookRootFolderPath = audiobookRoot.Path, AudiobookQualityProfileId = 3, AudiobookMetadataProfileId = 4 }
                };
                var ebook = new ImportListDefinition
                {
                    Id = 2, Name = "Ebook source", Implementation = "ChaptarrImport", EnableAutomaticAdd = true,
                    ShouldMonitor = ImportListMonitorType.SpecificBook, ShouldMonitorExisting = false, ShouldSearch = true,
                    MonitoringOptions = new ImportListMonitoringOptions { AddMissingAuthors = true, EbookMonitored = false },
                    Settings = new ChaptarrSettings { MonitorAudiobooks = false, MonitorEbooks = true,
                        EbookRootFolderPath = ebookRoot.Path, EbookQualityProfileId = 5, EbookMetadataProfileId = 6 }
                };
                var definitions = ebookFirst ? new[] { ebook, audio } : new[] { audio, ebook };
                IImportList Provider(ImportListDefinition definition) => Proxy<IImportList>((method, args) => method.Name == "get_Definition"
                    ? definition : throw new AssertionException(method.Name));
                var factory = Proxy<IImportListFactory>((method, args) => method.Name switch
                {
                    nameof(IImportListFactory.AutomaticAddEnabled) => definitions.Select(Provider).ToList(),
                    nameof(IImportListFactory.Get) => definitions.Single(list => list.Id == (int)args[0]),
                    _ => throw new AssertionException(method.Name)
                });
                var reports = definitions.Select(list => new ImportListItemInfo
                {
                    ImportListId = list.Id, Author = author.Name, AuthorProviderId = author.GoodreadsAuthorId,
                    Book = list.Id == 1 ? "First Source Work" : "Second Source Work", BookProviderId = list.Id == 1 ? "gr:8001" : "gr:8002"
                }).ToList();
                var metadataCalls = new System.Collections.Concurrent.ConcurrentBag<(string Id, BookMediaType Side)>();
                var metadata = Proxy<IProvideBookInfo>((method, args) =>
                {
                    metadataCalls.Add(((string)args[0], (BookMediaType)args[1]));
                    return Tuple.Create(author.GoodreadsAuthorId, new Book { GoodreadsWorkId = (string)args[0],
                        Title = (string)args[0] == "gr:8001" ? "First Source Work" : "Second Source Work",
                        Author = author, MediaType = (BookMediaType)args[1] }, new List<Author> { author });
                });
                var pendingRepository = new PendingAuthorImportRepository(database, events);
                var pending = new PendingAuthorImportService(pendingRepository, authorService, events, logger);
                var writes = new List<MonitoringConfig>();
                var writer = Proxy<IPendingAuthorImportService>((method, args) =>
                {
                    if (method.Name == nameof(IPendingAuthorImportService.EnqueueAsync)) writes.Add((MonitoringConfig)args[1]);
                    return method.Invoke(pending, args);
                });
                var commands = new List<Command>();
                var queue = Proxy<IManageCommandQueue>((method, args) =>
                {
                    commands.Add((Command)args[0]);
                    return new CommandModel { Body = (Command)args[0] };
                });
                new ImportListSyncService(factory, Proxy<IImportListExclusionService>((method, args) => new List<ImportListExclusion>()),
                    Proxy<IFetchAndParseImportList>((method, args) => reports), metadata, authorService, books, editionService, writer,
                    roots, new RootFolderSettingsResolver(roots), events, queue, logger,
                    new ImportListBookIdentityCacheRepository(database, events)).Execute(new ImportListSyncCommand());
                var saved = pending.GetByProviderId(author.GoodreadsAuthorId);
                Assert.That(saved, Is.Not.Null);
                Assert.Multiple(() =>
                {
                    Assert.That(pendingRepository.All(), Has.Count.EqualTo(1));
                    Assert.That(saved.HasAudiobook(), Is.EqualTo(!existingAudio));
                    Assert.That(saved.HasEbook(), Is.True);
                    Assert.That(JsonConvert.DeserializeObject<List<string>>(saved.AudiobookBooksToMonitor ?? "[]"),
                        Is.EquivalentTo(existingAudio ? Array.Empty<string>() : new[] { "gr:8001" }));
                    Assert.That(JsonConvert.DeserializeObject<List<string>>(saved.EbookBooksToMonitor ?? "[]"), Is.EqualTo(new[] { "gr:8002" }));
                    Assert.That(saved.AudiobookRootFolderPath, Is.EqualTo(existingAudio ? null : audiobookRoot.Path));
                    Assert.That(saved.EbookRootFolderPath, Is.EqualTo(ebookRoot.Path));
                    Assert.That(writes, Has.Count.EqualTo(existingAudio ? 1 : 2));
                    Assert.That(commands.OfType<ProcessPendingImportsCommand>().Count(), Is.EqualTo(1));
                    Assert.That(metadataCalls, Has.Count.EqualTo(existingAudio ? 1 : 2));
                    Assert.That(metadataCalls, Does.Contain(("gr:8002", BookMediaType.Ebook)));
                    Assert.That(author.AudiobookQualityProfileId, Is.EqualTo(existingAudio ? (int?)90 : null));
                    Assert.That(author.AudiobookMetadataProfileId, Is.EqualTo(existingAudio ? (int?)91 : null));
                });
            });
        }
    }
}
