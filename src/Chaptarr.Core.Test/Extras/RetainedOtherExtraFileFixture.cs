using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Others;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.Extras
{
    [TestFixture]
    public class RetainedOtherExtraFileFixture
    {
        [Test]
        public void recycled_sidecars_are_untracked_when_a_later_recycle_or_retained_save_fails(
            [Values(false, true)] bool postgres, [Values(false, true)] bool failSecondRecycle)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var folder = Directory.CreateTempSubdirectory("chaptarr-extra-save-failure-").FullName;
                try
                {
                    var author = new Author { Id = 7, Path = folder, AudiobookPath = folder };
                    var file = new BookFile { Id = 19, Author = author, Path = Path.Combine(folder, "book.m4b"), Edition = new Edition { BookId = 11 } };
                    var repository = new ExtraFileRepository<OtherExtraFile>(database, InterfaceProxy.Proxy<IEventAggregator>((_, _) => null));
                    OtherExtraFile Insert(string name)
                    {
                        File.WriteAllText(Path.Combine(folder, name), name);
                        return repository.Insert(new OtherExtraFile { AuthorId = 7, BookId = 11, BookFileId = 19,
                            RelativePath = name, Extension = ".jpg", Added = DateTime.UtcNow, LastUpdated = DateTime.UtcNow });
                    }
                    var remove = Insert("cover.jpg");
                    var retain = Insert("alternative.jpg");
                    var failing = InterfaceProxy.Proxy<IExtraFileRepository<OtherExtraFile>>((method, args) =>
                    {
                        if (method.Name == nameof(IExtraFileRepository<OtherExtraFile>.UpdateMany))
                            throw new InvalidOperationException("planted retained-row save failure");
                        return method.Invoke(repository, args);
                    });
                    var disk = InterfaceProxy.Proxy<IDiskProvider>((method, args) => method.Name switch
                    {
                        nameof(IDiskProvider.FileExistsCanonical) => File.Exists((string)args[0]),
                        nameof(IDiskProvider.GetParentFolder) => Path.GetDirectoryName((string)args[0]),
                        _ => throw new AssertionException(method.Name)
                    });
                    var recycled = new List<string>();
                    var service = new OtherExtraFileService(failing, InterfaceProxy.Proxy<IAuthorService>((_, _) => author), disk,
                        InterfaceProxy.Proxy<IRecycleBinProvider>((_, args) =>
                        {
                            if (failSecondRecycle && recycled.Count == 1) throw new IOException("planted second recycle failure");
                            File.Delete((string)args[0]);
                            recycled.Add((string)args[0]);
                            return null;
                        }), LogManager.GetCurrentClassLogger());
                    var confirmed = failSecondRecycle ? new[] { remove.RelativePath, retain.RelativePath } : new[] { remove.RelativePath };
                    Assert.That(() => service.Handle(new BookFileDeletedEvent(file, DeleteMediaFileReason.Manual,
                        confirmed.Select(path => Path.Combine(folder, path)).ToArray())),
                        Throws.Exception.With.Message.EqualTo(failSecondRecycle ? "planted second recycle failure" : "planted retained-row save failure"));
                    Assert.That(recycled, Is.EqualTo(new[] { Path.Combine(folder, remove.RelativePath) }));
                    Assert.That(repository.All().Select(extra => extra.Id), Is.EqualTo(new[] { retain.Id }));
                    Assert.That(File.Exists(Path.Combine(folder, remove.RelativePath)), Is.False);
                    Assert.That(File.ReadAllText(Path.Combine(folder, retain.RelativePath)), Is.EqualTo("alternative.jpg"));
                }
                finally { Directory.Delete(folder, true); }
            });
        }

        [Test]
        public void retained_downloaded_sidecar_keeps_its_bytes_and_book_link(
            [Values(false, true)] bool postgres,
            [Values(DeleteMediaFileReason.MissingFromDisk, DeleteMediaFileReason.Manual, DeleteMediaFileReason.Upgrade)] DeleteMediaFileReason reason,
            [Values("audiobook", "ebook")] string mediaType)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var folder = Directory.CreateTempSubdirectory("chaptarr-retained-extra-").FullName;
                try
                {
                    var bookFolder = Directory.CreateDirectory(Path.Combine(folder, "Book")).FullName;
                    var sidecarPath = Path.Combine(bookFolder, "cover.jpg");
                    File.WriteAllText(sidecarPath, "keep these cover bytes");
                    var author = new Author { Id = 7, Path = folder, AudiobookPath = folder, EbookPath = folder };
                    var file = new BookFile { Id = 19, Author = author, MediaType = mediaType,
                        Path = Path.Combine(bookFolder, mediaType == "ebook" ? "book.epub" : "book.m4b"),
                        Edition = new Edition { BookId = 11 } };
                    if (reason == DeleteMediaFileReason.Manual)
                        file.Path = Path.Combine(folder, "Moved", Path.GetFileName(file.Path));
                    var events = InterfaceProxy.Proxy<IEventAggregator>((_, _) => null);
                    var repository = new ExtraFileRepository<OtherExtraFile>(database, events);
                    var original = repository.Insert(new OtherExtraFile { AuthorId = 7, BookId = 11, BookFileId = 19,
                        RelativePath = Path.Combine("Book", "cover.jpg"), Extension = ".jpg",
                        Added = DateTime.UtcNow, LastUpdated = DateTime.UtcNow });
                    var successor = new BookFile { Id = 20, Path = Path.Combine(bookFolder, "replacement.m4b"), Edition = new Edition { BookId = 11 } };
                    var disk = InterfaceProxy.Proxy<IDiskProvider>((method, args) => method.Name switch
                    {
                        nameof(IDiskProvider.FileExistsCanonical) => reason == DeleteMediaFileReason.Upgrade && (string)args[0] == successor.Path
                            ? throw new IOException("replacement path unavailable") : File.Exists((string)args[0]),
                        nameof(IDiskProvider.GetFiles) => Directory.GetFiles((string)args[0]).ToList(),
                        nameof(IDiskProvider.GetParentFolder) => Path.GetDirectoryName((string)args[0]),
                        _ => throw new AssertionException(method.Name)
                    });
                    using var logs = new LogFactory();
                    var sink = new NLog.Targets.MemoryTarget { Layout = "${level}|${message}" };
                    var config = new NLog.Config.LoggingConfiguration(); config.AddRuleForAllLevels(sink); logs.Configuration = config;
                    var service = new OtherExtraFileService(repository, InterfaceProxy.Proxy<IAuthorService>((_, _) => author), disk,
                        InterfaceProxy.Proxy<IRecycleBinProvider>((_, _) => throw new AssertionException("retained bytes must not be recycled")), logs.GetLogger("retained-extra"));
                    service.Handle(new BookFileDeletedEvent(file, reason,
                        replacementFiles: reason == DeleteMediaFileReason.Upgrade ? new[] { successor } : null));
                    var saved = new ExtraFileRepository<OtherExtraFile>(database, events).Get(original.Id);
                    Assert.Multiple(() =>
                    {
                        Assert.That(saved.BookId, Is.EqualTo(11));
                        Assert.That(saved.BookFileId, Is.Null);
                        Assert.That(saved.RelativePath, Is.EqualTo(original.RelativePath));
                        Assert.That(File.ReadAllText(sidecarPath), Is.EqualTo("keep these cover bytes"));
                        Assert.That(repository.GetFilesByBookFile(file.Id), Is.Empty);
                        Assert.That(repository.GetFilesByBook(author.Id, 11).Select(extra => extra.Id), Is.EqualTo(new[] { original.Id }));
                        Assert.That(repository.GetFilesForDeletion(new[] { file }).Select(extra => extra.Id), Is.EqualTo(new[] { original.Id }),
                            "a later whole-book deletion preview must still list the retained sidecar");
                        Assert.That(sink.Logs.Any(line => line.StartsWith("Error|")), Is.False);
                    });
                    if (reason != DeleteMediaFileReason.MissingFromDisk)
                        Assert.That(sink.Logs.Any(line => line.StartsWith("Warn|")), Is.True);
                    if (reason == DeleteMediaFileReason.Manual)
                        Assert.That(sink.Logs.Single(line => line.StartsWith("Warn|")), Does.Contain("unresolved physical ownership"));
                }
                finally { Directory.Delete(folder, true); }
            });
        }
    }
}
