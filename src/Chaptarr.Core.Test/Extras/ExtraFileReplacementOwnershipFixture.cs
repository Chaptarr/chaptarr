using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core;
using NzbDrone.Core.Books;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Extras
{
    [TestFixture]
    public class ExtraFileReplacementOwnershipFixture
    {
        [TestCase(false, false, false)]
        [TestCase(true, false, false)]
        [TestCase(false, true, false)]
        [TestCase(false, false, true)]
        [TestCase(false, false, false, true)]
        [TestCase(true, false, false, false, true)]
        [TestCase(false, false, false, false, true)]
        [TestCase(false, false, false, true, true)]
        public void retained_metadata_should_follow_only_a_committed_same_book_successor(
            bool externallyModified, bool samePath, bool otherBook, bool unreadable = false, bool differentFolder = false)
        {
            var author = new Author { Id = 1, Path = @"C:\library\Author".AsOsAgnostic(), AudiobookPath = @"C:\library\Author".AsOsAgnostic() };
            var old = new BookFile { Id = 10, Path = @"C:\library\Author\Book\old.mp3".AsOsAgnostic(), Author = author,
                Edition = new Edition { BookId = 2 } };
            var successor = new BookFile { Id = 11, Path = samePath ? old.Path : differentFolder ? @"C:\library\Author\New Book\new.m4b".AsOsAgnostic() : @"C:\library\Author\Book\new.m4b".AsOsAgnostic(),
                Edition = new Edition { BookId = otherBook ? 3 : 2 } };
            var sidecar = new MetadataFile { Id = 20, AuthorId = 1, BookId = 2, BookFileId = 10,
                RelativePath = "Book/metadata.json", Type = MetadataType.BookMetadata, Hash = "original".SHA256Hash() };
            var stored = new List<MetadataFile> { sidecar };
            var recycled = new List<string>();
            var upserts = 0;
            var repository = Proxy<IExtraFileRepository<MetadataFile>>((method, args) =>
            {
                if (method.Name == nameof(IExtraFileRepository<MetadataFile>.GetFilesByBookFile))
                    return stored.Where(file => file.BookFileId == (int)args[0]).ToList();
                if (method.Name == nameof(IExtraFileRepository<MetadataFile>.GetFilesByBookFiles))
                    return stored.ToList();
                if (method.Name == nameof(IExtraFileRepository<MetadataFile>.UpdateMany)) { upserts++; return null; }
                if (method.Name == nameof(IExtraFileRepository<MetadataFile>.InsertMany)) return null;
                if (method.Name == nameof(IExtraFileRepository<MetadataFile>.DeleteMany))
                { stored.RemoveAll(file => ((IEnumerable<int>)args[0]).Contains(file.Id)); return null; }
                if (method.Name == nameof(IExtraFileRepository<MetadataFile>.DeleteForBookFile))
                { stored.RemoveAll(file => file.BookFileId == (int)args[0]); return null; }
                throw new NotImplementedException(method.Name);
            });
            var disk = Proxy<IDiskProvider>((method, args) =>
            {
                if (method.Name == nameof(IDiskProvider.FileExistsCanonical)) return true;
                if (method.Name == nameof(IDiskProvider.ReadAllText))
                {
                    if (unreadable) throw new IOException("Sidecar unavailable");
                    return externallyModified ? "changed bytes" : "original";
                }
                if (method.Name == nameof(IDiskProvider.GetFiles))
                    return new[] { successor.Path, @"C:\library\Author\Book\metadata.json".AsOsAgnostic() }
                        .Where(path => Path.GetDirectoryName(path) == (string)args[0]).ToList();
                if (method.Name == nameof(IDiskProvider.GetParentFolder)) return Path.GetDirectoryName((string)args[0]);
                throw new NotImplementedException(method.Name);
            });
            var recycle = Proxy<IRecycleBinProvider>((method, args) => { recycled.Add((string)args[0]); return null; });
            var service = new MetadataFileService(repository, Proxy<IAuthorService>((m, a) => author), disk, recycle,
                LogManager.GetCurrentClassLogger());
            service.Handle(new BookFileDeletedEvent(old, DeleteMediaFileReason.Upgrade, null, new[] { successor }));

            var retainOwnership = !otherBook && (!differentFolder || externallyModified || unreadable);
            Assert.That(upserts, Is.EqualTo(retainOwnership ? 1 : 0));
            Assert.That(stored, Has.Count.EqualTo(retainOwnership ? 1 : 0));
            if (retainOwnership) Assert.That(stored[0].BookFileId, Is.EqualTo(differentFolder ? (int?)null : successor.Id));
            Assert.That(recycled, Is.EqualTo(!retainOwnership
                ? new[] { @"C:\library\Author\Book\metadata.json".AsOsAgnostic() } : Array.Empty<string>()));
            if (retainOwnership)
            {
                Assert.That(stored[0].Hash, Is.EqualTo(externallyModified || unreadable ? null : "original".SHA256Hash()));
            }
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void cross_folder_replacement_should_preserve_edited_sidecar_on_disk_and_in_database(bool postgres, bool edited)
        {
            DualDatabaseFixture.Run(postgres, db =>
            {
                var folder = Directory.CreateTempSubdirectory("chaptarr-sidecar-").FullName;
                try
                {
                    Directory.CreateDirectory(Path.Combine(folder, "Old Book"));
                    Directory.CreateDirectory(Path.Combine(folder, "New Book"));
                    var path = Path.Combine(folder, "Old Book", "metadata.json");
                    File.WriteAllText(path, edited ? "edited by owner" : "original");
                    var author = new Author { Id = 1, Path = folder, AudiobookPath = folder };
                    var old = new BookFile { Id = 10, Author = author, Edition = new Edition { BookId = 2 }, Path = Path.Combine(folder, "Old Book", "old.mp3") };
                    var successor = new BookFile { Id = 11, Edition = new Edition { BookId = 2 }, Path = Path.Combine(folder, "New Book", "new.m4b") };
                    File.WriteAllText(successor.Path, "committed replacement");
                    var repository = new ExtraFileRepository<MetadataFile>(db, Proxy<NzbDrone.Core.Messaging.Events.IEventAggregator>((m, a) => null));
                    repository.Insert(new MetadataFile { AuthorId = 1, BookId = edited ? null : 2, BookFileId = 10, Consumer = "fixture",
                        RelativePath = Path.Combine("Old Book", "metadata.json"), Extension = ".json", Type = MetadataType.BookMetadata,
                        Hash = "original".SHA256Hash(), Added = DateTime.UtcNow, LastUpdated = DateTime.UtcNow });
                    var disk = Proxy<IDiskProvider>((method, args) => method.Name switch
                    {
                        nameof(IDiskProvider.FileExistsCanonical) => File.Exists((string)args[0]),
                        nameof(IDiskProvider.ReadAllText) => File.ReadAllText((string)args[0]),
                        nameof(IDiskProvider.GetFiles) => Directory.GetFiles((string)args[0]).ToList(),
                        nameof(IDiskProvider.GetParentFolder) => Path.GetDirectoryName((string)args[0]),
                        _ => throw new NotImplementedException(method.Name)
                    });
                    var recycle = Proxy<IRecycleBinProvider>((method, args) => { File.Delete((string)args[0]); return null; });
                    var service = new MetadataFileService(repository, Proxy<IAuthorService>((m, a) => author), disk, recycle, LogManager.GetCurrentClassLogger());
                    service.Handle(new BookFileDeletedEvent(old, DeleteMediaFileReason.Upgrade, replacementFiles: new[] { successor }));
                    Assert.That(File.Exists(path), Is.EqualTo(edited));
                    var saved = new ExtraFileRepository<MetadataFile>(db, Proxy<NzbDrone.Core.Messaging.Events.IEventAggregator>((m, a) => null)).All().ToList();
                    Assert.That(saved, Has.Count.EqualTo(edited ? 1 : 0));
                    if (edited)
                    {
                        Assert.That(File.ReadAllText(path), Is.EqualTo("edited by owner"));
                        Assert.That(saved.Single().BookId, Is.EqualTo(2));
                        Assert.That(saved.Single().BookFileId, Is.Null);
                        Assert.That(saved.Single().Hash, Is.Null);
                    }
                }
                finally { Directory.Delete(folder, true); }
            });
        }

        [Test]
        public void replacement_observers_load_extras_once_without_enumerating_unrelated_folder_contents()
        {
            var author = new Author { Id = 1, Path = @"C:\library\Author".AsOsAgnostic() };
            var files = Enumerable.Range(1, 64).Select(id => new BookFile
                { Id = id, Author = author, Path = $@"C:\library\Author\Book\{id}.mp3".AsOsAgnostic(), Edition = new Edition { BookId = 2 } }).ToList();
            var extras = files.Select(file => new MetadataFile { Id = file.Id, BookFileId = file.Id,
                BookId = 2, RelativePath = $"Book/{file.Id}.json" }).ToList();
            var reads = 0;
            var listings = 0;
            var recycled = new List<string>();
            var repository = Proxy<IExtraFileRepository<MetadataFile>>((method, args) =>
            {
                if (method.Name == nameof(IExtraFileRepository<MetadataFile>.GetFilesByBookFiles)) { reads++; return extras.ToList(); }
                if (method.Name == nameof(IExtraFileRepository<MetadataFile>.DeleteForBookFile) ||
                    method.Name == nameof(IExtraFileRepository<MetadataFile>.DeleteMany)) return null;
                throw new NotImplementedException(method.Name);
            });
            var disk = Proxy<IDiskProvider>((method, args) =>
            {
                if (method.Name == nameof(IDiskProvider.FileExistsCanonical)) return true;
                if (method.Name == nameof(IDiskProvider.GetFiles)) { listings++; return new List<string> { @"C:\library\Author\Book\remaining.m4b".AsOsAgnostic() }; }
                throw new NotImplementedException(method.Name);
            });
            var service = new MetadataFileService(repository, Proxy<IAuthorService>((m, a) => author), disk,
                Proxy<IRecycleBinProvider>((m, a) => { recycled.Add((string)a[0]); return null; }), LogManager.GetCurrentClassLogger());
            var context = new BookFileDeletionContext(files.Select(file => file.Id));
            foreach (var file in files) service.Handle(new BookFileDeletedEvent(file, DeleteMediaFileReason.Upgrade, context: context));
            Assert.That(reads, Is.EqualTo(1));
            Assert.That(listings, Is.Zero);
            Assert.That(recycled, Has.Count.EqualTo(64));
        }
    }
}
