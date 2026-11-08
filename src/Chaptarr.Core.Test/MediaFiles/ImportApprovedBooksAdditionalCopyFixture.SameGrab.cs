using System;
using System.Data;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Chaptarr.Api.V1.ManualImport;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Extras;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.Extras.Others;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Manual;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.MediaFiles
{
    public partial class ImportApprovedBooksAdditionalCopyFixture
    {
        [TestCase(false, "subset")]
        [TestCase(true, "subset")]
        [TestCase(false, "retry-all")]
        [TestCase(true, "retry-all")]
        [TestCase(false, "copy-failure")]
        [TestCase(true, "copy-failure")]
        [TestCase(false, "changed-edition")]
        [TestCase(true, "changed-edition")]
        [TestCase(false, "changed-edition-collision")]
        [TestCase(true, "changed-edition-collision")]
        [TestCase(false, "replace")]
        [TestCase(true, "replace")]
        [TestCase(false, "replace-all")]
        [TestCase(true, "replace-all")]
        [TestCase(false, "replace-move")]
        [TestCase(true, "replace-move")]
        [TestCase(false, "replace-move-failure")]
        [TestCase(true, "replace-move-failure")]
        [TestCase(false, "replace-missing")]
        [TestCase(true, "replace-missing")]
        [TestCase(false, "replace-failure")]
        [TestCase(true, "replace-failure")]
        [TestCase(false, "replace-stale")]
        [TestCase(true, "replace-stale")]
        [TestCase(false, "replace-unconfirmed")]
        [TestCase(true, "replace-unconfirmed")]
        [TestCase(false, "replace-null-confirmation")]
        [TestCase(true, "replace-null-confirmation")]
        [TestCase(false, "replace-renamed")]
        [TestCase(true, "replace-renamed")]
        [TestCase(false, "replace-old-missing")]
        [TestCase(true, "replace-old-missing")]
        [TestCase(false, "replace-all-old-missing")]
        [TestCase(true, "replace-all-old-missing")]
        [TestCase(false, "replace-all-old-missing-failure")]
        [TestCase(true, "replace-all-old-missing-failure")]
        [TestCase(false, "replace-all-old-missing-concurrent")]
        [TestCase(true, "replace-all-old-missing-concurrent")]
        [TestCase(false, "replace-in-root-failure")]
        [TestCase(true, "replace-in-root-failure")]
        [TestCase(false, "replace-sidecars")]
        [TestCase(true, "replace-sidecars")]
        [TestCase(false, "replace-sidecars-failure")]
        [TestCase(true, "replace-sidecars-failure")]
        [TestCase(false, "replace-sidecars-stale")]
        [TestCase(true, "replace-sidecars-stale")]
        [TestCase(false, "replace-sidecars-late")]
        [TestCase(true, "replace-sidecars-late")]
        [TestCase(false, "replace-colocated")]
        [TestCase(true, "replace-colocated")]
        public void manual_import_must_honor_combine_and_confirmed_replace_for_three_parts(bool postgres, string scenario)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var root = Path.Combine(Path.GetTempPath(), "manual-combine-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                try
                {
                    var logger = LogManager.GetCurrentClassLogger();
                    var history = new HistoryService(new HistoryRepository(database, new NoOpEventAggregator()), logger);
                    Action<BookFileDeletedEvent> onFileDeleted = null;
                    Action<BookFileDeletedEvent> beforeAssociatedDeletion = null;
                    var events = InterfaceProxy.Proxy<IEventAggregator>((method, args) =>
                    {
                        if (args[0] is NzbDrone.Core.MediaFiles.Events.TrackImportedEvent imported)
                        {
                            history.Handle(imported);
                        }
                        else if (args[0] is BookFileDeletedEvent deleted)
                        {
                            onFileDeleted?.Invoke(deleted);
                        }

                        return null;
                    });
                    var (profile, author, book, edition) = CreateAudiobookConversionGraph(296);
                    var quality = new QualityModel(Quality.MP3);
                    var library = Path.Combine(root, "library");
                    var download = Path.Combine(root, "download");
                    Directory.CreateDirectory(library);
                    Directory.CreateDirectory(download);
                    author.Id = 0;
                    author.Path = library;
                    author.AudiobookPath = library;
                    author.CleanName = "testwriter";
                    author.SortName = "testwriter";
                    author.TitleSlug = "test-writer";
                    author.Added = DateTime.UtcNow;
                    new AuthorRepository(database, events, logger).Insert(author);
                    profile.UpgradeAllowed = false;
                    profile.ConvertToQualityId = null;
                    book.Id = 0;
                    book.AuthorId = author.Id;
                    book.CleanTitle = "blacksheep";
                    book.TitleSlug = "target";
                    book.Added = DateTime.UtcNow;
                    book.AnyEditionOk = false;
                    var books = new BookRepository(database, events);
                    books.Insert(book);
                    var editions = new EditionRepository(database, events);
                    edition.Id = 0;
                    edition.BookId = book.Id;
                    edition.Monitored = true;
                    edition.ManualAdd = true;
                    editions.Insert(edition);
                    var selected = editions.Insert(new Edition
                    {
                        BookId = book.Id, Book = book, Title = book.Title, ForeignEditionId = "hc:edition:selected",
                        ReadingFormatId = 2, NarratorNames = new List<string> { "Selected Narrator" }
                    });
                    book.Editions = new List<Edition> { edition, selected };
                    var paths = Enumerable.Range(1, 3).Select(part => Path.Combine(download, $"Part {part:00}.mp3")).ToList();
                    foreach (var path in paths)
                    {
                        File.WriteAllText(path, "recording: " + Path.GetFileName(path));
                    }

                    var files = new MediaFileRepository(database, events);
                    var disk = InterfaceProxy.Proxy<IDiskProvider>((method, args) => method.Name switch
                    {
                        nameof(IDiskProvider.FileExists) or nameof(IDiskProvider.FileExistsCanonical) => File.Exists((string)args[0]),
                        nameof(IDiskProvider.GetFiles) => Directory.GetFiles((string)args[0]).ToList(),
                        nameof(IDiskProvider.GetParentFolder) => Path.GetDirectoryName((string)args[0]),
                        nameof(IDiskProvider.OpenReadStream) => File.OpenRead((string)args[0]),
                        _ => throw new InvalidOperationException(method.Name)
                    });
                    var metadataFiles = new MetadataFileRepository(database, events);
                    var otherFiles = new OtherExtraFileRepository(database, events);
                    var replacementPreview = new ManualImportReplacementFiles(disk, metadataFiles, otherFiles, new AuthorRepository(database, events, logger), books,
                        InterfaceProxy.Proxy<NzbDrone.Core.Configuration.IConfigService>((_, _) => string.Empty));
                    var realMedia = new MediaFileService(files, events, null, disk, logger, replacementFiles: replacementPreview);
                    var recycle = InterfaceProxy.Proxy<IRecycleBinProvider>((method, args) => { File.Delete((string)args[0]); return null; });
                    var metadataDeletion = new MetadataFileService(metadataFiles, null, disk, recycle, logger);
                    var otherDeletion = new OtherExtraFileService(otherFiles, null, disk, recycle, logger);
                    var replicaDeletion = new MediaFileDeletionService(disk, recycle, null, null, null, events, null, null, logger, metadataFiles: Chaptarr.Core.Test.Common.EmptyDeletionExtras.Repository<NzbDrone.Core.Extras.Metadata.Files.MetadataFile>(), otherFiles: Chaptarr.Core.Test.Common.EmptyDeletionExtras.Repository<NzbDrone.Core.Extras.Others.OtherExtraFile>(), metadataFileService: Chaptarr.Core.Test.Common.EmptyDeletionExtras.Metadata(), otherFileService: Chaptarr.Core.Test.Common.EmptyDeletionExtras.Other());
                    onFileDeleted = deleted =>
                    {
                        beforeAssociatedDeletion?.Invoke(deleted);
                        metadataDeletion.Handle(deleted);
                        otherDeletion.Handle(deleted);
                        replicaDeletion.Handle(deleted);
                    };
                    var replaceRequested = false;
                    var replacementWrites = 0;
                    var extrasBeforeCommit = false;
                    var extraImports = new List<string>();
                    var extras = InterfaceProxy.Proxy<IExtraService>((method, args) =>
                    {
                        var imported = (BookFile)args[1];
                        extrasBeforeCommit |= !files.All().Any(file => file.Id == imported.Id && file.EditionId == imported.EditionId);
                        extraImports.Add(imported.Path);
                        return null;
                    });
                    var importMode = scenario.StartsWith("replace-move", StringComparison.Ordinal) ? ImportMode.Move : ImportMode.Copy;
                    var media = InterfaceProxy.Proxy<IMediaFileService>((method, args) =>
                    {
                        switch (method.Name)
                        {
                            case nameof(IMediaFileService.GetFilesByBook): return files.GetFilesByBook((int)args[0]);
                            case nameof(IMediaFileService.GetFilesByBooks): return files.GetFilesByBooks((List<int>)args[0]);
                            case nameof(IMediaFileService.GetFileWithPath): return args[0] is List<string> paths ? files.GetFileWithPath(paths) : files.GetFileWithPath((string)args[0]);
                            case nameof(IMediaFileService.Update): files.Update((BookFile)args[0]); return null;
                            case nameof(IMediaFileService.ValidateReplacementFiles):
                                realMedia.ValidateReplacementFiles((List<BookFile>)args[0], (IReadOnlyCollection<ManualImportReplacementFile>)args[1]);
                                return null;
                            case nameof(IMediaFileService.ReplaceMany):
                                if (replaceRequested && ++replacementWrites == 1 && scenario.EndsWith("concurrent", StringComparison.Ordinal))
                                {
                                    var incoming = ((List<BookFile>)args[0]).First(file => file.Id == 0);
                                    files.Insert(new BookFile { Path = incoming.Path, EditionId = edition.Id, Edition = edition,
                                        Quality = quality, MediaInfo = new MediaInfoModel(), DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow });
                                }

                                if (!replaceRequested)
                                {
                                    Assert.That((List<BookFile>)args[1], Is.Empty, "Combine must retain the successful part");
                                }
                                if (args.Length == 5)
                                    realMedia.ReplaceMany((List<BookFile>)args[0], (List<BookFile>)args[1], (DeleteMediaFileReason)args[2],
                                        (IReadOnlyCollection<ManualImportReplacementFile>)args[3], (Action<Action<IDbConnection, IDbTransaction>>)args[4]);
                                else
                                    realMedia.ReplaceMany((List<BookFile>)args[0], (List<BookFile>)args[1], (DeleteMediaFileReason)args[2],
                                        (IReadOnlyCollection<ManualImportReplacementFile>)args[3]);
                                return null;
                            default: throw new InvalidOperationException(method.Name);
                        }
                    });
                    var editionService = new EditionService(editions, events, logger);
                    var authorService = InterfaceProxy.Proxy<IAuthorService>((method, args) => author);
                    var bookService = new BookService(books, editionService, events, authorService, media,
                        Proxy<IRootFolderService>(), null, null, logger);
                    var mover = new StubMoveBookFiles
                    {
                        DestinationForFile = local => Path.Combine(library,
                            scenario == "changed-edition" ? local.Edition.Id.ToString() : "parts", Path.GetFileName(local.Path)),
                        TransferFilesOnDisk = true
                    };
                    var tracked = new TrackedDownload
                    {
                        State = TrackedDownloadState.ImportBlocked,
                        DownloadItem = new DownloadClientItem { DownloadId = "three-parts", FilePaths = paths },
                        RemoteBook = new RemoteBook { Author = author, Books = new List<Book> { book }, ParsedBookInfo = new ParsedBookInfo { Quality = quality } }
                    };
                    ManualImportService CreateService()
                    {
                        var importer = new ImportApprovedBooks(media, new StubMetadataTagService(), new StubMediaInfoExtractor(),
                            authorService, bookService, editionService, new StubRecycleBinProvider(), extras, mover,
                            history, Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(), events, Proxy<IManageCommandQueue>(),
                            new SeriesBookLinkService(new SeriesBookLinkRepository(database, events)), Proxy<ISeriesService>(), Proxy<NzbDrone.Core.Profiles.Qualities.IQualityProfileService>(),
                            Proxy<IM4bConversionService>(), logger);
                        var dependencies = new Dictionary<Type, object>
                        {
                            [typeof(Logger)] = logger,
                            [typeof(IDiskProvider)] = InterfaceProxy.Proxy<IDiskProvider>((method, args) => new System.IO.Abstractions.FileSystem().FileInfo.FromFileName((string)args[0])),
                            [typeof(IRootFolderService)] = InterfaceProxy.Proxy<IRootFolderService>((method, args) => ((string)args[0]).StartsWith(library, StringComparison.Ordinal) ? new RootFolder { Id = 1, Path = library } : null),
                            [typeof(IAuthorService)] = authorService,
                            [typeof(IBookService)] = bookService,
                            [typeof(IEditionService)] = editionService,
                            [typeof(IMetadataTagService)] = new StubMetadataTagService(),
                            [typeof(ITrackedDownloadService)] = InterfaceProxy.Proxy<ITrackedDownloadService>((method, args) => method.Name == nameof(ITrackedDownloadService.Find) ? tracked : new List<TrackedDownload>()),
                            [typeof(IDownloadImportModeResolver)] = InterfaceProxy.Proxy<IDownloadImportModeResolver>((method, args) => importMode),
                            [typeof(IImportApprovedBooks)] = importer,
                            [typeof(IDownloadClientFileSnapshotService)] = new DownloadClientFileSnapshotService(new DownloadClientFileSnapshotRepository(database, events), null, logger),
                            [typeof(IEventAggregator)] = events,
                            [typeof(IHistoryService)] = history,
                            [typeof(ITrackedDownloadAlreadyImported)] = new TrackedDownloadAlreadyImported(logger, media)
                        };
                        var constructor = typeof(ManualImportService).GetConstructors().Single();
                        return (ManualImportService)constructor.Invoke(constructor.GetParameters().Select(parameter => dependencies.GetValueOrDefault(parameter.ParameterType)).ToArray());
                    }

                    ManualImportCommand Command(IEnumerable<string> sources, int editionId) => new()
                    {
                        ImportMode = importMode,
                        ReplaceExistingFiles = false,
                        Files = sources.Select(path => new ManualImportFile
                        {
                            Path = path, AuthorId = author.Id, BookId = book.Id, EditionId = editionId,
                            Quality = quality, DisableReleaseSwitching = true, DownloadId = tracked.DownloadItem.DownloadId
                        }).ToList(),
                        PreviewFiles = paths.Select(path => new ManualImportFile
                        {
                            Path = path, BookId = book.Id, DownloadId = tracked.DownloadItem.DownloadId
                        }).ToList()
                    };

                    if (scenario == "copy-failure")
                    {
                        foreach (var path in paths.Skip(1))
                        {
                            Directory.CreateDirectory(Path.Combine(library, "parts", Path.GetFileName(path)));
                        }

                        Assert.Throws<CommandFailedException>(() => CreateService().Execute(Command(paths, edition.Id)));
                        foreach (var path in paths.Skip(1))
                        {
                            Directory.Delete(Path.Combine(library, "parts", Path.GetFileName(path)));
                        }
                    }
                    else
                    {
                        CreateService().Execute(Command(paths.Take(1), edition.Id));
                    }

                    var first = files.GetFilesByBook(book.Id).Single();
                    Assert.That(history.FindByDownloadId(tracked.DownloadItem.DownloadId)
                        .Count(row => row.EventType == EntityHistoryEventType.BookFileImported), Is.EqualTo(1));
                    Assert.That(tracked.State, Is.EqualTo(TrackedDownloadState.ImportBlocked));
                    Assert.That(first.EditionId, Is.EqualTo(edition.Id));
                    Assert.That(File.ReadAllText(first.Path), Is.EqualTo("recording: Part 01.mp3"));
                    Assert.That(extrasBeforeCommit, Is.False);
                    extraImports.Clear();
                    var associatedScenario = scenario.Contains("sidecars", StringComparison.Ordinal) || scenario == "replace-colocated";
                    string coverPath = null;
                    if (associatedScenario)
                    {
                        coverPath = Path.Combine(Path.GetDirectoryName(first.Path), "cover.jpg");
                        File.WriteAllText(coverPath, "old cover");
                        metadataFiles.Insert(new MetadataFile
                        {
                            AuthorId = author.Id, BookId = book.Id, BookFileId = first.Id,
                            RelativePath = Path.GetRelativePath(library, coverPath), Extension = ".jpg", Consumer = "test",
                            Type = MetadataType.BookImage, Hash = "old cover".SHA256Hash(),
                            Added = DateTime.UtcNow, LastUpdated = DateTime.UtcNow
                        });
                        var storedExtra = metadataFiles.GetFilesByBookFiles(new[] { first.Id }).Single();
                        Assert.That(metadataFiles.GetFilesByBookFiles(Enumerable.Range(first.Id, 901)).Select(file => file.Id),
                            Is.EqualTo(new[] { storedExtra.Id }), "Batching beyond SQLite's parameter limit must retain the same owned files");
                        var storedAuthor = new AuthorRepository(database, events, logger).Get(author.Id);
                        Assert.That(ExtraFilePathHelper.ResolveOwnedSidecarPath(storedAuthor, storedExtra, new[] { first }, disk),
                            Is.EqualTo(coverPath), $"Persisted paths: {storedAuthor.Path}, {storedAuthor.AudiobookPath}, {storedAuthor.EbookPath}; relative: {storedExtra.RelativePath}");
                        if (scenario == "replace-colocated")
                        {
                            File.WriteAllText(Path.Combine(Path.GetDirectoryName(first.Path), "book.epub"), "independent ebook");
                        }
                        else
                        {
                            mover.DestinationForFile = local => Path.Combine(library, "replacement", Path.GetFileName(local.Path));
                        }
                    }
                    if (scenario.StartsWith("replace", StringComparison.Ordinal))
                    {
                        var replaceAll = scenario.StartsWith("replace-all", StringComparison.Ordinal);
                        var oldMissing = scenario.Contains("old-missing", StringComparison.Ordinal);
                        var replacementPaths = (replaceAll ? paths : paths.Skip(1)).ToList();
                        string inRootPath = null;
                        if (scenario == "replace-in-root-failure")
                        {
                            inRootPath = Path.Combine(library, "Untracked Part 04.mp3");
                            File.WriteAllText(inRootPath, new string('x', 4096));
                            replacementPaths.Add(inRootPath);
                        }
                        var replacement = Command(replacementPaths, selected.Id);
                        replacement.ReplaceExistingFiles = true;
                        var rootReads = 0;
                        var previewRoots = new List<RootFolder> { new() { Id = 1, Path = library } };
                        var preview = new ManualImportController(null, null, null, null, media,
                            InterfaceProxy.Proxy<IRootFolderService>((method, args) =>
                            {
                                if (method.Name == nameof(IRootFolderService.All))
                                {
                                    rootReads++;
                                    return previewRoots;
                                }
                                Assert.That(args, Has.Length.EqualTo(2));
                                Assert.That(args[1], Is.SameAs(previewRoots));
                                return ((string)args[0]).StartsWith(library, StringComparison.Ordinal) ? previewRoots[0] : null;
                            }), logger, replacementPreview);
                        replacement.ConfirmedReplacementFiles = preview.GetReplacementFiles(new ManualImportReplacementRequest { Files = replacement.Files.Select(file => new ManualImportReplacementSelection { Path = file.Path, BookId = file.BookId }).ToList() });
                        Assert.That(rootReads, Is.EqualTo(1));
                        Assert.That(replacement.ConfirmedReplacementFiles.Select(file => file.Id), Is.EqualTo(new[] { first.Id }));
                        if (associatedScenario)
                        {
                            Assert.That(replacement.ConfirmedReplacementFiles.Single().AssociatedPaths,
                                Is.EquivalentTo(new[] { coverPath }), "An associated audiobook cover is listed even beside an independent ebook");
                        }
                        if (scenario == "replace-sidecars-stale")
                        {
                            var unconfirmed = Path.Combine(Path.GetDirectoryName(first.Path), "late.nfo");
                            File.WriteAllText(unconfirmed, "new unconfirmed sidecar");
                            otherFiles.Insert(new OtherExtraFile
                            {
                                AuthorId = author.Id, BookId = book.Id, BookFileId = first.Id,
                                RelativePath = Path.GetRelativePath(library, unconfirmed), Extension = ".nfo",
                                Added = DateTime.UtcNow, LastUpdated = DateTime.UtcNow
                            });
                        }
                        if (scenario == "replace-sidecars-late")
                        {
                            beforeAssociatedDeletion = deleted =>
                            {
                                var late = Path.Combine(Path.GetDirectoryName(first.Path), "late.nfo");
                                File.WriteAllText(late, "new unconfirmed sidecar");
                                otherFiles.Insert(new OtherExtraFile
                                {
                                    AuthorId = author.Id, BookId = book.Id, BookFileId = deleted.BookFile.Id,
                                    RelativePath = Path.GetRelativePath(library, late), Extension = ".nfo",
                                    Added = DateTime.UtcNow, LastUpdated = DateTime.UtcNow
                                });
                            };
                        }
                        if (scenario == "replace-null-confirmation")
                        {
                            replacement.ConfirmedReplacementFiles = null;
                        }
                        replaceRequested = true;
                        if (scenario == "replace-unconfirmed")
                        {
                            replacement.ConfirmedReplacementFiles.Clear();
                        }
                        else if (scenario == "replace-renamed")
                        {
                            var renamed = first.Path + ".renamed.mp3";
                            File.Move(first.Path, renamed);
                            first.Path = renamed;
                            files.Update(first);
                        }
                        else if (oldMissing)
                        {
                            File.Delete(first.Path);
                        }

                        if (scenario == "replace-missing")
                        {
                            File.Delete(paths[2]);
                        }
                        else if (scenario.EndsWith("failure", StringComparison.Ordinal))
                        {
                            Directory.CreateDirectory(mover.DestinationForFile(new LocalBook { Path = paths[2], Edition = selected }));
                        }
                        else if (scenario == "replace-stale")
                        {
                            var extraPath = Path.Combine(library, "Another Recording.mp3");
                            File.WriteAllText(extraPath, "unconfirmed recording");
                            files.Insert(new BookFile { Path = extraPath, EditionId = edition.Id, Edition = edition, Quality = quality,
                                MediaInfo = new MediaInfoModel(), DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow });
                        }

                        if (scenario.EndsWith("failure", StringComparison.Ordinal) || scenario.EndsWith("concurrent", StringComparison.Ordinal) || scenario is "replace-null-confirmation" or "replace-missing" or "replace-stale" or "replace-sidecars-stale" or "replace-unconfirmed" or "replace-renamed")
                        {
                            if (scenario.EndsWith("concurrent", StringComparison.Ordinal))
                            {
                                var error = Assert.Throws<CommandFailedException>(() => CreateService().Execute(replacement));
                                Assert.That(error.Message, Does.Contain("Existing files changed"));
                            }
                            else
                            {
                                Assert.Throws<CommandFailedException>(() => CreateService().Execute(replacement));
                            }
                            Assert.That(files.Get(first.Id).EditionId, Is.EqualTo(edition.Id));
                            if (oldMissing)
                            {
                                Assert.That(File.Exists(first.Path), Is.False);
                            }
                            else
                            {
                                Assert.That(File.ReadAllText(first.Path), Is.EqualTo("recording: Part 01.mp3"));
                            }
                            Assert.That(files.GetFilesByBook(book.Id).Any(file => file.EditionId == selected.Id), Is.False);
                            Assert.That(history.FindByDownloadId(tracked.DownloadItem.DownloadId)
                                .Count(row => row.EventType == EntityHistoryEventType.BookFileImported), Is.EqualTo(1));
                            Assert.That(editions.Get(edition.Id).Monitored, Is.True);
                            Assert.That(extraImports, Is.Empty);
                            if (scenario == "replace-move-failure")
                            {
                                Assert.That(File.ReadAllText(paths[1]), Is.EqualTo("recording: Part 02.mp3"));
                                Assert.That(File.ReadAllText(paths[2]), Is.EqualTo("recording: Part 03.mp3"));
                            }
                        }
                        else
                        {
                            CreateService().Execute(replacement);
                            var savedReplacement = files.GetFilesByBook(book.Id);
                            Assert.That(savedReplacement.Any(file => file.Id == first.Id), Is.EqualTo(replaceAll && oldMissing));
                            Assert.That(savedReplacement, Has.Count.EqualTo(replaceAll ? 3 : 2));
                            Assert.That(savedReplacement.All(file => file.EditionId == selected.Id), Is.True);
                            Assert.That(editions.Get(selected.Id).Monitored, Is.True);
                            Assert.That(replacementWrites, Is.EqualTo(scenario.EndsWith("concurrent", StringComparison.Ordinal) ? 2 : 1));
                            Assert.That(extraImports, Has.Count.EqualTo(replaceAll ? 3 : 2));
                        }

                        if (inRootPath != null)
                        {
                            Assert.That(File.ReadAllText(inRootPath), Is.EqualTo(new string('x', 4096)));
                        }
                        Assert.That(Directory.EnumerateFiles(library, "*.chaptarr-upgrade~*", SearchOption.AllDirectories), Is.Empty);
                        Assert.That(books.All(), Has.Count.EqualTo(1));
                        Assert.That(extrasBeforeCommit, Is.False);
                        if (associatedScenario)
                        {
                            var keptCover = scenario is not "replace-sidecars" and not "replace-sidecars-late" and not "replace-colocated";
                            Assert.That(File.Exists(coverPath), Is.EqualTo(keptCover));
                            if (keptCover)
                            {
                                Assert.That(File.ReadAllText(coverPath), Is.EqualTo("old cover"));
                            }
                            if (scenario == "replace-colocated")
                            {
                                Assert.That(File.ReadAllText(Path.Combine(Path.GetDirectoryName(first.Path), "book.epub")), Is.EqualTo("independent ebook"));
                                Assert.That(metadataFiles.GetFilesByBookFiles(new[] { first.Id }), Is.Empty,
                                    "The confirmed audiobook-owned cover and its row are removed together");
                            }
                            if (scenario is "replace-sidecars-stale" or "replace-sidecars-late")
                            {
                                Assert.That(File.ReadAllText(Path.Combine(Path.GetDirectoryName(first.Path), "late.nfo")), Is.EqualTo("new unconfirmed sidecar"));
                            }
                        }
                        return;
                    }

                    var changedEdition = scenario.StartsWith("changed-edition", StringComparison.Ordinal);
                    var retry = Command(scenario == "subset" ? paths.Skip(1) : paths, changedEdition ? selected.Id : edition.Id);
                    if (scenario == "changed-edition-collision")
                    {
                        Assert.Throws<CommandFailedException>(() => CreateService().Execute(retry), "A failed correction must not be hidden by the old source-path coverage");
                        Assert.That(tracked.State, Is.EqualTo(TrackedDownloadState.ImportBlocked));
                        Assert.That(tracked.StatusMessages.Any(message => message.Title == Path.GetFileName(paths[0])), Is.True, "Name the part whose selected edition could not be imported");
                        Assert.That(tracked.StatusMessages.Any(message => message.SourcePath == paths[0]), Is.True, "Keep the missing source identity");
                        Assert.That(files.GetFilesByBook(book.Id).Single(file => file.Path == first.Path).EditionId, Is.EqualTo(edition.Id));
                    }
                    else
                    {
                        CreateService().Execute(retry);
                        Assert.That(tracked.State, Is.EqualTo(TrackedDownloadState.Imported));
                        var saved = files.GetFilesByBook(book.Id);
                        Assert.That(saved.Count(file => file.EditionId == (changedEdition ? selected.Id : edition.Id)), Is.EqualTo(3));
                        Assert.That(saved, Has.Count.EqualTo(changedEdition ? 4 : 3), "Combine preserves an existing file when another edition is explicitly selected");
                    }

                    Assert.That(files.Get(first.Id).Path, Is.EqualTo(first.Path));
                    Assert.That(files.Get(first.Id).EditionId, Is.EqualTo(edition.Id));
                    Assert.That(File.ReadAllText(first.Path), Is.EqualTo(File.ReadAllText(paths[0])));
                    Assert.That(books.All(), Has.Count.EqualTo(1));
                    if (!changedEdition)
                    {
                        Assert.That(mover.CopyCalls, Is.EqualTo(scenario == "copy-failure" ? 5 : 3), "Successful parts must not be copied again");
                        Assert.That(files.GetFilesByBook(book.Id).Select(file => file.Part).OrderBy(part => part), Is.EqualTo(new[] { 1, 2, 3 }));
                    }
                }
                finally
                {
                    Directory.Delete(root, true);
                }
            });
        }
    }
}
