using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test.Books;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using Newtonsoft.Json;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Chaptarr;
using NzbDrone.Core.ImportLists.Goodreads;
using NzbDrone.Core.ImportLists.Hardcover.Library;
using NzbDrone.Core.ImportLists.LazyLibrarianImport;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.RootFolders;
using static Chaptarr.Core.Test.Books.AuthorLibraryServiceSpecificBookMonitoringFixture;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.ImportLists
{
    [TestFixture]
    [NonParallelizable]
    public class ImportListCommonConfigurationFixture
    {
        private static IEnumerable<TestCaseData> SelectedSides()
        {
            foreach (var provider in Enumerable.Range(0, 6))
            foreach (var postgres in new[] { false, true })
            foreach (var sides in new[] { 1, 2, 3 })
            {
                yield return new TestCaseData(provider, postgres, sides);
            }
        }

        private static IEnumerable<TestCaseData> Providers()
        {
            foreach (var provider in Enumerable.Range(0, 6))
            foreach (var postgres in new[] { false, true })
            {
                yield return new TestCaseData(provider, postgres);
            }
        }

        private static DualMediaImportListSettings Settings(int provider) => provider switch
        {
            0 => new HardcoverLibraryImportListSettings(),
            1 => new GoodreadsBookshelfImportListSettings(),
            2 => new GoodreadsListImportListSettings(),
            3 => new GoodreadsSeriesImportListSettings(),
            4 => new ChaptarrSettings(),
            _ => new LazyLibrarianImportSettings()
        };

        [TestCaseSource(nameof(SelectedSides))]
        public void every_provider_should_persist_only_selected_format_settings(int provider, bool postgres, int sides)
        {
            Verify(provider, postgres, sides, false, 1);
        }

        [TestCaseSource(nameof(Providers))]
        public void missing_format_should_not_carry_targets_or_settings_for_configured_format(int provider, bool postgres)
        {
            Verify(provider, postgres, 3, true, 1);
        }

        [TestCaseSource(nameof(Providers))]
        public void multiple_authors_should_reuse_selected_roots_and_source_cohorts(int provider, bool postgres)
        {
            Verify(provider, postgres, 3, false, 3);
        }

        [TestCaseSource(nameof(Providers))]
        public void cleared_format_tags_should_not_reapply_list_classification_tags(int provider, bool postgres)
        {
            Verify(provider, postgres, 3, false, 1, true);
        }

        private static void Verify(int provider, bool postgres, int sides, bool existingAudio, int authorCount, bool clearTags = false)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var logger = LogManager.GetCurrentClassLogger();
                var events = Proxy<IEventAggregator>((method, args) => null);
                var repository = new AuthorRepository(database, events, logger);
                var authors = Enumerable.Range(0, authorCount).Select(index => new Author
                {
                    Name = "Common Settings Author " + index, CleanName = "commonsettingsauthor" + index,
                    SortName = "commonsettingsauthor" + index, SortNameLastFirst = "Author, Common Settings " + index,
                    TitleSlug = "common-settings-author-" + index, GoodreadsAuthorId = "gr:" + (7001 + index),
                    Path = @"C:\Existing\Common Settings Author".AsOsAgnostic(), Added = DateTime.UtcNow
                }).ToList();
                if (existingAudio)
                {
                    authors[0].AudiobookQualityProfileId = 90;
                    authors[0].AudiobookMetadataProfileId = 91;
                    repository.Insert(authors[0]);
                }
                var localAuthors = existingAudio ? authors : new List<Author>();
                var authorService = Proxy<IAuthorService>((method, args) => method.Name switch
                {
                    nameof(IAuthorService.GetAllAuthors) => localAuthors,
                    nameof(IAuthorService.GetExistingAuthors) => localAuthors,
                    nameof(IAuthorService.GetAuthor) => localAuthors.Single(author => author.Id == (int)args[0]),
                    nameof(IAuthorService.FindByProviderId) => localAuthors.FirstOrDefault(),
                    _ => throw new AssertionException(method.Name)
                });
                var editions = new EditionService(new EditionRepository(database, events), events, logger);
                var books = new BookService(new BookRepository(database, events), editions, events, authorService,
                    Proxy<IMediaFileService>((method, args) => new List<BookFile>()), null,
                    new SeriesBookLinkRepository(database, events), null, logger);
                if (existingAudio)
                {
                    books.AddBook(new Book
                    {
                        AuthorId = authors[0].Id, Author = authors[0], Title = "Common Settings Work 0",
                        CleanTitle = "commonsettingswork0", GoodreadsWorkId = "gr:8001",
                        MediaType = BookMediaType.Audiobook, Added = DateTime.UtcNow,
                        Editions = new List<Edition> { new() { GoodreadsEditionId = 9001, ForeignEditionId = "gr:9001",
                            Title = "Common Settings Work 0", ReadingFormatId = 2 } }
                    });
                }
                var audiobookRoot = BuildAudiobookRoot(@"C:\Audio".AsOsAgnostic());
                audiobookRoot.SetAudiobookSettings(new MediaTypeSettings
                {
                    QualityProfileId = 3, MetadataProfileId = 4, Monitored = false,
                    MonitorExistingMode = MonitorTypes.Missing, MonitorNewItems = NewItemMonitorTypes.New
                });
                var ebookRoot = BuildEbookRoot(@"C:\Ebooks".AsOsAgnostic());
                ebookRoot.SetEbookSettings(new MediaTypeSettings
                {
                    QualityProfileId = 5, MetadataProfileId = 6, Monitored = false,
                    MonitorExistingMode = clearTags ? MonitorTypes.All : null, MonitorNewItems = NewItemMonitorTypes.All
                });
                var rootStub = new StubRootFolderService(audiobookRoot, ebookRoot);
                var rootReads = new List<string>();
                var roots = Proxy<IRootFolderService>((method, args) =>
                {
                    if (method.Name == nameof(IRootFolderService.GetBestRootFolder)) rootReads.Add((string)args[0]);
                    return method.Invoke(rootStub, args);
                });
                var pending = new PendingAuthorImportService(new PendingAuthorImportRepository(database, events), authorService, events, logger);
                var enqueued = new List<MonitoringConfig>();
                var writer = Proxy<IPendingAuthorImportService>((method, args) =>
                {
                    if (method.Name == nameof(IPendingAuthorImportService.EnqueueAsync)) enqueued.Add((MonitoringConfig)args[1]);
                    return method.Invoke(pending, args);
                });
                var commands = new List<Command>();
                var queue = Proxy<IManageCommandQueue>((method, args) =>
                {
                    commands.Add((Command)args[0]);
                    return new CommandModel { Body = (Command)args[0] };
                });
                var reports = authors.SelectMany((author, index) => Enumerable.Range(0, authorCount == 1 ? 1 : 64).Select(_ => new ImportListItemInfo
                {
                    ImportListId = 1, Author = author.Name, AuthorProviderId = author.GoodreadsAuthorId,
                    Book = "Common Settings Work " + index, BookProviderId = "gr:" + (8001 + index)
                })).ToList();
                var metadataReads = new System.Collections.Concurrent.ConcurrentBag<(string Id, BookMediaType Side)>();
                ImportListSyncServiceSearchOnAddFixture.BuildMissingMediaSideService(authors[0],
                    new Book(), (sides & 1) != 0 ? BookMediaType.Audiobook : BookMediaType.Ebook, false,
                    authorService, books, editions, writer, roots, queue, bothSides: sides == 3,
                    newAuthor: !existingAudio, reports: reports, keep: false,
                    metadataLookup: (id, side) => metadataReads.Add((id, side)),
                    configureList: list =>
                    {
                        var settings = Settings(provider);
                        settings.MonitorAudiobooks = (sides & 1) != 0;
                        settings.MonitorEbooks = (sides & 2) != 0;
                        settings.AudiobookRootFolderPath = audiobookRoot.Path;
                        settings.EbookRootFolderPath = ebookRoot.Path;
                        settings.AudiobookQualityProfileId = 13;
                        settings.EbookMetadataProfileId = 16;
                        settings.AudiobookTags = clearTags ? new List<int>() : new List<int> { 20 };
                        settings.EbookTags = clearTags ? new List<int>() : new List<int> { 30 };
                        list.Settings = settings;
                        list.Tags = new HashSet<int> { 10 };
                        list.RootFolderPath = @"C:\Ignored".AsOsAgnostic();
                        list.QualityProfileId = 99;
                        list.MetadataProfileId = 98;
                        list.MonitoringOptions = new ImportListMonitoringOptions { AddMissingAuthors = true };
                        if (clearTags)
                        {
                            var lists = new ImportListRepository(database, events);
                            list.Id = 0;
                            list.Implementation = provider switch
                            {
                                0 => "HardcoverLibraryImportList", 1 => "GoodreadsBookshelfImportList",
                                2 => "GoodreadsListImportList", 3 => "GoodreadsSeriesImportList",
                                4 => "ChaptarrImport", _ => "LazyLibrarianImport"
                            };
                            settings.AudiobookTags = new List<int> { 10, 20 };
                            settings.EbookTags = new List<int> { 10, 30 };
                            lists.Insert(list);
                            settings.AudiobookTags.Clear();
                            settings.EbookTags.Clear();
                            lists.UpdateSettings(list);
                            var savedList = lists.Get(list.Id);
                            list.Settings = savedList.Settings;
                            list.Tags = savedList.Tags;
                            Assert.That(((DualMediaImportListSettings)list.Settings).AudiobookTags, Is.Empty);
                            Assert.That(((DualMediaImportListSettings)list.Settings).EbookTags, Is.Empty);
                            Assert.That(list.Tags, Is.EqualTo(new[] { 10 }));
                        }
                    }).Execute(new ImportListSyncCommand(1));
                var expectedAudio = (sides & 1) != 0 && !existingAudio;
                var expectedEbook = (sides & 2) != 0;
                Assert.That(enqueued, Has.Count.EqualTo(authorCount));
                for (var index = 0; index < authorCount; index++)
                {
                    var saved = pending.GetByProviderId(authors[index].GoodreadsAuthorId);
                    var config = enqueued[index];
                    Assert.That(saved, Is.Not.Null);
                    Assert.Multiple(() =>
                    {
                        Assert.That(saved.HasAudiobook(), Is.EqualTo(expectedAudio));
                        Assert.That(saved.HasEbook(), Is.EqualTo(expectedEbook));
                        Assert.That(saved.AudiobookRootFolderPath, Is.EqualTo(expectedAudio ? audiobookRoot.Path : null));
                        Assert.That(saved.EbookRootFolderPath, Is.EqualTo(expectedEbook ? ebookRoot.Path : null));
                        Assert.That(saved.AudiobookQualityProfileId, Is.EqualTo(expectedAudio ? (int?)13 : null));
                        Assert.That(saved.AudiobookMetadataProfileId, Is.EqualTo(expectedAudio ? (int?)4 : null));
                        Assert.That(saved.EbookQualityProfileId, Is.EqualTo(expectedEbook ? (int?)5 : null));
                        Assert.That(saved.EbookMetadataProfileId, Is.EqualTo(expectedEbook ? (int?)16 : null));
                        Assert.That(saved.AudiobookMonitorExistingMode, Is.EqualTo(expectedAudio ? (MonitorTypes?)MonitorTypes.Missing : null));
                        Assert.That(saved.EbookMonitorExistingMode, Is.EqualTo(expectedEbook && clearTags ? (MonitorTypes?)MonitorTypes.All : null));
                        Assert.That(JsonConvert.DeserializeObject<HashSet<int>>(saved.AudiobookTags ?? "[]"), Is.EquivalentTo(expectedAudio && !clearTags ? new[] { 20 } : Array.Empty<int>()));
                        Assert.That(JsonConvert.DeserializeObject<HashSet<int>>(saved.EbookTags ?? "[]"), Is.EquivalentTo(expectedEbook && !clearTags ? new[] { 30 } : Array.Empty<int>()));
                        Assert.That(config.AudiobookBooksToMonitor, expectedAudio ? Is.EqualTo(new[] { "gr:" + (8001 + index) }) : Is.Null);
                        Assert.That(config.EbookBooksToMonitor, expectedEbook ? Is.EqualTo(new[] { "gr:" + (8001 + index) }) : Is.Null);
                    });
                }
                Assert.That(rootReads, Is.EquivalentTo((sides & 1) != 0 && (sides & 2) != 0
                    ? new[] { audiobookRoot.Path, ebookRoot.Path }
                    : new[] { (sides & 1) != 0 ? audiobookRoot.Path : ebookRoot.Path }));
                Assert.That(metadataReads, Has.Count.EqualTo(existingAudio ? 0 : authorCount));
                Assert.That(metadataReads.Select(read => read.Side), Is.All.EqualTo((sides & 1) != 0 ? BookMediaType.Audiobook : BookMediaType.Ebook));
                Assert.That(commands.OfType<ProcessPendingImportsCommand>().Count(), Is.EqualTo(1));
            });
        }
    }
}
