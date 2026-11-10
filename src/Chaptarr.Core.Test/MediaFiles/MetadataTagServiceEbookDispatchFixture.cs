using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Reflection;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Commands;
using NUnit.Framework;

namespace Chaptarr.Core.Test.MediaFiles
{
    /// <summary>
    /// Ebook tag writing used to be reachable only for files that carried a Calibre id, which
    /// meant the writeBookTags setting did nothing at all unless the user ran a Calibre content
    /// server. These cover which files now reach the ebook tag service.
    /// </summary>
    [TestFixture]
    public class MetadataTagServiceEbookDispatchFixture
    {
        private class ThrowingProxy<T> : DispatchProxy
            where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"Unexpected call to {typeof(T).Name}.{targetMethod?.Name}");
            }
        }

        private sealed class RecordingEBookTagService : IEBookTagService
        {
            public List<BookFile> WrittenFiles { get; } = new();

            public void WriteTags(BookFile trackfile, bool newDownload, bool force = false) => WrittenFiles.Add(trackfile);

            public Dictionary<string, List<string>> ReadAllTags(IFileInfo file) => throw new NotImplementedException();
            public void SyncTags(List<Edition> books) => throw new NotImplementedException();
            public List<RetagBookFilePreview> GetRetagPreviewsByAuthor(int authorId) => throw new NotImplementedException();
            public List<RetagBookFilePreview> GetRetagPreviewsByBook(int bookId) => throw new NotImplementedException();
            public void RetagFiles(RetagFilesCommand message) => throw new NotImplementedException();
            public void RetagAuthor(RetagAuthorCommand message) => throw new NotImplementedException();
        }

        private RecordingEBookTagService _ebookTagService;

        [SetUp]
        public void SetUp()
        {
            _ebookTagService = new RecordingEBookTagService();
        }

        private MetadataTagService BuildSubject()
        {
            return new MetadataTagService(
                DispatchProxy.Create<IAudioTagService, ThrowingProxy<IAudioTagService>>(),
                _ebookTagService,
                LogManager.GetLogger("test"));
        }

        [Test]
        public void should_write_tags_for_an_epub_that_has_no_calibre_id()
        {
            BuildSubject().WriteTags(new BookFile { Path = "/books/example.epub", CalibreId = 0 }, newDownload: true);

            Assert.That(_ebookTagService.WrittenFiles, Has.Count.EqualTo(1));
        }

        [Test]
        public void should_still_write_tags_for_a_calibre_managed_file_of_any_format()
        {
            BuildSubject().WriteTags(new BookFile { Path = "/books/example.pdf", CalibreId = 7 }, newDownload: true);

            Assert.That(_ebookTagService.WrittenFiles, Has.Count.EqualTo(1));
        }

        [Test]
        public void should_not_write_tags_for_a_format_with_no_writer_and_no_calibre_id()
        {
            BuildSubject().WriteTags(new BookFile { Path = "/books/example.pdf", CalibreId = 0 }, newDownload: true);

            Assert.That(_ebookTagService.WrittenFiles, Is.Empty);
        }
    }
}
