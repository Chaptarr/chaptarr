using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Events;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.RootFolders;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.RootFolders
{
    [TestFixture]
    public class RootFolderRemovalSafetyFixture
    {
        [TestCase("audiobook", ".m4b")]
        [TestCase("ebook", ".epub")]
        public void root_removed_event_keeps_media_replicas_and_sidecars_and_does_not_notify_a_file_delete(string mediaType, string extension)
        {
            var scratch = Path.Combine(Path.GetTempPath(), "chaptarr-root-removal-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            try
            {
                var primary = Path.Combine(scratch, "book" + extension);
                var replica = Path.Combine(scratch, "replica" + extension);
                var sidecar = Path.Combine(scratch, "book.nfo");
                foreach (var path in new[] { primary, replica, sidecar }) File.WriteAllText(path, "keep these bytes");
                var author = new Author { Id = 7, Path = scratch };
                var file = new BookFile { Id = 11, EditionId = 21, Path = primary, MediaType = mediaType, Author = author,
                    Edition = new Edition { Id = 21, BookId = 31 }, ReplicaPaths = new List<string> { replica } };
                var files = new List<BookFile> { file };
                var recordDeletes = 0;
                var extraDeletes = 0;
                var logger = LogManager.GetLogger("RootRemovalSafety");
                var repository = Proxy<IMediaFileRepository>((method, args) => method.Name switch
                {
                    "GetFilesWithBasePath" => files,
                    "Get" => files.AsEnumerable(),
                    "DeleteMany" => CountDelete(),
                    _ => throw new InvalidOperationException(method.Name)
                });
                object CountDelete() { recordDeletes++; return null; }
                var disk = Proxy<IDiskProvider>((method, args) => throw new InvalidOperationException("Unauthorized disk operation: " + method.Name));
                var bin = Proxy<IRecycleBinProvider>((method, args) => throw new InvalidOperationException("Unauthorized recycle operation: " + method.Name));
                var extras = Proxy<IExtraFileRepository<MetadataFile>>((method, args) =>
                {
                    if (method.Name == "DeleteForBookFile") { extraDeletes++; return null; }
                    throw new InvalidOperationException(method.Name);
                });
                var extraHandler = new MetadataFileService(extras, null, disk, bin, logger);
                var deletionHandler = new MediaFileDeletionService(disk, bin, null, null, null, null, null, null, logger, null, null, null, null);
                var notificationHandler = new NotificationService(
                    Proxy<INotificationFactory>((method, args) => throw new InvalidOperationException("No physical deletion occurred")), null, null, logger);
                var delivered = new List<BookFileDeletedEvent>();
                var events = Proxy<IEventAggregator>((method, args) =>
                {
                    var deleted = (BookFileDeletedEvent)args[0];
                    delivered.Add(deleted);
                    extraHandler.Handle(deleted);
                    deletionHandler.Handle(deleted);
                    notificationHandler.Handle(deleted);
                    return null;
                });
                var service = new MediaFileService(repository, events,
                    Proxy<IIngestQueueRepository>((method, args) => 1), disk, logger);

                service.HandleAsync(new ModelEvent<RootFolder>(new RootFolder { Id = 17, Path = scratch }, ModelAction.Deleted));

                Assert.That(recordDeletes, Is.EqualTo(1));
                Assert.That(extraDeletes, Is.EqualTo(1));
                Assert.That(delivered, Has.Count.EqualTo(1));
                Assert.That(delivered[0].Reason, Is.EqualTo(DeleteMediaFileReason.NoLinkedEpisodes));
                foreach (var path in new[] { primary, replica, sidecar }) Assert.That(File.ReadAllText(path), Is.EqualTo("keep these bytes"));
            }
            finally { Directory.Delete(scratch, true); }
        }

        [TestCase(FolderType.Audiobook)]
        [TestCase(FolderType.Ebook)]
        [TestCase(FolderType.Mixed)]
        public void removing_root_configuration_never_touches_media_or_queues_file_deletion(FolderType type)
        {
            var root = new RootFolder { Id = 17, Path = "/library", FolderType = type };
            var actions = new List<string>();
            var repository = Proxy<IRootFolderRepository>((method, args) =>
            {
                Assert.That(args[0], Is.EqualTo(17));
                if (method.Name == "Get") return root;
                if (method.Name == "Delete") { actions.Add("remove root"); return null; }
                throw new InvalidOperationException(method.Name);
            });
            var staging = Proxy<IIngestQueueRepository>((method, args) =>
            {
                Assert.That(method.Name, Is.EqualTo("PurgeUnderPath"));
                Assert.That(args[0], Is.EqualTo(root.Path));
                actions.Add("remove pending scan work");
                return 2;
            });
            var diskCalls = new List<string>();
            var commandCalls = new List<string>();
            var service = new RootFolderService(repository, staging,
                Proxy<IDiskProvider>((method, args) => { diskCalls.Add(method.Name); return null; }),
                Proxy<IManageCommandQueue>((method, args) => { commandCalls.Add(method.Name); return null; }),
                LogManager.GetLogger("RootRemovalSafety"));

            service.Remove(root.Id);

            Assert.That(actions, Is.EqualTo(new[] { "remove pending scan work", "remove root" }));
            Assert.That(diskCalls, Is.Empty, "Removing a root is never permission to delete its files");
            Assert.That(commandCalls, Is.Empty, "Do not defer file deletion to a background command either");
        }
    }
}
