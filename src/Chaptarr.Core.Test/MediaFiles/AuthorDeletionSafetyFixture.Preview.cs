using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Exceptions;

namespace Chaptarr.Core.Test.MediaFiles
{
    public partial class AuthorDeletionSafetyFixture
    {
        [TestCase(BookMediaType.Audiobook)]
        [TestCase(BookMediaType.Ebook)]
        public void preview_discloses_independent_folder_contents_and_execution_deletes_exactly_that_side(BookMediaType type)
        {
            using var s = new Sandbox();
            s.Root("audio"); s.Root("ebooks");
            var author = s.Author(@"audio\Author", @"audio\Author", @"ebooks\Author");
            var audio = s.Track(author, @"audio\Author\Book.m4b");
            var ebook = s.Track(author, @"ebooks\Author\Book.epub");
            var selected = type == BookMediaType.Audiobook ? audio : ebook;
            var retained = type == BookMediaType.Audiobook ? ebook : audio;
            var other = Path.Combine(Path.GetDirectoryName(selected.Path), "notes.txt");
            File.WriteAllText(other, "included by explicit folder deletion");
            var preview = s.Deletion.PreviewAuthorFiles(new List<Author> { author }, type);
            Assert.That(preview.Folders, Has.Count.EqualTo(1));
            Assert.That(preview.Folders.Single().DeleteContents, Is.True);
            Assert.That(preview.Folders.Single().OtherFiles, Does.Contain(other));
            Assert.That(preview.Files.Select(file => file.Path), Does.Contain(selected.Path).And.Not.Contain(retained.Path));
            Assert.That(File.Exists(selected.Path), Is.True, "preview does not touch files");
            s.Service.DeleteAuthors(new List<int> { author.Id }, true, mediaType: type);
            Assert.That(File.Exists(selected.Path), Is.False);
            Assert.That(File.Exists(other), Is.False);
            Assert.That(File.Exists(retained.Path), Is.True);
            Assert.That(s.Files, Does.Contain(retained));
            Assert.That(s.Authors, Does.Contain(author));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void preview_requires_explicit_consent_for_other_authors_and_preserves_copies_outside_selected_folder(bool replica)
        {
            using var s = new Sandbox(); s.Root("media");
            var author = s.Author(@"media\Author");
            var other = s.Author(@"media\Other");
            s.Track(author, @"media\Author\Selected.m4b");
            var outside = s.Track(other, replica ? @"media\Other\Other.m4b" : @"media\Author\Other.m4b");
            var extra = s.Seed(replica ? @"media\Author\Copy.m4b" : @"media\Other\Copy.m4b");
            outside.ReplicaPaths.Add(extra);
            var preview = s.Deletion.PreviewAuthorFiles(new List<Author> { author });
            Assert.That(preview.OtherAuthors.Single().AuthorId, Is.EqualTo(other.Id));
            Assert.That(preview.OtherAuthors.Single().FileCount, Is.EqualTo(1));
            Assert.Throws<NzbDroneClientException>(() => s.Service.DeleteAuthor(author.Id, true));
            Assert.That(File.Exists(extra), Is.True);
            s.Service.DeleteAuthors(new List<int> { author.Id }, true, applyToBothFormats: true, deleteOtherAuthorsFiles: true);
            Assert.That(Directory.Exists(author.Path), Is.False);
            Assert.That(s.Authors, Does.Contain(other));
            var survivor = s.Files.Single();
            Assert.That(survivor.Path, Is.EqualTo(replica ? outside.Path : extra));
            Assert.That(File.Exists(survivor.Path), Is.True);
            if (!replica) Assert.That(survivor.EditionId, Is.Zero);
            Assert.That(survivor.ReplicaPaths, Is.Empty);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void explicit_folder_preview_includes_unmapped_files_and_keeps_their_outside_copies(bool outsideCopy)
        {
            using var s = new Sandbox(); s.Root("media");
            var author = s.Author(@"media\Author");
            var path = s.Seed(@"media\Author\Unmapped.m4b");
            var copy = outsideCopy ? s.Seed(@"media\Other\Copy.m4b") : null;
            var file = new NzbDrone.Core.MediaFiles.BookFile
            {
                Id = 100, Path = path, MediaType = "audiobook",
                ReplicaPaths = outsideCopy ? new List<string> { copy } : new List<string>()
            };
            s.Files.Add(file);
            var preview = s.Deletion.PreviewAuthorFiles(new List<Author> { author });
            Assert.That(preview.Files.Select(item => item.Path), Does.Contain(path));
            Assert.That(preview.Folders.Single().BookFileCount, Is.EqualTo(1));
            s.Service.DeleteAuthors(new List<int> { author.Id }, true, applyToBothFormats: true);
            Assert.That(File.Exists(path), Is.False);
            Assert.That(s.Files.Contains(file), Is.False);
            Assert.That(s.Files, Has.Count.EqualTo(outsideCopy ? 1 : 0));
            if (outsideCopy)
            {
                Assert.That(File.Exists(copy), Is.True);
                Assert.That(s.Files.Single().Path, Is.EqualTo(copy));
                Assert.That(s.Files.Single().ReplicaPaths, Is.Empty);
                Assert.That(s.Files.Single().EditionId, Is.Zero);
            }
        }

        [Test]
        public void confirmed_other_author_file_is_removed_from_inventory_only_after_folder_deletion()
        {
            using var s = new Sandbox(); s.Root("media");
            var author = s.Author(@"media\Author");
            var other = s.Author(@"media\Other");
            var file = s.Track(other, @"media\Author\Other.m4b");
            Assert.That(s.Deletion.PreviewAuthorFiles(new List<Author> { author }).OtherAuthors.Single().FileCount, Is.EqualTo(1));
            s.Service.DeleteAuthors(new List<int> { author.Id }, true, applyToBothFormats: true, deleteOtherAuthorsFiles: true);
            Assert.That(File.Exists(file.Path), Is.False);
            Assert.That(s.Files, Does.Not.Contain(file));
            Assert.That(s.Authors, Does.Contain(other));
        }
    }
}
