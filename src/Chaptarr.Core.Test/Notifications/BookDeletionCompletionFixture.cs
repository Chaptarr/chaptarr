using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Notifications.Kavita;
using NzbDrone.Core.Notifications.Synology;
using NzbDrone.Core.Notifications.Plex.Server;
using NzbDrone.Core.Notifications.Plex.PlexTv;
using NzbDrone.Core.Notifications.Subsonic;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Notifications
{
    [TestFixture]
    public class BookDeletionCompletionFixture
    {
        [Test]
        public void direct_book_delete_flushes_existing_queues_after_physical_completion(
            [Values(false, true)] bool postgres,
            [Values("delete", "keep", "empty", "missing", "fail-first", "fail-second", "scan-failure")] string scenario)
        {
            var scratch = Path.Combine(Path.GetTempPath(), "chaptarr-delete-notification-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            try
            {
                DualDatabaseFixture.Run(postgres, database =>
                {
                    var root = new RootFolder { Path = Path.Combine(scratch, "library") };
                    var authorPath = Path.Combine(root.Path, "Author");
                    var folder = Path.Combine(authorPath, "Book");
                    Directory.CreateDirectory(folder);
                    var logger = LogManager.GetCurrentClassLogger();
                    Action<IEvent> dispatch = _ => { };
                    var events = Proxy<IEventAggregator>((_, args) => { dispatch((IEvent)args[0]); return null; });
                    var author = new AuthorRepository(database, events, logger).Insert(new Author
                    {
                        Name = "Author", CleanName = "author", TitleSlug = "author", Path = authorPath,
                        AudiobookPath = authorPath, Added = DateTime.UtcNow
                    });
                    var books = new BookRepository(database, events);
                    var editions = new EditionRepository(database, events);
                    var book = books.Insert(new Book { AuthorId = author.Id, Title = "Book", CleanTitle = "book",
                        TitleSlug = "book", MediaType = BookMediaType.Audiobook, Added = DateTime.UtcNow });
                    var edition = editions.Insert(new Edition { BookId = book.Id, Title = "Book", ForeignEditionId = "hc:edition:701" });
                    var fileRows = new MediaFileRepository(database, events);
                    var paths = Enumerable.Range(1, scenario == "empty" ? 0 : 3).Select(part =>
                    {
                        var path = Path.Combine(folder, $"Book - Part {part:00}.mp3");
                        File.WriteAllText(path, "Disposable file for deletion lifecycle test");
                        fileRows.Insert(new BookFile { EditionId = edition.Id, Path = path, Quality = new QualityModel(),
                            DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow, MediaInfo = new MediaInfoModel() });
                        if (scenario == "missing") File.Delete(path);
                        return path;
                    }).ToArray();
                    var editionService = new EditionService(editions, events, logger);
                    var media = Proxy<IMediaFileService>((method, args) => method.Name switch
                    {
                        nameof(IMediaFileService.GetFilesByBooks) => fileRows.GetFilesByBooks((List<int>)args[0]),
                        nameof(IMediaFileService.GetFilesByBook) => fileRows.GetFilesByBook((int)args[0]),
                        _ => throw new NotImplementedException(method.Name)
                    });
                    var disk = Proxy<IDiskProvider>((method, args) => method.Name switch
                    {
                        nameof(IDiskProvider.FileExistsCanonical) or nameof(IDiskProvider.FileExists) => File.Exists((string)args[0]),
                        nameof(IDiskProvider.FolderExists) => Directory.Exists((string)args[0]),
                        nameof(IDiskProvider.GetFiles) => Directory.GetFiles((string)args[0]),
                        nameof(IDiskProvider.FolderEmpty) => !Directory.EnumerateFileSystemEntries((string)args[0]).Any(),
                        _ => throw new NotImplementedException(method.Name)
                    });
                    var recycled = new List<string>();
                    var recycle = Proxy<IRecycleBinProvider>((method, args) =>
                    {
                        Assert.That(method.Name, Is.EqualTo(nameof(IRecycleBinProvider.DeleteFile)));
                        if ((scenario == "fail-first" && (string)args[0] == paths[0]) ||
                            (scenario == "fail-second" && (string)args[0] == paths[1]))
                        {
                            throw new IOException("Constructed disk deletion failure");
                        }

                        File.Delete((string)args[0]);
                        recycled.Add((string)args[0]);
                        return null;
                    });
                    var roots = Proxy<IRootFolderService>((method, args) => method.Name switch
                    {
                        nameof(IRootFolderService.All) => new List<RootFolder> { root },
                        nameof(IRootFolderService.GetBestRootFolder) => ((string)args[0]).StartsWith(root.Path + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? root : null,
                        _ => throw new NotImplementedException(method.Name)
                    });
                    var config = Proxy<IConfigService>((method, _) => method.Name == "get_DeleteEmptyFolders"
                        ? false : throw new NotImplementedException(method.Name));
                    var deletion = new MediaFileDeletionService(disk, recycle, media, null, config, events, roots, null, logger, metadataFiles: Chaptarr.Core.Test.Common.EmptyDeletionExtras.Repository<NzbDrone.Core.Extras.Metadata.Files.MetadataFile>(), otherFiles: Chaptarr.Core.Test.Common.EmptyDeletionExtras.Repository<NzbDrone.Core.Extras.Others.OtherExtraFile>(), metadataFileService: Chaptarr.Core.Test.Common.EmptyDeletionExtras.Metadata(), otherFileService: Chaptarr.Core.Test.Common.EmptyDeletionExtras.Other());
                    var subsonicUpdates = 0;
                    var plexUpdates = 0;
                    var failScan = scenario == "scan-failure";
                    var subsonic = new Subsonic(Proxy<ISubsonicService>((method, _) =>
                    {
                        Assert.That(method.Name, Is.EqualTo(nameof(ISubsonicService.Update)));
                        Assert.That(paths.Any(File.Exists), Is.False, "Refresh must follow physical deletion");
                        subsonicUpdates++;
                        if (failScan) throw new IOException("Synthetic remote scan failure");
                        return null;
                    }), new CacheManager(), new Chaptarr.Core.Test.Common.MemoryPendingNotifications())
                    { Definition = new NotificationDefinition { Id = 1, Settings = new SubsonicSettings { UpdateLibrary = true } } };
                    var plex = new PlexServer(Proxy<IPlexServerService>((method, _) =>
                    {
                        Assert.That(method.Name, Is.EqualTo(nameof(IPlexServerService.UpdateLibrary)));
                        Assert.That(paths.Any(File.Exists), Is.False, "Refresh must follow physical deletion");
                        plexUpdates++;
                        return null;
                    }), Proxy<IPlexTvService>((method, _) => method.Name == nameof(IPlexTvService.Ping)
                        ? null : throw new NotImplementedException(method.Name)), null, new CacheManager(), logger)
                    { Definition = new NotificationDefinition { Id = 2, Settings = new PlexServerSettings { Host = "fixture.invalid", AuthToken = "fixture" } } };
                    var kavitaUpdates = 0;
                    var synologyDeletes = 0;
                    var kavita = new Kavita(Proxy<IKavitaService>((method, args) =>
                    {
                        Assert.That(method.Name, Is.EqualTo(nameof(IKavitaService.Notify)));
                        Assert.That((string)args[1], Is.EqualTo(folder));
                        Assert.That(paths.Any(File.Exists), Is.False);
                        kavitaUpdates++;
                        return null;
                    }), new CacheManager(), new Chaptarr.Core.Test.Common.MemoryPendingNotifications())
                    { Definition = new NotificationDefinition { Id = 3, Settings = new KavitaSettings { Notify = true } } };
                    var synology = new SynologyIndexer(Proxy<ISynologyIndexerProxy>((method, args) =>
                    {
                        Assert.That(method.Name, Is.EqualTo(nameof(ISynologyIndexerProxy.DeleteFile)));
                        Assert.That(paths, Does.Contain((string)args[0]));
                        Assert.That(File.Exists((string)args[0]), Is.False);
                        synologyDeletes++;
                        return null;
                    }), disk, new CacheManager(), new Chaptarr.Core.Test.Common.MemoryPendingNotifications())
                    { Definition = new NotificationDefinition { Id = 4, Settings = new SynologyIndexerSettings { UpdateLibrary = true } } };
                    var providers = new List<INotification> { subsonic, plex, kavita, synology };
                    var factory = Proxy<INotificationFactory>((method, _) => method.Name is nameof(INotificationFactory.OnBookDeleteEnabled) or nameof(INotificationFactory.GetAvailableProviders)
                        ? providers : throw new NotImplementedException(method.Name));
                    var statusFailures = 0;
                    var status = Proxy<INotificationStatusService>((method, _) =>
                    {
                        if (method.Name == nameof(INotificationStatusService.GetBlockedProviders)) return new List<NotificationStatus>();
                        if (method.Name == nameof(INotificationStatusService.RecordFailure)) statusFailures++;
                        else Assert.That(method.Name, Is.EqualTo(nameof(INotificationStatusService.RecordSuccess)));
                        return null;
                    });
                    var notifications = new NotificationService(factory, status, editionService, logger);
                    Exception physicalFailure = null;
                    var completions = 0;
                    dispatch = message =>
                    {
                        if (message is BookDeletedEvent deleted)
                        {
                            notifications.Handle(deleted);
                            Assert.That(subsonicUpdates + plexUpdates + kavitaUpdates + synologyDeletes, Is.Zero);
                            Assert.That(deleted.FilesAlreadyDeleted, Is.EqualTo(scenario != "keep"));
                        }
                        if (message is DeleteCompletedEvent completed)
                        {
                            completions++;
                            notifications.HandleAsync(completed);
                        }
                    };
                    try
                    {
                        new BookService(books, editionService, events, null, media, roots,
                            new SeriesBookLinkRepository(database, events), null, logger,
                            deleteMediaFiles: new Lazy<IDeleteMediaFiles>(() => deletion)).DeleteBook(book.Id, scenario != "keep");
                    }
                    catch (NzbDroneClientException ex) { physicalFailure = ex; }

                    var failed = scenario is "fail-first" or "fail-second";
                    var expected = scenario == "keep" || failed ? 0 : 1;
                    Assert.That(books.Find(book.Id), failed ? Is.Not.Null : Is.Null,
                        "A physical deletion failure must retain the book and its file inventory");
                    Assert.That(editions.Find(edition.Id), failed ? Is.Not.Null : Is.Null);
                    Assert.That(fileRows.GetFilesByBook(book.Id).Count, Is.EqualTo(failed ? paths.Length : 0));
                    Assert.That(physicalFailure != null, Is.EqualTo(failed));
                    Assert.That(completions, Is.EqualTo(expected));
                    Assert.That(subsonicUpdates, Is.EqualTo(expected));
                    Assert.That(plexUpdates, Is.EqualTo(expected));
                    Assert.That(kavitaUpdates, Is.EqualTo(paths.Length > 0 ? expected : 0));
                    Assert.That(synologyDeletes, Is.EqualTo(expected * paths.Length));
                    Assert.That(recycled.Count, Is.EqualTo(scenario == "fail-second" ? 1 : scenario == "missing" ? 0 : expected * paths.Length));
                    Assert.That(paths.Count(File.Exists), Is.EqualTo(scenario == "missing" ? 0 : paths.Length - recycled.Count));
                    Assert.That(statusFailures, Is.EqualTo(scenario == "scan-failure" ? 1 : 0));
                    Assert.That(subsonic.HasPendingQueue, Is.EqualTo(scenario == "scan-failure"));
                    if (scenario == "scan-failure")
                    {
                        failScan = false;
                        notifications.HandleAsync(new DeleteCompletedEvent());
                        Assert.That(subsonicUpdates, Is.EqualTo(2));
                        Assert.That(plexUpdates, Is.EqualTo(1));
                        Assert.That(kavitaUpdates, Is.EqualTo(1));
                        Assert.That(synologyDeletes, Is.EqualTo(paths.Length));
                        Assert.That(subsonic.HasPendingQueue, Is.False);
                    }
                });
            }
            finally
            {
                Directory.Delete(scratch, true);
            }
        }
    }
}
