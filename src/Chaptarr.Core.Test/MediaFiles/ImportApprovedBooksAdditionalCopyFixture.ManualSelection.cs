using System;
using System.Data;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Chaptarr.Core.Test.Common;
using Dapper;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Http;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Notifications.Webhook;
using System.Text;
using Newtonsoft.Json.Linq;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Extras;
using NzbDrone.Core.Extras.Others;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Manual;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.MediaFiles
{
    public partial class ImportApprovedBooksAdditionalCopyFixture
    {
        private static IEnumerable<TestCaseData> ManualSelectionCases()
        {
            foreach (var postgres in new[] { false, true })
            foreach (var media in new[] { BookMediaType.Audiobook, BookMediaType.Ebook })
            foreach (var scenario in new[] { "adopt", "adopt-edition-save-failure", "adopt-persistence-failure", "relink-persistence-failure", "relink", "alternative-copy", "alternative-copy-adopt", "alternative-copy-relink", "move", "combine", "combine-same-edition", "combine-unpinned", "queue-combine", "replace", "transfer-failure", "combine-collision", "database-conflict", "edition-save-failure", "book-save-failure", "relink-edition-save-failure", "extras-combine", "extras-replace", "extras-new", "extras-disabled", "extras-move-combine", "extras-move-replace", "extras-hardlink-combine", "extras-hardlink-replace" })
            {
                yield return new TestCaseData(postgres, media, scenario);
            }
        }

        [TestCaseSource(nameof(ManualSelectionCases))]
        public void manual_selection_must_honor_target_combine_replace_and_successful_pin(bool postgres, BookMediaType mediaType, string scenario)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var root = Path.Combine(Path.GetTempPath(), "manual-selection-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                try
                {
                    using var logFactory = new LogFactory();
                    var logs = new MemoryTarget { Layout = "${level}|${message}|${exception:format=Message}" };
                    var logging = new LoggingConfiguration();
                    logging.AddRule(LogLevel.Warn, LogLevel.Fatal, logs);
                    logFactory.Configuration = logging;
                    var logger = logFactory.GetCurrentClassLogger();
                    var extrasCase = scenario.StartsWith("extras-", StringComparison.Ordinal);
                    var extrasEnabled = extrasCase && scenario != "extras-disabled";
                    var move = scenario == "move" || scenario.StartsWith("extras-move-", StringComparison.Ordinal);
                    var hardlink = scenario.StartsWith("extras-hardlink-", StringComparison.Ordinal);
                    var history = new HistoryService(new HistoryRepository(database, new NoOpEventAggregator()), logger);
                    var published = new List<object>();
                    var requests = new List<JObject>();
                    var http = InterfaceProxy.Proxy<IHttpClient>((method, args) =>
                    {
                        Assert.That(method.Name, Is.EqualTo(nameof(IHttpClient.Execute)));
                        var request = (HttpRequest)args[0];
                        requests.Add(JObject.Parse(Encoding.UTF8.GetString(request.ContentData)));
                        return new HttpResponse(request, new HttpHeader(), "{}");
                    });
                    var webhook = new Webhook(new WebhookProxy(http), InterfaceProxy.Proxy<IConfigFileProvider>((method, _) =>
                        method.Name == "get_InstanceName" ? "Fixture" : throw new NotImplementedException(method.Name)))
                    {
                        Definition = new NotificationDefinition { Id = 17, Settings = new WebhookSettings { Url = "https://fixture.invalid/hook" }, OnReleaseImport = true, OnUpgrade = true }
                    };
                    var notifications = new NotificationService(
                        InterfaceProxy.Proxy<INotificationFactory>((method, _) => method.Name switch
                        {
                            nameof(INotificationFactory.OnReleaseImportEnabled) or nameof(INotificationFactory.OnUpgradeEnabled) or nameof(INotificationFactory.GetAvailableProviders) => new List<INotification> { webhook },
                            _ => throw new NotImplementedException(method.Name)
                        }),
                        InterfaceProxy.Proxy<INotificationStatusService>((method, _) => method.Name switch
                        {
                            nameof(INotificationStatusService.GetBlockedProviders) => new List<NotificationStatus>(),
                            nameof(INotificationStatusService.RecordSuccess) => null,
                            _ => throw new AssertionException("Notification failed: " + method.Name)
                        }), null, logger);
                    var events = InterfaceProxy.Proxy<IEventAggregator>((method, args) =>
                    {
                        if (method.Name == nameof(IEventAggregator.PublishEvent))
                        {
                            published.Add(args[0]);
                            if (args[0] is BookImportedEvent bookImported) notifications.Handle(bookImported);
                            if (args[0] is TrackImportedEvent imported)
                            {
                                history.Handle(imported);
                            }
                        }

                        return null;
                    });
                    var (profile, author, book, oldEdition) = CreateAudiobookConversionGraph(295);
                    var quality = new QualityModel(mediaType == BookMediaType.Audiobook ? Quality.M4B : Quality.EPUB);
                    var extension = mediaType == BookMediaType.Audiobook ? ".m4b" : ".epub";
                    var library = Path.Combine(root, "library");
                    Directory.CreateDirectory(library);
                    author.Id = 0;
                    author.Path = library;
                    author.AudiobookPath = library;
                    author.EbookPath = library;
                    author.CleanName = "testwriter";
                    author.SortName = "testwriter";
                    author.TitleSlug = "test-writer";
                    author.Added = DateTime.UtcNow;
                    new AuthorRepository(database, events, logger).Insert(author);
                    author.EbookQualityProfile = profile;
                    author.EbookQualityProfileId = profile.Id;
                    profile.UpgradeAllowed = false;
                    var persistenceFailure = scenario.EndsWith("persistence-failure", StringComparison.Ordinal);
                    var failing = persistenceFailure || scenario is "transfer-failure" or "combine-collision" or "database-conflict";
                    var selectionFailure = scenario.EndsWith("save-failure", StringComparison.Ordinal);
                    var relink = scenario.Contains("relink", StringComparison.Ordinal);
                    var adopt = scenario.Contains("adopt", StringComparison.Ordinal);
                    var alternative = scenario.StartsWith("alternative-copy", StringComparison.Ordinal);
                    book.Id = 0;
                    book.AuthorId = author.Id;
                    book.CleanTitle = "blacksheep";
                    book.TitleSlug = "target";
                    book.Added = DateTime.UtcNow;
                    book.MediaType = mediaType;
                    book.AnyEditionOk = alternative || adopt || relink || failing || selectionFailure || scenario == "combine-unpinned";
                    var books = new BookRepository(database, events);
                    books.Insert(book);
                    var editions = new EditionRepository(database, events);
                    oldEdition.Id = 0;
                    oldEdition.BookId = book.Id;
                    oldEdition.Monitored = true;
                    oldEdition.ManualAdd = !book.AnyEditionOk;
                    oldEdition.ReadingFormatId = mediaType == BookMediaType.Audiobook ? 2 : 3;
                    editions.Insert(oldEdition);
                    var selected = scenario == "combine-same-edition" ? oldEdition : editions.Insert(new Edition
                    {
                        BookId = book.Id, Book = book, Title = book.Title, ForeignEditionId = "hc:edition:selected",
                        ReadingFormatId = oldEdition.ReadingFormatId, NarratorNames = new List<string> { "Selected Narrator" }
                    });
                    book.Editions = selected == oldEdition ? new List<Edition> { oldEdition } : new List<Edition> { oldEdition, selected };
                    var files = new MediaFileRepository(database, events);
                    var oldPath = Path.Combine(library, extrasCase ? "incoming" + (mediaType == BookMediaType.Audiobook ? ".mp3" : ".pdf") : "existing" + extension);
                    var source = Path.Combine(root, "incoming" + extension);
                    var destination = Path.Combine(library, "incoming" + extension);
                    File.WriteAllText(oldPath, "existing recording");
                    File.WriteAllText(source, "incoming recording");
                    var oldFile = files.Insert(new BookFile
                    {
                        EditionId = oldEdition.Id, Edition = oldEdition, Path = oldPath, Quality = quality,
                        IsGraphicAudio = relink, AudioProductionOverride = relink ? false : null,
                        DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow, MediaInfo = new MediaInfoModel()
                    });
                    if (relink)
                    {
                        source = oldPath;
                        destination = oldPath;
                    }

                    if (adopt)
                    {
                        File.Move(source, destination);
                        source = destination;
                    }

                    if (persistenceFailure)
                    {
                        using var connection = database.OpenConnection();
                        var operation = relink ? "UPDATE" : "INSERT";
                        connection.Execute(postgres
                            ? $"""
                                CREATE FUNCTION reject_file_save() RETURNS trigger LANGUAGE plpgsql AS $$
                                BEGIN RAISE EXCEPTION 'file write refused'; END $$;
                                CREATE TRIGGER reject_file_save BEFORE {operation} ON "BookFiles"
                                FOR EACH ROW EXECUTE FUNCTION reject_file_save();
                                """
                            : $"""
                                CREATE TRIGGER reject_file_save BEFORE {operation} ON "BookFiles"
                                BEGIN SELECT RAISE(ABORT, 'file write refused'); END;
                                """);
                    }

                    if (selectionFailure)
                    {
                        using var connection = database.OpenConnection();
                        var table = scenario == "book-save-failure" ? "Books" : "Editions";
                        var column = scenario == "book-save-failure" ? "AnyEditionOk" : "Monitored";
                        connection.Execute(postgres
                            ? $"""
                                CREATE FUNCTION reject_selection_save() RETURNS trigger LANGUAGE plpgsql AS $$
                                BEGIN RAISE EXCEPTION 'selection write refused'; END $$;
                                CREATE TRIGGER reject_selection_save BEFORE UPDATE OF "{column}" ON "{table}"
                                FOR EACH ROW EXECUTE FUNCTION reject_selection_save();
                                """
                            : $"""
                                CREATE TRIGGER reject_selection_save BEFORE UPDATE OF "{column}" ON "{table}"
                                BEGIN SELECT RAISE(ABORT, 'selection write refused'); END;
                                """);
                    }
                    if (scenario == "combine-collision")
                    {
                        destination = oldPath;
                    }

                    if (scenario == "transfer-failure")
                    {
                        Directory.CreateDirectory(destination);
                    }

                    OtherExtraFileService extraWriter = null;
                    var replacedCount = 0;
                    var media = InterfaceProxy.Proxy<IMediaFileService>((method, args) =>
                    {
                        switch (method.Name)
                        {
                            case nameof(IMediaFileService.GetFilesByBook): return files.GetFilesByBook((int)args[0]);
                            case nameof(IMediaFileService.GetFilesByBooks): return files.GetFilesByBooks((List<int>)args[0]);
                            case nameof(IMediaFileService.GetFileWithPath): return args[0] is List<string> paths ? files.GetFileWithPath(paths) : files.GetFileWithPath((string)args[0]);
                            case nameof(IMediaFileService.Update): files.Update((BookFile)args[0]); return null;
                            case nameof(IMediaFileService.ValidateReplacementFiles):
                                Assert.That(((List<BookFile>)args[0]).All(file => ManualImportReplacementFiles.IsConfirmed(
                                    file, (IReadOnlyCollection<ManualImportReplacementFile>)args[1])), Is.True);
                                return null;
                            case nameof(IMediaFileService.ReplaceMany):
                                if (scenario == "database-conflict")
                                {
                                    var other = books.Insert(new Book { AuthorId = author.Id, Title = "Other", CleanTitle = "other", TitleSlug = "other", Added = DateTime.UtcNow, MediaType = mediaType });
                                    var otherEdition = editions.Insert(new Edition { BookId = other.Id, Title = "Other", ForeignEditionId = "hc:edition:other", Monitored = true });
                                    files.Insert(new BookFile { EditionId = otherEdition.Id, Path = destination, Quality = quality, DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow, MediaInfo = new MediaInfoModel() });
                                }

                                replacedCount += ((List<BookFile>)args[1]).Count;
                                if (args.Length == 5)
                                    ((Action<Action<IDbConnection, IDbTransaction>>)args[4])((connection, transaction) =>
                                        files.ReplaceMany((List<BookFile>)args[0], (List<BookFile>)args[1], connection, transaction));
                                else
                                    files.ReplaceMany((List<BookFile>)args[0], (List<BookFile>)args[1]);
                                if (extrasCase)
                                {
                                    foreach (var replaced in (List<BookFile>)args[1])
                                        extraWriter.Handle(new BookFileDeletedEvent(replaced, (DeleteMediaFileReason)args[2],
                                            ManualImportReplacementFiles.ConfirmedAssociatedPaths(replaced, (IReadOnlyCollection<ManualImportReplacementFile>)args[3]),
                                            (List<BookFile>)args[0]));
                                }
                                return null;
                            default: throw new InvalidOperationException(method.Name);
                        }
                    });
                    var editionService = new EditionService(editions, events, logger);
                    var authorService = InterfaceProxy.Proxy<IAuthorService>((method, args) => method.Name == nameof(IAuthorService.GetAuthors) ? new List<Author> { author } : author);
                    var persistedBooks = new BookService(books, editionService, events, authorService, media,
                        Proxy<IRootFolderService>(), null, null, logger);
                    var bookService = InterfaceProxy.Proxy<IBookService>((method, args) => method.Name switch
                    {
                        nameof(IBookService.GetBook) => persistedBooks.GetBook((int)args[0]),
                        nameof(IBookService.UpdateBook) => args.Length == 1
                            ? persistedBooks.UpdateBook((Book)args[0])
                            : persistedBooks.UpdateBook((Book)args[0], (List<Edition>)args[1], (string)args[2],
                                (Action<IDbConnection, IDbTransaction>)args[3]),
                        _ => throw new AssertionException("Explicit import must not create another book: " + method.Name)
                    });
                    var extraRows = new OtherExtraFileRepository(database, events);
                    var extraDisk = new ExtraImportDisk();
                    var extraConfig = InterfaceProxy.Proxy<IConfigService>((method, _) => method.Name switch
                    {
                        "get_ImportExtraFiles" => extrasEnabled,
                        "get_ExtraFileExtensions" => "jpg,nfo,epub,m4b,mp3",
                        "get_CopyUsingHardlinks" => hardlink,
                        _ => throw new AssertionException(method.Name)
                    });
                    extraWriter = new OtherExtraFileService(extraRows, authorService, extraDisk, new StubRecycleBinProvider(), logger);
                    var extras = new ExtraService(media, editionService, extraDisk, extraConfig,
                        new[] { new OtherExtraService(extraConfig, extraDisk, new DiskTransferService(extraDisk, logger), extraWriter,
                            InterfaceProxy.Proxy<IMediaFileAttributeService>((_, _) => null), null, logger) }, logger);
                    if (extrasCase)
                    {
                        foreach (var suffix in new[] { ".jpg", ".nfo" })
                        {
                            File.WriteAllText(Path.ChangeExtension(source, suffix), "incoming " + suffix);
                            if (scenario != "extras-new")
                            {
                                File.WriteAllText(Path.ChangeExtension(destination, suffix), "existing " + suffix);
                                extraWriter.Upsert(new OtherExtraFile { AuthorId = author.Id, BookId = book.Id, BookFileId = oldFile.Id,
                                    RelativePath = Path.GetFileNameWithoutExtension(destination) + suffix, Extension = suffix });
                            }
                        }

                        File.WriteAllText(Path.ChangeExtension(source, ".png"), "unconfigured extra");
                        File.WriteAllText(Path.ChangeExtension(source, ".epub" == extension ? ".m4b" : ".epub"), "other media");
                    }

                    var mover = new StubMoveBookFiles { DestinationPath = destination, TransferFilesOnDisk = true };
                    var importer = new ImportApprovedBooks(media, new StubMetadataTagService(), new StubMediaInfoExtractor(),
                        Proxy<IAuthorService>(), bookService, editionService, new StubRecycleBinProvider(), extras, mover,
                        history, Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(), events, Proxy<IManageCommandQueue>(),
                        new SeriesBookLinkService(new SeriesBookLinkRepository(database, events)), Proxy<ISeriesService>(), Proxy<NzbDrone.Core.Profiles.Qualities.IQualityProfileService>(), Proxy<IM4bConversionService>(), logger);
                    var tracked = scenario == "queue-combine" || selectionFailure || alternative ? new TrackedDownload
                    {
                        State = TrackedDownloadState.ImportBlocked,
                        DownloadItem = new DownloadClientItem { DownloadId = "manual-selection", DownloadForced = true, FilePaths = new List<string> { source } },
                        RemoteBook = new RemoteBook { Author = author, Books = new List<Book> { book }, ParsedBookInfo = new ParsedBookInfo { Quality = quality },
                            AlternativeCopies = alternative, AlternativeCopyGrabDate = alternative ? DateTime.UtcNow : null,
                            TargetEditionIds = new Dictionary<int, int> { [book.Id] = selected.Id } }
                    } : null;
                    var dependencies = new Dictionary<Type, object>
                    {
                        [typeof(Logger)] = logger,
                        [typeof(IDiskProvider)] = InterfaceProxy.Proxy<IDiskProvider>((method, args) => new System.IO.Abstractions.FileSystem().FileInfo.FromFileName((string)args[0])),
                        [typeof(IRootFolderService)] = InterfaceProxy.Proxy<IRootFolderService>((method, args) => ((string)args[0]).StartsWith(library, StringComparison.Ordinal) ? new RootFolder { Id = 1, Path = library } : null),
                        [typeof(IAuthorService)] = InterfaceProxy.Proxy<IAuthorService>((method, args) => author),
                        [typeof(IBookService)] = bookService,
                        [typeof(IEditionService)] = editionService,
                        [typeof(IMetadataTagService)] = new StubMetadataTagService(),
                        [typeof(ITrackedDownloadService)] = InterfaceProxy.Proxy<ITrackedDownloadService>((method, args) => method.Name == nameof(ITrackedDownloadService.Find) ? tracked : new List<TrackedDownload>()),
                        [typeof(IDownloadImportModeResolver)] = InterfaceProxy.Proxy<IDownloadImportModeResolver>((method, args) => ImportMode.Copy),
                        [typeof(IImportApprovedBooks)] = importer,
                        [typeof(IDownloadClientFileSnapshotService)] = new DownloadClientFileSnapshotService(new DownloadClientFileSnapshotRepository(database, events), null, logger),
                        [typeof(IEventAggregator)] = events,
                        [typeof(IHistoryService)] = history,
                        [typeof(ITrackedDownloadAlreadyImported)] = new TrackedDownloadAlreadyImported(logger, media)
                    };
                    var constructor = typeof(ManualImportService).GetConstructors().Single();
                    var subject = (ManualImportService)constructor.Invoke(constructor.GetParameters().Select(parameter => dependencies.GetValueOrDefault(parameter.ParameterType)).ToArray());
                    var command = new ManualImportCommand
                    {
                        ImportMode = move ? ImportMode.Move : ImportMode.Copy, ReplaceExistingFiles = scenario is "replace" or "database-conflict" || (extrasCase && scenario.EndsWith("replace", StringComparison.Ordinal)),
                        Files = new List<ManualImportFile> { new() { Path = source, AuthorId = author.Id, BookId = book.Id, EditionId = selected.Id, Quality = quality, DisableReleaseSwitching = scenario != "combine-unpinned", DownloadId = tracked?.DownloadItem.DownloadId } }
                    };
                    if (command.ReplaceExistingFiles)
                    {
                        command.ConfirmedReplacementFiles = new List<ManualImportReplacementFile> { new()
                        {
                            Id = oldFile.Id, BookId = book.Id, Path = oldFile.Path,
                            AssociatedPaths = extrasCase ? new[] { ".jpg", ".nfo" }.Select(suffix => Path.ChangeExtension(destination, suffix)).ToList() : new()
                        } };
                    }

                    if (selectionFailure)
                    {
                        var error = Assert.Catch(() => subject.Execute(command));
                        var saved = files.GetFilesByBook(book.Id);
                        if (relink)
                        {
                            Assert.That(saved.Single().AudioProductionOverride, Is.False, "A relink preserves the explicit recording correction");
                            Assert.That(AudioProductionDetector.IsDramatized(saved.Single()), Is.False);
                        }
                        Assert.Multiple(() =>
                        {
                            Assert.That(saved.Single(file => file.Path == destination).EditionId, Is.EqualTo(selected.Id));
                            Assert.That(saved, Has.Count.EqualTo(relink ? 1 : 2));
                            Assert.That(File.ReadAllText(destination), Is.EqualTo(relink ? "existing recording" : "incoming recording"));
                            Assert.That(history.FindByDownloadId(tracked.DownloadItem.DownloadId)
                                .Count(row => row.EventType == EntityHistoryEventType.BookFileImported), Is.EqualTo(1));
                            Assert.That(published.OfType<BookImportedEvent>().Count(), Is.EqualTo(1));
                            Assert.That(published.OfType<DownloadCompletedEvent>().Count(), Is.EqualTo(1));
                            Assert.That(tracked.State, Is.EqualTo(TrackedDownloadState.Imported));
                            Assert.That(error, Is.TypeOf<CommandFailedException>());
                            Assert.That(error.Message, Does.Contain("Imported 1 files"));
                            Assert.That(error.Message, Does.Contain("selection write refused"));
                            Assert.That(books.Get(book.Id).AnyEditionOk, Is.True, "A failed edition selection must not pin the previously selected edition");
                        });
                    }
                    else if (failing)
                    {
                        var error = Assert.Catch(() => subject.Execute(command));
                        Assert.That(error.Message, Does.Contain(scenario == "database-conflict" ? "another book" : persistenceFailure ? "file write refused" : "Import failed"));
                        Assert.That(books.Get(book.Id).AnyEditionOk, Is.True, "Failed imports cannot pin a book");
                        Assert.That(editions.Get(oldEdition.Id).Monitored, Is.True, "Failed persistence cannot change the selected edition");
                        Assert.That(files.Get(oldFile.Id).EditionId, Is.EqualTo(oldEdition.Id));
                    }
                    else
                    {
                        subject.Execute(command);
                        Assert.That(books.All(), Has.Count.EqualTo(1), "Explicit selection cannot create a duplicate book");
                        Assert.That(books.Get(book.Id).AnyEditionOk, Is.EqualTo(alternative || scenario == "combine-unpinned"));
                        Assert.That(editions.Get(selected.Id).Monitored, Is.EqualTo(!alternative));
                        Assert.That(editions.Get(oldEdition.Id).Monitored, Is.EqualTo(alternative || selected == oldEdition));
                        var saved = files.GetFilesByBook(book.Id);
                        Assert.That(saved.Single(file => file.Path == destination).EditionId, Is.EqualTo(selected.Id));
                        Assert.That(replacedCount, Is.EqualTo(command.ReplaceExistingFiles ? 1 : 0));
                        Assert.That(saved.Count, Is.EqualTo(command.ReplaceExistingFiles || relink ? 1 : 2));
                        Assert.That(File.ReadAllText(destination), Is.EqualTo(relink ? "existing recording" : "incoming recording"));
                        if (!command.ReplaceExistingFiles && !relink)
                        {
                            Assert.That(files.Get(oldFile.Id).EditionId, Is.EqualTo(oldEdition.Id), "Combine preserves the retained file identity");
                        }
                    }

                    var expectedNotifications = failing || (adopt || relink) && tracked == null ? 0 : 1;
                    Assert.That(published.OfType<BookImportedEvent>().Count(e => e.NewDownload), Is.EqualTo(expectedNotifications),
                        "Successful new files notify regardless of a tracked download; failed imports do not");

                    Assert.That(requests.Count, Is.EqualTo(expectedNotifications), "Exactly one serialized notification per successfully imported book");
                    if (expectedNotifications > 0)
                    {
                        Assert.That((int)requests.Single()["book"]["id"], Is.EqualTo(book.Id));
                        Assert.That((bool)requests.Single()["isUpgrade"], Is.EqualTo(command.ReplaceExistingFiles));
                    }

                    if (!command.ReplaceExistingFiles || failing)
                    {
                        Assert.That(File.ReadAllText(oldPath), Is.EqualTo("existing recording"));
                    }

                    if (extrasCase)
                    {
                        var transferred = extrasEnabled && (command.ReplaceExistingFiles || scenario == "extras-new");
                        foreach (var suffix in new[] { ".jpg", ".nfo" })
                        {
                            Assert.That(File.ReadAllText(Path.ChangeExtension(destination, suffix)), Is.EqualTo((transferred ? "incoming " : "existing ") + suffix));
                            Assert.That(File.Exists(Path.ChangeExtension(source, suffix)), Is.EqualTo(!move || !transferred));
                            var extra = extraRows.All().Single(row => row.Extension == suffix);
                            Assert.That(extra.BookId, Is.EqualTo(book.Id));
                            if (!transferred)
                            {
                                Assert.That(extra.BookFileId, Is.EqualTo(oldFile.Id));
                            }
                        }

                        Assert.That(Directory.GetFiles(library).Count(path => path.EndsWith(".jpg") || path.EndsWith(".nfo")), Is.EqualTo(2));
                        Assert.That(File.Exists(Path.ChangeExtension(destination, ".png")), Is.False);
                        Assert.That(File.Exists(Path.ChangeExtension(destination, extension == ".epub" ? ".m4b" : ".epub")), Is.False);
                        var collisions = logs.Logs.Where(line => line.Contains("Failed to import extra file:", StringComparison.Ordinal)).ToList();
                        Assert.That(collisions.Count, Is.EqualTo(extrasEnabled && !transferred ? 2 : 0));
                        Assert.That(collisions.All(line => line.Contains("already exists", StringComparison.Ordinal)), Is.True);
                        Assert.That(extraDisk.HardlinkAttempts, Is.EqualTo(hardlink && transferred ? 2 : 0));
                    }

                    if (adopt || relink)
                    {
                        Assert.That(mover.CopyCalls + mover.MoveCalls, Is.Zero, "Existing library files must not be transferred");
                    }

                    if (selectionFailure && !adopt && !relink)
                    {
                        using var connection = database.OpenConnection();
                        var table = scenario == "book-save-failure" ? "Books" : "Editions";
                        connection.Execute(postgres ? $"DROP TRIGGER reject_selection_save ON \"{table}\"" : "DROP TRIGGER reject_selection_save");
                        var copies = mover.CopyCalls;
                        var moves = mover.MoveCalls;
                        var historyCount = history.FindByDownloadId(tracked.DownloadItem.DownloadId).Count;
                        var importedEvents = published.OfType<BookImportedEvent>().Count();
                        subject.Execute(command);
                        Assert.Multiple(() =>
                        {
                            Assert.That(editions.Get(selected.Id).Monitored, Is.True, "Retry must finish the requested edition selection");
                            Assert.That(books.Get(book.Id).AnyEditionOk, Is.False, "Retry must finish the requested pin");
                            Assert.That(mover.CopyCalls, Is.EqualTo(copies));
                            Assert.That(mover.MoveCalls, Is.EqualTo(moves));
                            Assert.That(history.FindByDownloadId(tracked.DownloadItem.DownloadId).Count, Is.EqualTo(historyCount));
                            Assert.That(published.OfType<BookImportedEvent>().Count(), Is.EqualTo(importedEvents));
                        });
                    }

                    if (move) Assert.That(File.Exists(source), Is.False);
                    else Assert.That(File.ReadAllText(source), Is.EqualTo(relink ? "existing recording" : "incoming recording"));
                }
                finally
                {
                    Directory.Delete(root, true);
                }
            });
        }
        private sealed class ExtraImportDisk : DiskProviderBase
        {
            public ExtraImportDisk() : base(new System.IO.Abstractions.FileSystem()) { }
            public int HardlinkAttempts { get; private set; }
            public override IMount GetMount(string path) => null;
            public override long? GetAvailableSpace(string path) => throw new NotImplementedException();
            public override long? GetTotalSize(string path) => throw new NotImplementedException();
            public override void InheritFolderPermissions(string path) => throw new NotImplementedException();
            public override void SetEveryonePermissions(string path) => throw new NotImplementedException();
            public override void SetFilePermissions(string path, string mask, string group) => throw new NotImplementedException();
            public override void SetPermissions(string path, string mask, string group) => throw new NotImplementedException();
            public override void CopyPermissions(string source, string destination) => throw new NotImplementedException();
            public override bool TryCreateHardLink(string source, string destination)
            {
                HardlinkAttempts++;
                return false;
            }
        }
    }
}
