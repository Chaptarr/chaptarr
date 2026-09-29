using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.MediaFiles;

namespace Chaptarr.Core.Test.Extras
{
    [TestFixture]
    public class CleanExtraFileServiceFixture
    {
        private const string AudioBase = "/mnt/Media/Audio Books/Some Author";
        private const string EbookBase = "/mnt/Media/Books/Some Author";

        private class DiskProxy : DispatchProxy
        {
            public HashSet<string> ExistingFiles { get; } = new HashSet<string>();
            public List<string> Probes { get; } = new List<string>();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IDiskProvider.FileExists) && args?.Length == 1 && args[0] is string path)
                {
                    Probes.Add(path);
                    return ExistingFiles.Contains(path);
                }

                throw new NotImplementedException($"Test proxy does not implement IDiskProvider.{targetMethod?.Name}");
            }
        }

        private class MetadataFileServiceProxy : DispatchProxy
        {
            public List<MetadataFile> Files { get; set; } = new List<MetadataFile>();
            public List<List<int>> DeleteManyCalls { get; } = new List<List<int>>();
            public List<int> DeleteSingleCalls { get; } = new List<int>();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case nameof(IMetadataFileService.GetFilesByAuthor):
                        return Files;
                    case nameof(IMetadataFileService.DeleteMany):
                        DeleteManyCalls.Add(((IEnumerable<int>)args[0]).ToList());
                        return null;
                    case nameof(IMetadataFileService.Delete):
                        DeleteSingleCalls.Add((int)args[0]);
                        return null;
                }

                throw new NotImplementedException($"Test proxy does not implement IMetadataFileService.{targetMethod?.Name}");
            }
        }

        private class MediaFileServiceProxy : DispatchProxy
        {
            public List<BookFile> Files { get; set; } = new List<BookFile>();
            public int GetFilesByAuthorCalls { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IMediaFileService.GetFilesByAuthor) && args?.Length == 1 && args[0] is int)
                {
                    GetFilesByAuthorCalls++;
                    return Files;
                }

                throw new NotImplementedException($"Test proxy does not implement IMediaFileService.{targetMethod?.Name}");
            }
        }

        private DiskProxy _disk;
        private MetadataFileServiceProxy _metadata;
        private MediaFileServiceProxy _media;
        private CleanExtraFileService _sut;
        private Author _author;

        [SetUp]
        public void SetUp()
        {
            var disk = DispatchProxy.Create<IDiskProvider, DiskProxy>();
            var metadata = DispatchProxy.Create<IMetadataFileService, MetadataFileServiceProxy>();
            var media = DispatchProxy.Create<IMediaFileService, MediaFileServiceProxy>();
            _disk = (DiskProxy)(object)disk;
            _metadata = (MetadataFileServiceProxy)(object)metadata;
            _media = (MediaFileServiceProxy)(object)media;
            _sut = new CleanExtraFileService(metadata, disk, LogManager.GetCurrentClassLogger(), media);
            _author = new Author { Id = 1, Name = "Some Author", AudiobookPath = AudioBase, EbookPath = EbookBase, Path = AudioBase };
        }

        private static MetadataFile Row(int id, string relative, int? bookFileId = null) =>
            new MetadataFile { Id = id, AuthorId = 1, RelativePath = relative, BookFileId = bookFileId };

        [Test]
        public void should_delete_every_missing_row_in_a_single_batch_not_one_delete_per_row()
        {
            _metadata.Files = new List<MetadataFile> { Row(1, "A/cover.jpg"), Row(2, "B/cover.jpg"), Row(3, "C/cover.jpg") };
            _disk.ExistingFiles.Add(EbookBase + "/B/cover.jpg");

            _sut.Clean(_author);

            Assert.That(_metadata.DeleteManyCalls, Has.Count.EqualTo(1), "one batched delete");
            Assert.That(_metadata.DeleteManyCalls[0], Is.EquivalentTo(new[] { 1, 3 }));
            Assert.That(_metadata.DeleteSingleCalls, Is.Empty, "no per-row deletes");
        }

        [Test]
        public void should_not_delete_anything_when_every_file_exists()
        {
            _metadata.Files = new List<MetadataFile> { Row(1, "A/cover.jpg"), Row(2, "B/cover.jpg") };
            _disk.ExistingFiles.Add(AudioBase + "/A/cover.jpg");
            _disk.ExistingFiles.Add(EbookBase + "/B/cover.jpg");

            _sut.Clean(_author);

            Assert.That(_metadata.DeleteManyCalls, Is.Empty);
            Assert.That(_metadata.DeleteSingleCalls, Is.Empty);
        }

        [Test]
        public void should_probe_the_base_path_the_linked_book_file_lives_under_first()
        {
            // An ebook sidecar: the ebook folder is the right place, the audiobook folder would be a guaranteed miss.
            _media.Files = new List<BookFile> { new BookFile { Id = 50, Path = EbookBase + "/Book/Book.epub", MediaType = "ebook" } };
            _metadata.Files = new List<MetadataFile> { Row(1, "Book/cover.jpg", 50) };
            _disk.ExistingFiles.Add(EbookBase + "/Book/cover.jpg");

            _sut.Clean(_author);

            Assert.That(_disk.Probes, Is.EqualTo(new[] { EbookBase + "/Book/cover.jpg" }), "found on the first probe, the audiobook folder is never touched");
            Assert.That(_metadata.DeleteManyCalls, Is.Empty);
        }

        [Test]
        public void should_still_find_a_file_under_another_base_path_when_the_preferred_one_misses()
        {
            _media.Files = new List<BookFile> { new BookFile { Id = 50, Path = EbookBase + "/Book/Book.epub", MediaType = "ebook" } };
            _metadata.Files = new List<MetadataFile> { Row(1, "Book/cover.jpg", 50) };
            _disk.ExistingFiles.Add(AudioBase + "/Book/cover.jpg");   // exists only under the other format's folder

            _sut.Clean(_author);

            Assert.That(_metadata.DeleteManyCalls, Is.Empty, "a file that exists somewhere must be kept");
            Assert.That(_disk.Probes.First(), Is.EqualTo(EbookBase + "/Book/cover.jpg"), "preferred base is tried first");
            Assert.That(_disk.Probes, Does.Contain(AudioBase + "/Book/cover.jpg"));
        }

        [Test]
        public void should_keep_the_default_probe_order_for_rows_that_have_no_book_file()
        {
            _metadata.Files = new List<MetadataFile> { Row(1, "poster.jpg") };
            _disk.ExistingFiles.Add(EbookBase + "/poster.jpg");

            _sut.Clean(_author);

            Assert.That(_disk.Probes, Is.EqualTo(new[] { AudioBase + "/poster.jpg", EbookBase + "/poster.jpg" }), "audiobook, then ebook (legacy path equals audiobook and is de-duplicated)");
            Assert.That(_metadata.DeleteManyCalls, Is.Empty);
        }

        [Test]
        public void should_ignore_rows_without_a_relative_path_instead_of_throwing()
        {
            _metadata.Files = new List<MetadataFile> { Row(1, null), Row(2, "  "), Row(3, "gone.jpg") };

            Assert.DoesNotThrow(() => _sut.Clean(_author));

            Assert.That(_metadata.DeleteManyCalls.Single(), Is.EqualTo(new[] { 3 }), "only the row that really is missing is deleted");
        }

        [Test]
        public void should_do_no_disk_or_media_file_work_for_an_author_with_no_metadata_files()
        {
            _metadata.Files = new List<MetadataFile>();

            _sut.Clean(_author);

            Assert.That(_disk.Probes, Is.Empty);
            Assert.That(_media.GetFilesByAuthorCalls, Is.EqualTo(0));
            Assert.That(_metadata.DeleteManyCalls, Is.Empty);
        }

        [Test]
        public void should_work_without_a_media_file_service_using_the_default_probe_order()
        {
            var disk = DispatchProxy.Create<IDiskProvider, DiskProxy>();
            var metadata = DispatchProxy.Create<IMetadataFileService, MetadataFileServiceProxy>();
            var diskProxy = (DiskProxy)(object)disk;
            var metadataProxy = (MetadataFileServiceProxy)(object)metadata;
            metadataProxy.Files = new List<MetadataFile> { Row(1, "Book/cover.jpg", 50), Row(2, "gone.jpg") };
            diskProxy.ExistingFiles.Add(EbookBase + "/Book/cover.jpg");
            var sut = new CleanExtraFileService(metadata, disk, LogManager.GetCurrentClassLogger());

            Assert.DoesNotThrow(() => sut.Clean(_author));

            Assert.That(metadataProxy.DeleteManyCalls.Single(), Is.EqualTo(new[] { 2 }), "the existing file is kept, the missing one is deleted");
        }
    }
}
