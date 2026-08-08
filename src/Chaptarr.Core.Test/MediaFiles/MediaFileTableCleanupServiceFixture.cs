using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.MediaFiles;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class MediaFileTableCleanupServiceFixture
    {
        private sealed class StubMediaFileService : IMediaFileService
        {
            public List<BookFile> Files { get; } = new();
            public List<BookFile> Deleted { get; } = new();
            public DeleteMediaFileReason? DeleteReason { get; private set; }

            public BookFile Add(BookFile bookFile) => throw new NotImplementedException();
            public void AddMany(List<BookFile> bookFiles) => throw new NotImplementedException();
            public void Update(BookFile bookFile) => throw new NotImplementedException();
            public void Update(List<BookFile> bookFiles) => throw new NotImplementedException();
            public void Delete(BookFile bookFile, DeleteMediaFileReason reason) => throw new NotImplementedException();
            public void DeleteMany(List<BookFile> bookFiles, DeleteMediaFileReason reason)
            {
                Deleted.AddRange(bookFiles ?? new List<BookFile>());
                DeleteReason = reason;
            }

            public List<IFileInfo> FilterUnchangedFiles(List<IFileInfo> files, FilterFilesType filter) => throw new NotImplementedException();
            public List<BookFile> GetFilesByAuthor(int authorId) => throw new NotImplementedException();
            public List<BookFile> GetFilesByBook(int bookId) => throw new NotImplementedException();
            public List<BookFile> GetFilesByBooks(List<int> bookIds) => throw new NotImplementedException();
            public List<BookFile> GetFilesByEdition(int editionId) => throw new NotImplementedException();
            public List<BookFile> GetUnmappedFiles() => throw new NotImplementedException();
            public List<BookFile> GetUnmappedFiles(string mediaType) => throw new NotImplementedException();
            public List<BookFile> GetUnmappedFiles(IEnumerable<int> ids, string mediaType) => throw new NotImplementedException();
            public BookFile Get(int id) => throw new NotImplementedException();
            public List<BookFile> Get(IEnumerable<int> ids) => throw new NotImplementedException();
            public List<BookFile> GetFilesWithBasePath(string path) => GetFilesWithBasePath(path, null);
            public List<BookFile> GetFilesWithBasePath(string path, string mediaType)
            {
                return Files
                    .Where(file => file.Path.StartsWith(path, StringComparison.OrdinalIgnoreCase))
                    .Where(file => string.IsNullOrWhiteSpace(mediaType) || string.Equals(file.MediaType, mediaType, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            public BookFile GetFileWithPath(string path) => throw new NotImplementedException();
            public List<BookFile> GetFileWithPath(List<string> path) => throw new NotImplementedException();
            public void UpdateMediaInfo(List<BookFile> bookFiles) => throw new NotImplementedException();
            public List<BookFile> GetFilesByAuthorAndMediaType(int authorId, string mediaType) => throw new NotImplementedException();
        }

        private class DiskProviderProxy : DispatchProxy
        {
            public HashSet<string> ExistingPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> LooseOnlyPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IDiskProvider.FileExists))
                {
                    return ExistingPaths.Contains((string)args[0]) || LooseOnlyPaths.Contains((string)args[0]);
                }

                if (targetMethod?.Name == nameof(IDiskProvider.FileExistsCanonical))
                {
                    return ExistingPaths.Contains((string)args[0]);
                }

                return targetMethod?.ReturnType?.IsValueType == true
                    ? Activator.CreateInstance(targetMethod.ReturnType)
                    : null;
            }
        }

        [Test]
        public void clean_should_delete_row_absent_from_complete_scan_when_disk_recheck_confirms_missing()
        {
            var missing = new BookFile
            {
                Id = 1,
                Path = "/books/missing.mp3",
                EditionId = 42,
                MediaType = "audiobook"
            };
            var media = new StubMediaFileService();
            media.Files.Add(missing);
            var disk = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var sut = new MediaFileTableCleanupService(media, disk, LogManager.GetCurrentClassLogger());

            sut.Clean("/books", new List<string> { "/books/present.mp3" }, "audiobook");

            Assert.That(media.Deleted, Is.EqualTo(new[] { missing }));
            Assert.That(media.DeleteReason, Is.EqualTo(DeleteMediaFileReason.MissingFromDisk));
        }

        [Test]
        public void clean_should_preserve_row_absent_from_scan_when_disk_recheck_finds_file()
        {
            var temporarilyUnseen = new BookFile
            {
                Id = 1,
                Path = "/books/temporarily-unseen.mp3",
                EditionId = 42,
                MediaType = "audiobook"
            };
            var media = new StubMediaFileService();
            media.Files.Add(temporarilyUnseen);
            var disk = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            ((DiskProviderProxy)(object)disk).ExistingPaths.Add(temporarilyUnseen.Path);
            var sut = new MediaFileTableCleanupService(media, disk, LogManager.GetCurrentClassLogger());

            sut.Clean("/books", new List<string> { "/books/present.mp3" }, "audiobook");

            Assert.That(media.Deleted, Is.Empty);
            Assert.That(media.DeleteReason, Is.Null);
        }

        [Test]
        public void clean_should_remove_stale_row_when_only_a_loose_path_match_exists()
        {
            var stale = new BookFile
            {
                Id = 1,
                Path = "/books/Philosopher’s Stone.m4b",
                EditionId = 42,
                MediaType = "audiobook"
            };
            var media = new StubMediaFileService();
            media.Files.Add(stale);
            var disk = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            ((DiskProviderProxy)(object)disk).LooseOnlyPaths.Add(stale.Path);
            var sut = new MediaFileTableCleanupService(media, disk, LogManager.GetCurrentClassLogger());

            sut.Clean("/books", new List<string> { "/books/Philosopher's Stone.m4b" }, "audiobook");

            Assert.That(media.Deleted, Is.EqualTo(new[] { stale }));
            Assert.That(media.DeleteReason, Is.EqualTo(DeleteMediaFileReason.MissingFromDisk));
        }
    }
}
