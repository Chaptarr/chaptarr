using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Extras;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Manual;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.MediaFiles
{
    public partial class ImportApprovedBooksAdditionalCopyFixture
    {
        [TestCase(false, false, false)]
        [TestCase(false, false, true)]
        [TestCase(false, true, false)]
        [TestCase(false, true, true)]
        [TestCase(true, false, false)]
        [TestCase(true, false, true)]
        [TestCase(true, true, false)]
        [TestCase(true, true, true)]
        public void manual_failed_previously_excluded_ebook_must_remain_blocked(bool postgres, bool savedFileEvidence, bool differentEdition)
        {
            ExerciseExcludedEbookRetry(postgres, savedFileEvidence, differentEdition, false);
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void automatic_alternative_copy_retry_must_keep_successful_epub_waiver(bool postgres, bool savedFileEvidence)
        {
            ExerciseExcludedEbookRetry(postgres, savedFileEvidence, false, true);
        }

        private void ExerciseExcludedEbookRetry(bool postgres, bool savedFileEvidence, bool differentEdition, bool automaticAlternativeRetry)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var root = Path.Combine(Path.GetTempPath(), "manual-exclusion-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                try
                {
                    var library = Path.Combine(root, "library");
                    var download = Path.Combine(root, "download");
                    Directory.CreateDirectory(library);
                    Directory.CreateDirectory(download);
                    var logger = LogManager.GetCurrentClassLogger();
                    var historyRows = new HistoryRepository(database, new NoOpEventAggregator());
                    var history = new HistoryService(historyRows, logger);
                    var completed = 0;
                    var incomplete = 0;
                    var events = InterfaceProxy.Proxy<IEventAggregator>((method, args) =>
                    {
                        if (args[0] is TrackImportedEvent imported) history.Handle(imported);
                        if (args[0] is DownloadCompletedEvent) completed++;
                        if (args[0] is BookImportIncompleteEvent) incomplete++;
                        return null;
                    });
                    var profile = new QualityProfile
                    {
                        Id = 1, Name = "Ebooks", ProfileType = ProfileType.Ebook,
                        Items = new() { new() { Allowed = true, Quality = Quality.MOBI }, new() { Allowed = true, Quality = Quality.EPUB } }
                    };
                    var author = new Author
                    {
                        Name = "Test Writer", CleanName = "testwriter", SortName = "testwriter", TitleSlug = "test-writer",
                        Path = library, EbookPath = library, EbookRootFolderPath = library,
                        EbookQualityProfileId = profile.Id, EbookQualityProfile = profile, Added = DateTime.UtcNow
                    };
                    new AuthorRepository(database, events, logger).Insert(author);
                    var books = new BookRepository(database, events);
                    var editions = new EditionRepository(database, events);
                    Book AddBook(string title, string key) => books.Insert(new Book
                    {
                        Author = author, AuthorId = author.Id, Title = title, CleanTitle = key, TitleSlug = key,
                        MediaType = BookMediaType.Ebook, Added = DateTime.UtcNow, AnyEditionOk = true
                    });
                    Edition AddEdition(Book book, string key, bool monitored) => editions.Insert(new Edition
                    {
                        Book = book, BookId = book.Id, Title = book.Title, ForeignEditionId = "local:" + key,
                        ReadingFormatId = 3, Monitored = monitored
                    });
                    var first = AddBook("Book One", "one");
                    var second = AddBook("Book Two", "two");
                    var firstEdition = AddEdition(first, "one", true);
                    var selectedEdition = differentEdition ? AddEdition(first, "one-selected", false) : firstEdition;
                    var secondEdition = AddEdition(second, "two", true);
                    first.Editions = new[] { firstEdition, selectedEdition }.Distinct().ToList();
                    second.Editions = new() { secondEdition };
                    var paths = new[] { "Book One.epub", "Book One.mobi", "Book Two.epub" }
                        .Select(name => Path.Combine(download, name)).ToList();
                    foreach (var path in paths) File.WriteAllText(path, Path.GetFileName(path));
                    var files = new MediaFileRepository(database, events);
                    var media = InterfaceProxy.Proxy<IMediaFileService>((method, args) => method.Name switch
                    {
                        nameof(IMediaFileService.GetFilesByBook) => files.GetFilesByBook((int)args[0]),
                        nameof(IMediaFileService.GetFilesByBooks) => savedFileEvidence ? files.GetFilesByBooks((List<int>)args[0]) : new List<BookFile>(),
                        nameof(IMediaFileService.GetFileWithPath) => args[0] is List<string> values ? files.GetFileWithPath(values) : files.GetFileWithPath((string)args[0]),
                        nameof(IMediaFileService.ReplaceMany) => SaveFiles((List<BookFile>)args[0], (List<BookFile>)args[1]),
                        _ => throw new InvalidOperationException(method.Name)
                    });
                    object SaveFiles(List<BookFile> added, List<BookFile> removed)
                    {
                        Assert.That(removed, Is.Empty);
                        files.ReplaceMany(added, removed);
                        return null;
                    }

                    var authorService = InterfaceProxy.Proxy<IAuthorService>((method, args) => author);
                    var editionService = new EditionService(editions, events, logger);
                    var bookService = new BookService(books, editionService, events, authorService, media,
                        Proxy<IRootFolderService>(), null, null, logger);
                    var mover = new StubMoveBookFiles
                    {
                        DestinationForFile = local => Path.Combine(library, Path.GetFileName(local.Path)), TransferFilesOnDisk = true
                    };
                    var importer = new ImportApprovedBooks(media, new StubMetadataTagService(), new StubMediaInfoExtractor(),
                        authorService, bookService, editionService, new StubRecycleBinProvider(), Proxy<IExtraService>(), mover,
                        history, Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(), events, Proxy<IManageCommandQueue>(),
                        new SeriesBookLinkService(new SeriesBookLinkRepository(database, events)), Proxy<ISeriesService>(), Proxy<IQualityProfileService>(), Proxy<IM4bConversionService>(), logger);
                    var tracked = new TrackedDownload
                    {
                        State = TrackedDownloadState.ImportBlocked,
                        DownloadItem = new DownloadClientItem { DownloadId = "ebook-pack", Title = "Two Books", FilePaths = automaticAlternativeRetry ? paths.Take(2).ToList() : paths },
                        RemoteBook = new RemoteBook { Author = author, Books = automaticAlternativeRetry ? new() { first } : new() { first, second }, ParsedBookInfo = new() { Quality = new(Quality.EPUB) },
                            AlternativeCopies = automaticAlternativeRetry,
                            AlternativeCopyGrabDate = automaticAlternativeRetry ? DateTime.UtcNow.AddMinutes(-1) : null,
                            TargetEditionIds = new() { [first.Id] = firstEdition.Id, [second.Id] = secondEdition.Id } }
                    };
                    ImportDecision<LocalBook> Decision(int index, Book book, Edition edition) => new(new LocalBook
                    {
                        Path = paths[index], Book = book, Edition = edition, Author = author, Size = new FileInfo(paths[index]).Length,
                        Modified = DateTime.UtcNow, Quality = new(index == 1 ? Quality.MOBI : Quality.EPUB),
                        AlternativeCopyGrabDate = tracked.RemoteBook.AlternativeCopyGrabDate
                    });
                    Directory.CreateDirectory(Path.Combine(library, "Book Two.epub"));
                    var initialDecisions = new List<ImportDecision<LocalBook>> { Decision(0, first, firstEdition), Decision(1, first, firstEdition) };
                    if (!automaticAlternativeRetry) initialDecisions.Add(Decision(2, second, secondEdition));
                    var initial = importer.Import(initialDecisions,
                        true, tracked.DownloadItem, ImportMode.Copy);
                    Assert.That(initial.Count(result => result.Result == ImportResultType.Imported), Is.EqualTo(1));
                    Assert.That(initial.Count(ImportApprovedBooks.IsSkippedEbookAlternative), Is.EqualTo(1));
                    Assert.That(files.All().Single().MatchProvenance.ImportExcludedSourcePaths, Is.EqualTo(new[] { paths[1] }));
                    Assert.That(historyRows.All().Single().Data.Single(pair => string.Equals(pair.Key, "ExcludedSourcePaths", StringComparison.OrdinalIgnoreCase)).Value,
                        Does.Contain("Book One.mobi"));
                    Directory.Delete(Path.Combine(library, "Book Two.epub"));
                    Directory.CreateDirectory(Path.Combine(library, "Book One.mobi"));

                    if (automaticAlternativeRetry)
                    {
                        var retry = importer.Import(new() { Decision(0, first, firstEdition), Decision(1, first, firstEdition) },
                            true, tracked.DownloadItem, ImportMode.Copy);
                        Assert.That(retry.Single(result => result.ImportDecision.Item.Path == paths[0]).Result, Is.EqualTo(ImportResultType.Skipped));
                        Assert.That(retry.Count(ImportApprovedBooks.IsSkippedEbookAlternative), Is.EqualTo(1));
                        Assert.That(new TrackedDownloadAlreadyImported(logger, media).IsImported(tracked,
                            savedFileEvidence ? new List<EntityHistory>() : history.FindByDownloadId(tracked.DownloadItem.DownloadId), retry), Is.True);
                        Assert.That(files.All(), Has.Count.EqualTo(1));
                        Assert.That(mover.CopyCalls, Is.EqualTo(1), "Retry must not transfer either the already-imported EPUB or its waived MOBI alternative");
                        Assert.That(File.ReadAllText(Path.Combine(library, "Book One.epub")), Is.EqualTo("Book One.epub"));
                        return;
                    }

                    var completion = new TrackedDownloadAlreadyImported(logger, media);
                    var dependencies = new Dictionary<Type, object>
                    {
                        [typeof(Logger)] = logger, [typeof(IEventAggregator)] = events,
                        [typeof(IDiskProvider)] = InterfaceProxy.Proxy<IDiskProvider>((method, args) => new System.IO.Abstractions.FileSystem().FileInfo.FromFileName((string)args[0])),
                        [typeof(IRootFolderService)] = InterfaceProxy.Proxy<IRootFolderService>((method, args) => ((string)args[0]).StartsWith(library, StringComparison.Ordinal) ? new RootFolder { Id = 1, Path = library } : null),
                        [typeof(IAuthorService)] = authorService, [typeof(IBookService)] = bookService, [typeof(IEditionService)] = editionService,
                        [typeof(IMetadataTagService)] = new StubMetadataTagService(),
                        [typeof(ITrackedDownloadService)] = InterfaceProxy.Proxy<ITrackedDownloadService>((method, args) => method.Name == nameof(ITrackedDownloadService.Find) ? tracked : new List<TrackedDownload>()),
                        [typeof(IDownloadImportModeResolver)] = InterfaceProxy.Proxy<IDownloadImportModeResolver>((method, args) => ImportMode.Copy),
                        [typeof(IImportApprovedBooks)] = importer,
                        [typeof(IDownloadClientFileSnapshotService)] = new DownloadClientFileSnapshotService(new DownloadClientFileSnapshotRepository(database, events), null, logger),
                        [typeof(IHistoryService)] = InterfaceProxy.Proxy<IHistoryService>((method, args) => savedFileEvidence ? new List<EntityHistory>() : history.FindByDownloadId((string)args[0])),
                        [typeof(ITrackedDownloadAlreadyImported)] = completion
                    };
                    var constructor = typeof(ManualImportService).GetConstructors().Single();
                    var subject = (ManualImportService)constructor.Invoke(constructor.GetParameters().Select(parameter => dependencies.GetValueOrDefault(parameter.ParameterType)).ToArray());
                    ManualImportCommand Command(params int[] indices) => new()
                    {
                        ImportMode = ImportMode.Copy, ReplaceExistingFiles = false,
                        Files = indices.Select(index => new ManualImportFile
                        {
                            Path = paths[index], AuthorId = author.Id, BookId = index == 2 ? second.Id : first.Id,
                            EditionId = index == 2 ? secondEdition.Id : selectedEdition.Id,
                            Quality = new(index == 1 ? Quality.MOBI : Quality.EPUB), DownloadId = tracked.DownloadItem.DownloadId
                        }).ToList()
                    };
                    var failed = Assert.Throws<CommandFailedException>(() => subject.Execute(Command(1, 2)));
                    Assert.That(files.GetFilesByBook(second.Id).Count, Is.EqualTo(1), failed.Message);
                    Assert.That(failed.Message, Does.Contain(paths[1]));
                    Assert.That(tracked.State, Is.EqualTo(TrackedDownloadState.ImportBlocked));
                    Assert.That(tracked.StatusMessages.Select(message => message.Title), Does.Contain(Path.GetFileName(paths[1])));
                    Assert.That(tracked.StatusMessages.Select(message => message.SourcePath), Does.Contain(paths[1]));
                    Assert.That(completed, Is.Zero);
                    Assert.That(incomplete, Is.EqualTo(1));
                    Assert.That(files.All().Count, Is.EqualTo(2));

                    Directory.Delete(Path.Combine(library, "Book One.mobi"));
                    subject.Execute(Command(1));
                    Assert.That(tracked.State, Is.EqualTo(TrackedDownloadState.Imported));
                    Assert.That(completed, Is.EqualTo(1));
                    Assert.That(tracked.StatusMessages, Is.Empty);
                    Assert.That(files.GetFileWithPath(Path.Combine(library, "Book One.mobi")).EditionId, Is.EqualTo(selectedEdition.Id));
                    Assert.That(File.ReadAllText(Path.Combine(library, "Book One.epub")), Is.EqualTo("Book One.epub"));
                    TestContext.Progress.WriteLine($"EXCLUSION_RETRY database={database.DatabaseType} savedFileEvidence={savedFileEvidence} differentEdition={differentEdition}");
                }
                finally
                {
                    Directory.Delete(root, true);
                }
            });
        }
    }
}
