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
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.Extras.Others;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport.Manual;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Qualities;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Extras
{
    [TestFixture]
    public class ExtraFileDeletionInventoryFixture
    {
        [Test]
        public void author_inventory_reads_only_selected_overlapping_or_escaping_extras([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, db =>
            {
                var events = Proxy<IEventAggregator>((_, _) => null);
                var authors = new AuthorRepository(db, events, LogManager.GetCurrentClassLogger());
                var selected = authors.Insert(new Author { Name = "Selected", CleanName = "selected", SortName = "selected", TitleSlug = "selected", Path = "/library/Selected", Added = DateTime.UtcNow });
                var unrelated = authors.Insert(new Author { Name = "Unrelated", CleanName = "unrelated", SortName = "unrelated", TitleSlug = "unrelated", Path = "/library/Unrelated", Added = DateTime.UtcNow });
                var extras = new ExtraFileRepository<MetadataFile>(db, events);
                MetadataFile Add(int owner, string path) => extras.Insert(new MetadataFile { AuthorId = owner, RelativePath = path, Extension = Path.GetExtension(path),
                    Consumer = "test", Added = DateTime.UtcNow, LastUpdated = DateTime.UtcNow });
                var owned = Add(selected.Id, "cover.jpg");
                var escaped = Add(unrelated.Id, "../Selected/metadata.json");
                Add(unrelated.Id, "book/cover.jpg");
                Assert.That(extras.GetFilesForAuthorDeletion(new[] { selected.Id }).Select(row => row.Id), Is.EquivalentTo(new[] { owned.Id, escaped.Id }));
                Assert.That(authors.AllAuthorPaths().Select(row => row.Name), Is.EquivalentTo(new[] { "Selected", "Unrelated" }));
            });
        }

        [Test]
        public void partial_delete_keeps_shared_cover([Values] bool postgres, [Values] bool deleteAll, [Values] bool afterDelete)
        {
            DualDatabaseFixture.Run(postgres, db =>
            {
                var events = Proxy<IEventAggregator>((m,a) => null);
                var author = new AuthorRepository(db, events, LogManager.GetCurrentClassLogger()).Insert(new Author {
                    Name="Frank Herbert", CleanName="frankherbert", SortName="frankherbert", TitleSlug="frank-herbert", Path="/library/Frank Herbert", Added=DateTime.UtcNow });
                var book = new BookRepository(db, events).Insert(new Book { AuthorId=author.Id, Title="Dune", CleanTitle="dune", TitleSlug="dune", MediaType=BookMediaType.Audiobook, Added=DateTime.UtcNow });
                var edition = new EditionRepository(db, events).Insert(new Edition { BookId=book.Id, Title="Dune", ForeignEditionId="gr:234225", ReadingFormatId=2 });
                var files = new MediaFileRepository(db, events);
                var parts = Enumerable.Range(1,2).Select(i=>files.Insert(new BookFile { EditionId=edition.Id, Edition=edition, Path=$"/library/Frank Herbert/Dune/Part {i}.mp3", Quality=new QualityModel(Quality.MP3), Modified=DateTime.UtcNow, DateAdded=DateTime.UtcNow, MediaInfo=new NzbDrone.Core.Parser.Model.MediaInfoModel(), Size=100, MediaType="audiobook" })).ToArray();
                var extras = new ExtraFileRepository<MetadataFile>(db, events);
                var cover = extras.Insert(new MetadataFile { AuthorId=author.Id, BookId=book.Id, BookFileId=null, RelativePath="Dune/cover.jpg", Extension=".jpg", Type=MetadataType.BookMetadata, Consumer="fixture", Added=DateTime.UtcNow, LastUpdated=DateTime.UtcNow });
                var selected = deleteAll ? parts : parts.Take(1).ToArray();
                if (afterDelete) files.DeleteMany(selected.Select(file => file.Id).ToList());
                // A complete selection may span multiple file-id parameter chunks.
                if (deleteAll) selected = selected.Take(1).Concat(Enumerable.Range(10000, 1000).Select(id => new BookFile {
                    Id = id, Edition = edition })).Concat(selected.Skip(1)).ToArray();
                var ids = extras.GetFilesForDeletion(selected).Select(x=>x.Id).ToList();
                Assert.That(ids.Contains(cover.Id), Is.EqualTo(deleteAll));
            });
        }

        [Test]
        public void preview_and_delete_follow_book_associations_even_with_another_media_file([Values] bool postgres, [Values] bool remainingMedia, [Values] bool survivingPart)
        {
            DualDatabaseFixture.Run(postgres, db =>
            {
                var folder = Directory.CreateTempSubdirectory("chaptarr-extra-inventory-").FullName;
                try
                {
                    var bookFolder = Directory.CreateDirectory(Path.Combine(folder, "Dune")).FullName;
                    var events = Proxy<IEventAggregator>((m, a) => null);
                    var author = new AuthorRepository(db, events, LogManager.GetCurrentClassLogger()).Insert(new Author {
                        Name = "Frank Herbert", CleanName = "frankherbert", SortName = "frankherbert", TitleSlug = "frank-herbert",
                        Path = folder, AudiobookPath = folder, Added = DateTime.UtcNow });
                    var books = new BookRepository(db, events);
                    Book AddBook(BookMediaType media, string slug) => books.Insert(new Book { AuthorId = author.Id, Title = "Dune",
                        CleanTitle = "dune", TitleSlug = slug, MediaType = media, Added = DateTime.UtcNow });
                    var book = AddBook(BookMediaType.Audiobook, "dune-audio");
                    var otherBook = AddBook(BookMediaType.Ebook, "dune-ebook");
                    var editions = new EditionRepository(db, events);
                    var edition = editions.Insert(new Edition { BookId = book.Id, Title = "Dune", ForeignEditionId = "gr:234225", ReadingFormatId = 2 });
                    var ebookEdition = editions.Insert(new Edition { BookId = otherBook.Id, Title = "Dune", ForeignEditionId = "gr:44767458", ReadingFormatId = 3 });
                    var mediaFiles = new MediaFileRepository(db, events);
                    BookFile AddFile(string name, Edition owner) => mediaFiles.Insert(new BookFile { Author = author,
                        Edition = owner, EditionId = owner.Id, Path = Path.Combine(bookFolder, name),
                        Quality = new QualityModel(owner == edition ? Quality.MP3 : Quality.EPUB), MediaType = owner == edition ? "audiobook" : "ebook",
                        DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow, Size = 14, MediaInfo = new NzbDrone.Core.Parser.Model.MediaInfoModel() });
                    var files = new[] { AddFile("Dune 10.mp3", edition), AddFile("Dune 11.mp3", edition) };
                    var survivor = AddFile(survivingPart ? "Dune 12.mp3" : "Dune.epub", survivingPart ? edition : ebookEdition);
                    File.WriteAllText(survivor.Path, "surviving primary");
                    foreach (var file in files) File.WriteAllText(file.Path, "selected audio");
                    var sidecar = Path.Combine(bookFolder, "cover.jpg");
                    var linked = Path.Combine(bookFolder, "Dune 10.cue");
                    File.WriteAllText(sidecar, "book cover");
                    File.WriteAllText(linked, "linked cue");
                    var otherMedia = Path.Combine(bookFolder, "Dune.epub");
                    if (remainingMedia) File.WriteAllText(otherMedia, "separate ebook primary");
                    var repository = new ExtraFileRepository<MetadataFile>(db, events);
                    MetadataFile Add(string path, int bookId, int? fileId) => repository.Insert(new MetadataFile {
                        AuthorId = author.Id, BookId = bookId, BookFileId = fileId, RelativePath = Path.GetRelativePath(folder, path),
                        Extension = Path.GetExtension(path), Type = MetadataType.BookMetadata, Consumer = "fixture",
                        Added = DateTime.UtcNow, LastUpdated = DateTime.UtcNow });
                    var cover = Add(sidecar, book.Id, null);
                    var cue = Add(linked, book.Id, files[0].Id);
                    var unrelated = Add(Path.Combine(bookFolder, "other.json"), otherBook.Id, null);
                    var unselectedPath = Path.Combine(bookFolder, "another.cue");
                    File.WriteAllText(unselectedPath, "surviving part cue");
                    var unselected = Add(unselectedPath, survivor.Edition.BookId, survivor.Id);
                    var removedIds = survivingPart ? new[] { cue.Id } : new[] { cover.Id, cue.Id };
                    var removedPaths = survivingPart ? new[] { linked } : new[] { sidecar, linked };
                    Assert.That(repository.GetFilesForDeletion(files).Select(extra => extra.Id), Is.EquivalentTo(removedIds));
                    Assert.That(repository.GetFilesForDeletion(Array.Empty<BookFile>()), Is.Empty);
                    // More files than one SQL parameter chunk still returns each book extra once.
                    var many = files.Concat(Enumerable.Range(1000, 1100).Select(id => new BookFile { Id = id, Edition = edition })).ToArray();
                    Assert.That(repository.GetFilesForDeletion(many).Select(extra => extra.Id), Is.EquivalentTo(removedIds));
                    var probes = new List<string>();
                    bool Exists(string path) { probes.Add(path); return File.Exists(path); }
                    var disk = Proxy<IDiskProvider>((m, a) => m.Name switch {
                        nameof(IDiskProvider.FileExistsCanonical) => Exists((string)a[0]),
                        nameof(IDiskProvider.GetFiles) => Directory.GetFiles((string)a[0]).ToList(),
                        nameof(IDiskProvider.GetParentFolder) => Path.GetDirectoryName((string)a[0]),
                        _ => throw new NotImplementedException(m.Name)
                    });
                    var preview = new ManualImportReplacementFiles(disk, repository, new ExtraFileRepository<OtherExtraFile>(db, events),
                        Proxy<IAuthorRepository>((m, a) => new List<Author> { author }),
                        Proxy<IBookRepository>((m, a) => new List<Book> { book }),
                        Proxy<IConfigService>((m, a) => m.Name == "get_RecycleBin" ? "" : throw new NotImplementedException(m.Name)));
                    var paths = preview.DescribeForDeletion(files).SelectMany(file => file.AssociatedPaths).Distinct().ToArray();
                    Assert.That(probes.Where(removedPaths.Contains), Is.EquivalentTo(removedPaths), "Each owned extra is probed once across all selected parts");
                    Assert.That(probes.Where(path => files.Any(file => file.Path == path)), Is.EquivalentTo(files.Select(file => file.Path)),
                        "Each selected primary is checked once for safe deletion");
                    Assert.That(paths, Is.EquivalentTo(removedPaths));
                    Assert.That(preview.Describe(files).SelectMany(file => file.AssociatedPaths), Does.Not.Contain(sidecar), "Replacement scope is unchanged");
                    var recycled = new List<string>();
                    var service = new MetadataFileService(repository, Proxy<IAuthorService>((m, a) => author), disk,
                        Proxy<IRecycleBinProvider>((m, a) => { recycled.Add((string)a[0]); File.Delete((string)a[0]); return null; }), LogManager.GetCurrentClassLogger());
                    foreach (var file in files) File.Delete(file.Path);
                    mediaFiles.DeleteMany(files.Select(file => file.Id));
                    var context = new BookFileDeletionContext(files.Select(file => file.Id), deletedFiles: files);
                    foreach (var file in files) service.Handle(new BookFileDeletedEvent(file, DeleteMediaFileReason.Manual, context: context));
                    Assert.That(recycled, Is.EquivalentTo(removedPaths));
                    Assert.That(File.Exists(sidecar), Is.EqualTo(survivingPart));
                    Assert.That(mediaFiles.All().Select(file => file.Id), Does.Contain(survivor.Id));
                    Assert.That(File.Exists(survivor.Path), Is.True);
                    Assert.That(File.ReadAllText(unselectedPath), Is.EqualTo("surviving part cue"));
                    Assert.That(File.Exists(linked), Is.False);
                    Assert.That(repository.All().Select(extra => extra.Id), Does.Contain(unrelated.Id).And.Contain(unselected.Id));
                    Assert.That(repository.All().Select(extra => extra.Id), Has.None.EqualTo(cue.Id));
                    Assert.That(repository.All().Any(extra => extra.Id == cover.Id), Is.EqualTo(survivingPart));
                    if (remainingMedia)
                    {
                        Assert.That(File.ReadAllText(otherMedia), Is.EqualTo("separate ebook primary"));
                    }
                }
                finally { Directory.Delete(folder, true); }
            });
        }
    }
}
