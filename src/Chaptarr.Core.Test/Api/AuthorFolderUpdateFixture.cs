using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Chaptarr.Api.V1.Author;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.RootFolders;
using static Chaptarr.Core.Test.MediaFiles.AuthorDeletionSafetyFixture;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class AuthorFolderUpdateFixture
    {
        public static IBuildFileNames CreateFileNames() => Proxy<IBuildFileNames>((m, a) =>
            m.Name == nameof(IBuildFileNames.GetAuthorFolder) ? ((Author)a[0]).Name : throw new NotImplementedException(m.Name));

        [Test]
        public void editor_root_selection_preserves_relative_folder_unless_moving(
            [Values(false, true)] bool postgres, [Values("audiobook", "ebook")] string media,
            [Values(false, true)] bool moveFiles, [Values(false, true)] bool sameRoot)
        {
            CheckEditorRootSelection(postgres, media, moveFiles, sameRoot);
        }

        [Test]
        public void editor_no_move_uses_the_files_actual_root_for_relative_folder(
            [Values(false, true)] bool postgres, [Values("audiobook", "ebook")] string media)
        {
            CheckEditorRootSelection(postgres, media, false, false, outsideOldRoot: true);
        }

        [Test]
        public void editor_move_flag_without_a_root_change_does_not_move_anything(
            [Values(false, true)] bool postgres, [Values("audiobook", "ebook")] string media)
        {
            CheckEditorRootSelection(postgres, media, true, false, omitRoot: true);
        }

        [Test]
        public void editor_move_preserves_other_formats_scattered_path(
            [Values(false, true)] bool postgres, [Values("audiobook", "ebook")] string media)
        {
            CheckEditorRootSelection(postgres, media, true, false, scatteredOtherFormat: true);
        }

        private static void CheckEditorRootSelection(bool postgres, string media, bool moveFiles, bool sameRoot,
            bool outsideOldRoot = false, bool omitRoot = false, bool scatteredOtherFormat = false)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var events = Proxy<IEventAggregator>((m, a) => null);
                var logger = LogManager.GetCurrentClassLogger();
                var root = Path.GetFullPath("folder-fixture");
                var audioRoot = Path.Combine(root, "audiobooks");
                var ebookRoot = Path.Combine(root, "ebooks");
                var newRoot = Path.Combine(root, "new-root");
                var physicalRoot = Path.Combine(root, "other-configured-root");
                var folders = new List<RootFolder>
                {
                    new() { Path = audioRoot, FolderType = FolderType.Audiobook },
                    new() { Path = ebookRoot, FolderType = FolderType.Ebook },
                    new() { Path = newRoot, FolderType = media == "ebook" ? FolderType.Ebook : FolderType.Audiobook },
                    new() { Path = physicalRoot, FolderType = media == "ebook" ? FolderType.Ebook : FolderType.Audiobook }
                };
                var roots = Proxy<IRootFolderService>((m, a) => m.Name switch
                {
                    nameof(IRootFolderService.All) => folders,
                    nameof(IRootFolderService.GetBestRootFolderPath) => folders.Where(f => f.Path.IsParentPath((string)a[0]))
                        .OrderByDescending(f => f.Path.Length).First().Path,
                    _ => throw new NotImplementedException(m.Name)
                });
                var names = Proxy<IBuildFileNames>((m, a) => m.Name == nameof(IBuildFileNames.GetAuthorFolder)
                    ? ((Author)a[0]).Name : throw new NotImplementedException(m.Name));
                var moves = new List<NzbDrone.Core.Books.Commands.MoveAuthorCommand>();
                var commands = Proxy<IManageCommandQueue>((m, a) =>
                {
                    Assert.That(m.Name, Is.EqualTo(nameof(IManageCommandQueue.PushMany)), "Moves are queued as one batch");
                    moves.AddRange((List<NzbDrone.Core.Books.Commands.MoveAuthorCommand>)a[0]);
                    return null;
                });
                var repository = new AuthorRepository(database, events, logger);
                var author = repository.Insert(new Author
                {
                    Name = "Walter Isaacson", CleanName = "walterisaacson", SortName = "isaacson walter",
                    TitleSlug = Guid.NewGuid().ToString("N"), Added = DateTime.UtcNow,
                    AudiobookRootFolderPath = audioRoot, EbookRootFolderPath = ebookRoot,
                    AudiobookPath = Path.Combine(outsideOldRoot && media == "audiobook" ? physicalRoot : audioRoot, "library", "Walter Isaacson"),
                    EbookPath = Path.Combine(outsideOldRoot && media == "ebook" ? physicalRoot : ebookRoot, "library", "Walter Isaacson"),
                    Path = Path.Combine(audioRoot, "library", "Walter Isaacson")
                });
                if (scatteredOtherFormat)
                {
                    if (media == "audiobook") author.EbookPath = Path.Combine(root, "scattered-ebooks", author.Name);
                    else author.AudiobookPath = Path.Combine(root, "scattered-audio", author.Name);
                    repository.Update(author);
                }
                var service = new AuthorService(repository, events, new AuthorPathBuilder(names, roots), roots, commands, new CacheManager(),
                    new BookRepository(database, events), null, logger, null, null, null, database);
                var controller = new AuthorEditorController(service, commands, roots,
                    new TestQualityProfileService(), new TestMetadataProfileService(), names);
                var selectedRoot = sameRoot || omitRoot ? (media == "audiobook" ? audioRoot : ebookRoot) : newRoot;
                var originalFolder = media == "audiobook" ? author.AudiobookPath : author.EbookPath;
                var otherFolder = media == "audiobook" ? author.EbookPath : author.AudiobookPath;
                controller.SaveAll(new AuthorEditorResource
                {
                    AuthorIds = new List<int> { author.Id },
                    AudiobookRootFolderPath = !omitRoot && media == "audiobook" ? selectedRoot : null,
                    EbookRootFolderPath = !omitRoot && media == "ebook" ? selectedRoot : null,
                    MoveFiles = moveFiles
                });
                var stored = repository.Get(author.Id);
                Assert.That(media == "audiobook" ? stored.AudiobookRootFolderPath : stored.EbookRootFolderPath, Is.EqualTo(selectedRoot));
                Assert.That(media == "audiobook" ? stored.AudiobookPath : stored.EbookPath,
                    Is.EqualTo(omitRoot ? originalFolder : Path.Combine(selectedRoot, moveFiles ? author.Name : Path.Combine("library", author.Name))));
                Assert.That(media == "audiobook" ? stored.EbookPath : stored.AudiobookPath,
                    Is.EqualTo(otherFolder));
                Assert.That(moves.Count, Is.EqualTo(moveFiles && !omitRoot ? 1 : 0));
                if (moveFiles && !omitRoot)
                {
                    Assert.That(moves[0].SourcePath, Is.EqualTo(originalFolder));
                    Assert.That(moves[0].DestinationPath, Is.EqualTo(Path.Combine(selectedRoot, author.Name)));
                    Assert.That(moves[0].MediaType, Is.EqualTo(media));
                }
            });
        }

        [TestCase("audiobook")]
        [TestCase("ebook")]
        public void resource_update_honors_explicit_media_folder(string media)
        {
            var author = new Author { AudiobookPath = "/audio/old", EbookPath = "/ebooks/old" };
            var payload = author.ToResource();
            if (media == "audiobook") payload.AudiobookFolder = "/audio/new";
            else payload.EbookFolder = "/ebooks/new";
            payload.ToModel(author);
            Assert.That(media == "audiobook" ? author.AudiobookPath : author.EbookPath,
                Is.EqualTo(media == "audiobook" ? "/audio/new" : "/ebooks/new"));
        }

        [TestCase("outside")]
        [TestCase("root")]
        [TestCase("wrong-format")]
        [TestCase("shared-wrong-format")]
        [TestCase("two-shared-destinations")]
        public void invalid_folder_changes_are_rejected_before_any_path_changes(string scenario)
        {
            var root = Path.GetFullPath("folder-validation");
            var mixed = Path.Combine(root, "mixed");
            var ebook = Path.Combine(root, "ebooks");
            var source = Path.Combine(mixed, "Frank Herbert");
            var author = new Author { AudiobookPath = source, EbookPath = source, Path = source };
            var roots = new List<RootFolder> { new() { Path = mixed, FolderType = FolderType.Mixed },
                new() { Path = ebook, FolderType = FolderType.Ebook } };
            var destination = scenario switch
            {
                "outside" => Path.Combine(root, "outside", "Author"),
                "root" => mixed,
                "wrong-format" or "shared-wrong-format" => Path.Combine(ebook, "Author"),
                _ => Path.Combine(mixed, "New")
            };
            Assert.Throws<FluentValidation.ValidationException>(() => AuthorPathBuilder.ChangeFolders(author,
                scenario == "shared-wrong-format" ? null : destination,
                scenario == "shared-wrong-format" ? destination : scenario == "two-shared-destinations" ? Path.Combine(mixed, "Other") : null,
                roots, true));
            Assert.That(author.AudiobookPath, Is.EqualTo(source));
            Assert.That(author.EbookPath, Is.EqualTo(source));
        }

        [Test]
        public void no_move_changes_only_the_requested_path_even_in_a_shared_root()
        {
            var root = Path.GetFullPath("folder-shared");
            var old = Path.Combine(root, "Original");
            var target = Path.Combine(root, "New");
            var author = new Author { AudiobookPath = old, EbookPath = old, Path = old };
            var moves = AuthorPathBuilder.ChangeFolders(author, null, target,
                new List<RootFolder> { new() { Path = root, FolderType = FolderType.Mixed } }, false);
            Assert.That(moves, Is.Empty);
            Assert.That(author.AudiobookPath, Is.EqualTo(old));
            Assert.That(author.EbookPath, Is.EqualTo(target));
            Assert.That(author.Path, Is.EqualTo(old));
        }
    }
}
