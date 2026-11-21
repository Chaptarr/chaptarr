using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport.Manual;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class BookFileImportReplicaLifecycleFixture
    {
        [TestCase(false)]
        [TestCase(true)]
        public void import_should_leave_replicas_untouched_until_primary_is_committed(bool confirmed)
        {
            var (service, file, local, disk, deleted, transfers) = Create(confirmed);
            service.CopyBookFile(file, local);
            Assert.That(local.PendingReplicaPaths, Is.EqualTo(new[] { "/library/Author/Other/book.epub" }));
            Assert.That(deleted, Is.Empty);
            Assert.That(transfers, Is.Empty);
            Assert.Throws<InvalidOperationException>(() => service.CompleteImportReplicas(file, local));
            Assert.That(deleted, Is.Empty);

            file.Id = 99; // Database commit gives the successor its own ID.
            Assert.That(service.CompleteImportReplicas(file, local), Is.True);
            Assert.That(deleted, Is.EqualTo(new[] { "/library/Author/Other/book.epub" }));
            Assert.That(transfers, Is.EqualTo(new[] { "/library/Author/Other/book.epub" }));
            Assert.That(file.ReplicaPaths, Is.EqualTo(new[] { "/library/Author/Other/book.epub" }));
            Assert.That(local.PendingReplicaPaths, Is.Null);
        }

        [Test]
        public void unconfirmed_existing_copy_and_primary_collision_should_never_be_deleted()
        {
            var (service, file, local, disk, deleted, transfers) = Create(true);
            file.Id = 99;
            file.ReplicaPaths.Add(file.Path);
            local.ConfirmedReplacementFiles.First().AssociatedPaths.Clear();
            local.PendingReplicaPaths = new List<string> { "/library/Author/Other/book.epub" };
            service.CompleteImportReplicas(file, local);
            Assert.That(deleted, Is.Empty);
            Assert.That(transfers, Is.Empty);
            Assert.That(disk.Contains(file.Path), Is.True);
            Assert.That(file.ReplicaPaths, Is.EqualTo(new[] { "/library/Author/Other/book.epub" }));
        }

        [Test]
        public void import_destination_preview_should_not_clean_up_old_replicas()
        {
            var (service, file, local, disk, deleted, transfers) = Create(false, cleanup: true);
            service.GetImportDestinationPath(file, local);
            Assert.That(deleted, Is.Empty);
            Assert.That(local.PendingReplicaPaths, Is.Empty);
            Assert.That(disk.Contains(file.ReplicaPaths[0]), Is.True);
        }

        private static (BookFileMovingService Service, BookFile File, LocalBook Local, HashSet<string> Disk,
            List<string> Deleted, List<string> Transfers) Create(bool confirmed, bool cleanup = false)
        {
            const string primary = "/library/Author/Book/book.epub";
            const string replica = "/library/Author/Other/book.epub";
            var disk = new HashSet<string>(PathEqualityComparer.Instance) { primary, replica };
            var deleted = new List<string>();
            var transfers = new List<string>();
            var author = new Author { Id = 1, EbookRootFolderPath = "/library", EbookPath = "/library/Author", Path = "/library/Author" };
            var book = new Book { Id = 2, AuthorId = 1, Author = author, MediaType = BookMediaType.Ebook };
            var edition = new Edition { Id = 3, BookId = 2, Book = book };
            var file = new BookFile { Path = primary, EditionId = 3, Edition = edition, Quality = new QualityModel(Quality.EPUB),
                ReplicaPaths = new List<string> { replica }, MediaType = "ebook" };
            var local = new LocalBook { Path = primary, Author = author, Book = book, Edition = edition, Quality = file.Quality };
            if (confirmed) local.ConfirmedReplacementFiles = new List<ManualImportReplacementFile>
                { new() { Id = 10, BookId = 2, Path = primary, AssociatedPaths = new List<string> { replica } } };
            var diskProvider = Proxy<IDiskProvider>((method, args) =>
            {
                if (method.Name == nameof(IDiskProvider.FileExists)) return disk.Contains((string)args[0]);
                if (method.Name == nameof(IDiskProvider.FolderExists)) return true;
                throw new NotImplementedException(method.Name);
            });
            var mover = new BookFileMovingService(
                Proxy<IEditionService>((m, a) => throw new NotImplementedException(m.Name)),
                Proxy<IUpdateBookFileService>((m, a) => null),
                Proxy<IBuildFileNames>((m, a) => "Book/book"),
                Proxy<IBuildAuthorPaths>((m, a) => "/library/Author"),
                Proxy<INamingConfigService>((m, a) => NamingConfig.Default),
                Proxy<IEbookColocationPlanner>((m, a) => cleanup ? EbookColocationPlan.Skipped(EbookColocationSkipReason.RootNotMixedOrDisabled, true) :
                    new EbookColocationPlan { Applies = true, PrimaryPath = primary, ReplicaPaths = new List<string> { replica } }),
                Proxy<IDiskTransferService>((m, a) => { transfers.Add((string)a[1]); disk.Add((string)a[1]); return (TransferMode)a[2]; }),
                diskProvider,
                Proxy<IRecycleBinProvider>((m, a) => { deleted.Add((string)a[0]); disk.Remove((string)a[0]); return null; }),
                Proxy<IRootFolderWatchingService>((m, a) => null),
                Proxy<IMediaFileAttributeService>((m, a) => null),
                Proxy<IEventAggregator>((m, a) => null),
                Proxy<IConfigService>((m, a) => false),
                Proxy<IFileMutationSafetyService>((m, a) => null), LogManager.GetCurrentClassLogger());
            return (mover, file, local, disk, deleted, transfers);
        }
    }
}
