using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.Extras.Others;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport.Manual;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class EbookReplacementAssociatedFilesFixture
    {
        [TestCase(true, true, true)]
        [TestCase(true, false, true)]
        [TestCase(false, true, true)]
        [TestCase(false, false, true)]
        [TestCase(true, true, false)]
        [TestCase(false, true, false)]
        [TestCase(true, true, true, false)]
        [TestCase(false, true, true, false)]
        public void confirmed_import_defers_owned_ebook_copies_until_commit(bool colocate, bool copy, bool confirmedReplica, bool retainedRow = true)
        {
            var scratch = Path.Combine(Path.GetTempPath(), "ebook-replacement-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            try
            {
                var root = new RootFolder { Id = 1, Path = Path.Combine(scratch, "library"), FolderType = FolderType.Mixed, PlaceEbooksWithAudiobooks = colocate };
                var author = new Author { Id = 1, Name = "Test Writer", EbookRootFolderPath = root.Path, AudiobookRootFolderPath = root.Path };
                var authorFolder = Path.Combine(root.Path, author.Name);
                var ebook = new Book { Id = 1, AuthorId = author.Id, MediaType = BookMediaType.Ebook, GoodreadsWorkId = "gr:100" };
                var audio = new Book { Id = 2, AuthorId = author.Id, MediaType = BookMediaType.Audiobook, GoodreadsWorkId = "gr:100" };
                var edition = new Edition { Id = 1, BookId = ebook.Id, Book = ebook };
                var audioFiles = new[] { "Narration A", "Narration B" }.Select((folder, index) => new BookFile
                {
                    Id = 10 + index, Path = Path.Combine(authorFolder, folder, "The Book.m4b")
                }).ToList();
                foreach (var file in audioFiles)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file.Path));
                    File.WriteAllText(file.Path, "audio untouched");
                }

                var replica = Path.Combine(Path.GetDirectoryName(audioFiles[1].Path), "The Book.epub");
                File.WriteAllText(replica, "old ebook copy");
                var independentEbook = Path.Combine(Path.GetDirectoryName(audioFiles[1].Path), "Other Book.epub");
                File.WriteAllText(independentEbook, "independent ebook");
                var source = Path.Combine(scratch, "The Book.epub");
                File.WriteAllText(source, "new ebook");
                var fileToImport = new BookFile
                {
                    Id = retainedRow ? 40 : 0, EditionId = edition.Id, Edition = edition, Author = author, Path = source,
                    Quality = new QualityModel(Quality.EPUB), MediaType = "ebook", ReplicaPaths = retainedRow ? new List<string> { replica } : new List<string>()
                };
                var disk = InterfaceProxy.Proxy<IDiskProvider>((method, args) => method.Name switch
                {
                    nameof(IDiskProvider.FileExists) or nameof(IDiskProvider.FileExistsCanonical) => File.Exists((string)args[0]),
                    nameof(IDiskProvider.FolderExists) => Directory.Exists((string)args[0]),
                    nameof(IDiskProvider.FolderWritable) => true,
                    _ => throw new InvalidOperationException(method.Name)
                });
                var roots = InterfaceProxy.Proxy<IRootFolderService>((method, args) => method.Name == nameof(IRootFolderService.All) ? new List<RootFolder> { root } : root);
                var planner = new EbookColocationPlanner(
                    InterfaceProxy.Proxy<IBookService>((_, _) => new List<Book> { ebook, audio }),
                    InterfaceProxy.Proxy<IMediaFileService>((_, _) => audioFiles), roots, disk);
                var transferCount = 0;
                var transfers = InterfaceProxy.Proxy<IDiskTransferService>((method, args) =>
                {
                    Assert.That(method.Name, Is.EqualTo(nameof(IDiskTransferService.TransferFile)));
                    transferCount++;
                    var mode = (TransferMode)args[2];
                    if (mode == TransferMode.Move)
                    {
                        File.Move((string)args[0], (string)args[1]);
                    }
                    else
                    {
                        File.Copy((string)args[0], (string)args[1], (bool)args[3]);
                    }
                    return mode;
                });
                var recycled = new List<string>();
                var recycle = InterfaceProxy.Proxy<IRecycleBinProvider>((_, args) =>
                {
                    recycled.Add((string)args[0]);
                    File.Delete((string)args[0]);
                    return null;
                });
                var mover = new BookFileMovingService(null,
                    InterfaceProxy.Proxy<IUpdateBookFileService>((_, _) => null),
                    InterfaceProxy.Proxy<IBuildFileNames>((_, _) => "The Book"),
                    InterfaceProxy.Proxy<IBuildAuthorPaths>((_, _) => authorFolder), null, planner, transfers, disk, recycle,
                    InterfaceProxy.Proxy<IRootFolderWatchingService>((_, _) => null),
                    InterfaceProxy.Proxy<IMediaFileAttributeService>((_, _) => null),
                    InterfaceProxy.Proxy<IEventAggregator>((_, _) => null),
                    InterfaceProxy.Proxy<IConfigService>((_, _) => false),
                    InterfaceProxy.Proxy<IFileMutationSafetyService>((_, _) => null), LogManager.GetCurrentClassLogger());
                var local = new LocalBook
                {
                    Path = source, Author = author, Book = ebook, Edition = edition, Quality = fileToImport.Quality,
                    ConfirmedReplacementFiles = new List<ManualImportReplacementFile>()
                };
                var destination = mover.GetImportDestinationPath(fileToImport, local);
                var preview = new ManualImportReplacementFiles(disk,
                    InterfaceProxy.Proxy<IExtraFileRepository<MetadataFile>>((_, _) => new List<MetadataFile>()),
                    InterfaceProxy.Proxy<IExtraFileRepository<OtherExtraFile>>((_, _) => new List<OtherExtraFile>()),
                    InterfaceProxy.Proxy<IAuthorRepository>((_, _) => new List<Author>()),
                    InterfaceProxy.Proxy<IBookRepository>((_, _) => new List<Book>()),
                    InterfaceProxy.Proxy<IConfigService>((_, _) => string.Empty));
                Assert.That(preview.Describe(new[] { new BookFile
                {
                    Id = 40, Path = destination, Edition = edition, ReplicaPaths = new List<string> { replica }
                } }).Single().AssociatedPaths, Is.EqualTo(new[] { replica }));
                local.ConfirmedReplacementFiles = new[] { new ManualImportReplacementFile
                {
                    Id = 40, BookId = ebook.Id, Path = destination,
                    AssociatedPaths = confirmedReplica ? new List<string> { replica } : new List<string>()
                } };
                Assert.That(File.ReadAllText(replica), Is.EqualTo("old ebook copy"), "Planning cannot remove managed copies");
                if (copy)
                {
                    mover.CopyBookFile(fileToImport, local);
                }
                else
                {
                    mover.MoveBookFile(fileToImport, local);
                }

                Assert.That(fileToImport.Path, Is.EqualTo(destination));
                Assert.That(File.ReadAllText(replica), Is.EqualTo("old ebook copy"), "No replica mutation before the primary import commits");
                Assert.That(transferCount, Is.EqualTo(1));
                Assert.That(recycled, Is.Empty);
                if (!retainedRow)
                {
                    var deleted = new BookFile { Id = 40, Path = destination, ReplicaPaths = new List<string> { replica } };
                    var deletion = new MediaFileDeletionService(disk, recycle, null, null, null, null, null, null, LogManager.GetCurrentClassLogger(), metadataFiles: Chaptarr.Core.Test.Common.EmptyDeletionExtras.Repository<NzbDrone.Core.Extras.Metadata.Files.MetadataFile>(), otherFiles: Chaptarr.Core.Test.Common.EmptyDeletionExtras.Repository<NzbDrone.Core.Extras.Others.OtherExtraFile>(), metadataFileService: Chaptarr.Core.Test.Common.EmptyDeletionExtras.Metadata(), otherFileService: Chaptarr.Core.Test.Common.EmptyDeletionExtras.Other());
                    deletion.Handle(new BookFileDeletedEvent(deleted, DeleteMediaFileReason.Upgrade, Array.Empty<string>()));
                    Assert.That(File.ReadAllText(replica), Is.EqualTo("old ebook copy"), "An unconfirmed copy must survive event cleanup");
                    deletion.Handle(new BookFileDeletedEvent(deleted, DeleteMediaFileReason.Upgrade, new[] { replica }));
                    fileToImport.Id = 41;
                }
                Assert.That(mover.CompleteImportReplicas(fileToImport, local), Is.True);
                Assert.That(File.ReadAllText(fileToImport.Path), Is.EqualTo("new ebook"));
                Assert.That(File.Exists(replica), Is.EqualTo(colocate || !confirmedReplica));
                if (File.Exists(replica))
                {
                    Assert.That(File.ReadAllText(replica), Is.EqualTo(confirmedReplica ? "new ebook" : "old ebook copy"));
                }
                Assert.That(recycled, Is.EquivalentTo(confirmedReplica ? new[] { replica } : Array.Empty<string>()));
                Assert.That(mover.CompleteImportReplicas(fileToImport, local), Is.False, "The post-commit step runs once");
                Assert.That(audioFiles.All(file => File.ReadAllText(file.Path) == "audio untouched"), Is.True);
                Assert.That(File.ReadAllText(independentEbook), Is.EqualTo("independent ebook"));
            }
            finally
            {
                Directory.Delete(scratch, true);
            }
        }
    }
}
