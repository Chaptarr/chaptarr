using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Extras;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Extras.Metadata.Consumers.AudioBookShelf;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class OrganizeSidecarSafetyFixture
    {
        private static T Proxy<T>(Func<MethodInfo, object[], object> call) where T : class
        {
            var proxy = DispatchProxy.Create<T, AuthorDeletionSafetyFixture.CallbackProxy>();
            ((AuthorDeletionSafetyFixture.CallbackProxy)(object)proxy).Call = call;
            return proxy;
        }

        private sealed class Events : IEventAggregator
        {
            public List<IEvent> Published { get; } = new();
            public Action<AuthorRenamedEvent> OnRename { get; set; }
            public void PublishEvent<T>(T message) where T : class, IEvent
            {
                Published.Add(message);
                if (message is AuthorRenamedEvent renamed) OnRename?.Invoke(renamed);
            }
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void organize_removes_empty_source_but_keeps_sidecar_and_other_media(bool ebook, bool sidecar)
        {
            RunScenario(ebook, sidecar, false);
        }

        [Test]
        public void import_colocation_should_not_leave_a_genuinely_empty_source_folder()
        {
            RunScenario(true, false, true);
        }

        [TestCase(false, "cover.jpg")]
        [TestCase(true, "cover.jpg")]
        [TestCase(false, "metadata.json")]
        [TestCase(true, "metadata.json")]
        public void organize_moves_owned_sidecar_before_empty_cleanup(bool ebook, string extraName)
        {
            RunScenario(ebook, true, false, true, false, extraName);
        }

        [TestCase(false, "cover.jpg")]
        [TestCase(true, "cover.jpg")]
        [TestCase(false, "metadata.json")]
        [TestCase(true, "metadata.json")]
        public void organize_must_not_take_sidecar_from_an_unmoved_copy_folder(bool ebook, string extraName)
        {
            RunScenario(ebook, true, false, true, true, extraName);
        }

        private static void RunScenario(bool ebook, bool sidecar, bool importColocation, bool tracked = false, bool otherOwner = false, string extraName = "cover.jpg")
        {
            var temp = Path.Combine(Path.GetTempPath(), "chaptarr-cleanup-review-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                var root = Path.Combine(temp, "media");
                var authorPath = Path.Combine(root, "Author");
                var source = Path.Combine(authorPath, "Old Title");
                var target = Path.Combine(authorPath, "New Title");
                var otherMedia = Path.Combine(temp, "other-media", "Author");
                Directory.CreateDirectory(source);
                Directory.CreateDirectory(target);
                Directory.CreateDirectory(otherMedia);
                var sentinel = Path.Combine(otherMedia, "Keep.txt");
                File.WriteAllText(sentinel, "synthetic other-media sentinel");
                var file = new BookFile
                {
                    Id = 1,
                    EditionId = 1,
                    MediaType = ebook ? "ebook" : "audiobook",
                    Quality = new QualityModel(ebook ? Quality.EPUB : Quality.M4B),
                    Path = Path.Combine(source, ebook ? "Text.epub" : "Audio.m4b")
                };
                File.WriteAllText(file.Path, "synthetic media");
                var extraFolder = otherOwner ? Path.Combine(authorPath, "Unmoved Copy") : source;
                Directory.CreateDirectory(extraFolder);
                var extra = Path.Combine(extraFolder, extraName);
                if (sidecar) File.WriteAllText(extra, "synthetic untracked sidecar");
                var author = new Author
                {
                    Id = 1,
                    Name = "Synthetic Author",
                    Path = authorPath,
                    AudiobookPath = ebook ? otherMedia : authorPath,
                    EbookPath = ebook ? authorPath : otherMedia,
                    AudiobookRootFolderPath = root,
                    EbookRootFolderPath = root
                };
                var book = new Book
                {
                    Id = 1,
                    AuthorId = 1,
                    Author = author,
                    Title = "Synthetic Title",
                    MediaType = ebook ? BookMediaType.Ebook : BookMediaType.Audiobook,
                    RemoteProviderIds = new HashSet<string> { "hc:123" }
                };
                file.Author = author;
                file.Edition = new Edition { Id = 1, BookId = 1, Book = book, Title = book.Title };
                var authorFiles = new List<BookFile> { file };
                if (otherOwner)
                {
                    var copy = new BookFile
                    {
                        Id = 999,
                        Author = author,
                        EditionId = file.EditionId,
                        Edition = file.Edition,
                        MediaType = file.MediaType,
                        Quality = file.Quality,
                        Path = Path.Combine(extraFolder, Path.GetFileName(file.Path))
                    };
                    File.WriteAllText(copy.Path, "synthetic unmoved copy");
                    authorFiles.Add(copy);
                }
                var plan = new BookFileMovePlan
                {
                    CanOrganize = true,
                    SourceAuthorFolderPath = authorPath,
                    DestinationAuthorFolderPath = authorPath,
                    DestinationPath = Path.Combine(target, Path.GetFileName(file.Path)),
                    ReplicaPaths = importColocation ? new List<string>() : null
                };
                var media = Proxy<IMediaFileService>((m, a) => m.Name switch
                {
                    nameof(IMediaFileService.GetFilesByAuthor) => authorFiles,
                    nameof(IMediaFileService.GetFilesByBook) => authorFiles,
                    nameof(IMediaFileService.GetFilesByBooks) => authorFiles,
                    nameof(IMediaFileService.Get) => new List<BookFile> { file },
                    nameof(IMediaFileService.Update) => null,
                    _ => throw new NotImplementedException(m.Name)
                });
                var authors = Proxy<IAuthorService>((m, a) => m.Name == nameof(IAuthorService.GetAuthor) ? author : throw new NotImplementedException(m.Name));
                var mover = Proxy<IMoveBookFiles>((m, a) =>
                {
                    if (m.Name == nameof(IMoveBookFiles.GetOrganizeDestination)) return plan;
                    if (m.Name != nameof(IMoveBookFiles.MoveBookFile)) throw new NotImplementedException(m.Name);
                    File.Move(file.Path, plan.DestinationPath);
                    file.Path = plan.DestinationPath;
                    return file;
                });
                var realDisk = Chaptarr.Core.Test.Common.RealDiskProvider.Create(LogManager.GetCurrentClassLogger());
                var disk = Proxy<IDiskProvider>((m, a) =>
                {
                    if (a?.FirstOrDefault() is string path && !temp.IsParentPath(path)) throw new InvalidOperationException("Sandbox boundary violation");
                    return m.Invoke(realDisk, a);
                });
                var events = new Events();
                if (tracked)
                {
                    var consumer = new AudioBookShelfMetadata(null, null, null);
                    var rows = new List<MetadataFile>
                    {
                        new MetadataFile
                        {
                            Id = 1, AuthorId = author.Id, BookId = book.Id,
                            BookFileId = extraName == "metadata.json" ? (otherOwner ? 999 : file.Id) : null,
                            Type = extraName == "metadata.json" ? MetadataType.BookMetadata : MetadataType.BookImage,
                            Consumer = consumer.GetType().Name,
                            RelativePath = Path.GetRelativePath(authorPath, extra), Extension = Path.GetExtension(extraName)
                        }
                    };
                    var metadataFiles = Proxy<IMetadataFileService>((m, a) => m.Name switch
                    {
                        nameof(IMetadataFileService.GetFilesByAuthor) => rows,
                        nameof(IMetadataFileService.Upsert) => null,
                        _ => throw new NotImplementedException(m.Name)
                    });
                    var factory = Proxy<IMetadataFactory>((m, a) => m.Name == "GetAvailableProviders"
                        ? new List<IMetadata> { consumer } : throw new NotImplementedException(m.Name));
                    var config = Proxy<IConfigService>((m, a) => m.Name == "get_CopyUsingHardlinks" ? false : throw new NotImplementedException(m.Name));
                    var metadata = new MetadataService(config, disk, null, null, null, factory, null,
                        null, null, metadataFiles, null, LogManager.GetCurrentClassLogger());
                    var extras = new ExtraService(media, null, disk, config,
                        new IManageExtraFiles[] { metadata }, LogManager.GetCurrentClassLogger());
                    events.OnRename = extras.Handle;
                }
                if (importColocation)
                {
                    var roots = Proxy<IRootFolderService>((m, a) => m.Name == nameof(IRootFolderService.GetBestRootFolder)
                        ? new RootFolder { Path = root, FolderType = FolderType.Mixed, PlaceEbooksWithAudiobooks = true }
                        : throw new NotImplementedException(m.Name));
                    var books = Proxy<IBookService>((m, a) => m.Name == nameof(IBookService.GetBooksByAuthorId)
                        ? new List<Book> { book } : throw new NotImplementedException(m.Name));
                    var audio = new Book { Id = 2, AuthorId = 1, Author = author, MediaType = BookMediaType.Audiobook, RemoteProviderIds = new HashSet<string> { "hc:123" } };
                    var imported = new BookFile { Id = 2, Author = author, MediaType = "audiobook", Quality = new QualityModel(Quality.M4B) };
                    new EbookColocateOnAudiobookImportHandler(roots, books, media, mover, disk, events, LogManager.GetCurrentClassLogger())
                        .Handle(new BookImportedEvent(author, audio, new List<BookFile> { imported }, new List<BookFile>(), true, null));
                }
                else
                {
                    new RenameBookFileService(authors, media, mover, events, disk, LogManager.GetCurrentClassLogger(), null)
                        .Execute(new RenameFilesCommand(author.Id, new List<int> { file.Id }));
                }

                Assert.That(File.Exists(plan.DestinationPath), Is.True, "The move must actually complete");
                Assert.That(events.Published.OfType<AuthorRenamedEvent>().Count(), Is.EqualTo(1));
                Assert.That(File.Exists(sentinel), Is.True);
                if (otherOwner) Assert.That(File.Exists(authorFiles[1].Path), Is.True, "The other copy remains and still needs its sidecar");
                Assert.That(Directory.Exists(root), Is.True);
                if (sidecar && (!tracked || otherOwner)) Assert.That(File.Exists(extra), Is.True, "Do not move an untracked or another file's sidecar");
                if (tracked && !otherOwner) Assert.That(File.Exists(Path.Combine(target, extraName)), Is.True);
                Assert.That(Directory.Exists(source), Is.EqualTo(sidecar && !tracked), "Only a genuinely empty source folder should be removed");
            }
            finally
            {
                Directory.Delete(temp, true);
            }
        }
    }
}
