using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.RootFolders;
using NUnit.Framework;

namespace Chaptarr.Core.Test.MediaFiles
{
    /// <summary>
    /// Covers writing series metadata straight into the book file, which is the only path
    /// available to the majority of users who do not run a Calibre content server.
    /// </summary>
    [TestFixture]
    public class EbookTagServiceDirectWriteFixture
    {
        private class ThrowingProxy<T> : DispatchProxy
            where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"Unexpected call to {typeof(T).Name}.{targetMethod?.Name}");
            }
        }

        private class ConfigServiceProxy : DispatchProxy
        {
            public WriteBookTagsType WriteBookTags { get; set; } = WriteBookTagsType.NewFiles;

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    "get_WriteBookTags" => WriteBookTags,
                    "get_UpdateCovers" => false,
                    "get_EmbedMetadata" => false,
                    _ => throw new NotImplementedException($"Unexpected call to IConfigService.{targetMethod?.Name}")
                };
            }
        }

        private class RootFolderServiceProxy : DispatchProxy
        {
            public RootFolder RootFolder { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name == "GetBestRootFolder"
                    ? RootFolder
                    : throw new NotImplementedException($"Unexpected call to IRootFolderService.{targetMethod?.Name}");
            }
        }

        private class FileMutationSafetyProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name == "EnsureMutableFile"
                    ? null
                    : throw new NotImplementedException($"Unexpected call to IFileMutationSafetyService.{targetMethod?.Name}");
            }
        }

        private class RecordingSeriesTagWriter : IEpubSeriesTagWriter
        {
            public List<(string Path, string SeriesName, double? SeriesIndex)> Calls { get; } = new();

            public string ThrowForPath { get; set; }

            public bool WriteSeriesTags(string epubPath, string seriesName, double? seriesIndex)
            {
                Calls.Add((epubPath, seriesName, seriesIndex));

                if (epubPath == ThrowForPath)
                {
                    throw new IOException($"Simulated failure writing {epubPath}");
                }

                return true;
            }
        }

        private class AuthorServiceProxy : DispatchProxy
        {
            public List<Author> Authors { get; set; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name == "GetAuthors"
                    ? Authors
                    : throw new NotImplementedException($"Unexpected call to IAuthorService.{targetMethod?.Name}");
            }
        }

        private class MediaFileServiceProxy : DispatchProxy
        {
            public List<BookFile> Files { get; set; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name is "GetFilesByAuthor" or "GetFilesByBooks"
                    ? Files
                    : throw new NotImplementedException($"Unexpected call to IMediaFileService.{targetMethod?.Name}");
            }
        }

        private RecordingSeriesTagWriter _writer;
        private object _authorService;
        private object _mediaFileService;

        [SetUp]
        public void SetUp()
        {
            _writer = new RecordingSeriesTagWriter();
            _authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            _mediaFileService = DispatchProxy.Create<IMediaFileService, MediaFileServiceProxy>();
        }

        private EBookTagService BuildSubject(bool isCalibreLibrary, WriteBookTagsType writeBookTags = WriteBookTagsType.NewFiles)
        {
            var rootFolderService = DispatchProxy.Create<IRootFolderService, RootFolderServiceProxy>();
            ((RootFolderServiceProxy)rootFolderService).RootFolder = new RootFolder
            {
                Path = "/books",
                IsCalibreLibrary = isCalibreLibrary
            };

            var configService = DispatchProxy.Create<IConfigService, ConfigServiceProxy>();
            ((ConfigServiceProxy)configService).WriteBookTags = writeBookTags;

            return new EBookTagService(
                (IAuthorService)_authorService,
                (IMediaFileService)_mediaFileService,
                rootFolderService,
                configService,
                DispatchProxy.Create<ICalibreProxy, ThrowingProxy<ICalibreProxy>>(),
                DispatchProxy.Create<IFileMutationSafetyService, FileMutationSafetyProxy>(),
                _writer,
                LogManager.GetLogger("test"));
        }

        private static BookFile GivenBookFile(string seriesName, string seriesPosition, string path = "/books/example.epub")
        {
            return new BookFile
            {
                Path = path,
                Edition = new Edition
                {
                    Title = "An Example Book",
                    Book = new Book
                    {
                        SeriesName = seriesName,
                        SeriesPosition = seriesPosition
                    }
                }
            };
        }

        [Test]
        public void should_write_series_tags_directly_when_root_folder_is_not_a_calibre_library()
        {
            var sut = BuildSubject(isCalibreLibrary: false);

            // The Calibre proxy throws on any call, so reaching Calibre would fail this test.
            sut.WriteTags(GivenBookFile("Example Series", "3"), newDownload: true);

            Assert.That(_writer.Calls, Has.Count.EqualTo(1));
            Assert.That(_writer.Calls[0].Path, Is.EqualTo("/books/example.epub"));
            Assert.That(_writer.Calls[0].SeriesName, Is.EqualTo("Example Series"));
            Assert.That(_writer.Calls[0].SeriesIndex, Is.EqualTo(3));
        }

        [Test]
        public void should_pass_no_index_when_series_position_is_not_numeric()
        {
            var sut = BuildSubject(isCalibreLibrary: false);

            sut.WriteTags(GivenBookFile("Example Series", "2 - Heavy Metal"), newDownload: true);

            Assert.That(_writer.Calls, Has.Count.EqualTo(1));
            Assert.That(_writer.Calls[0].SeriesName, Is.EqualTo("Example Series"));
            Assert.That(_writer.Calls[0].SeriesIndex, Is.Null);
        }

        [Test]
        public void should_parse_fractional_series_position()
        {
            var sut = BuildSubject(isCalibreLibrary: false);

            sut.WriteTags(GivenBookFile("Example Series", "11.5"), newDownload: true);

            Assert.That(_writer.Calls[0].SeriesIndex, Is.EqualTo(11.5));
        }

        [Test]
        public void should_not_write_anything_when_book_has_no_series()
        {
            var sut = BuildSubject(isCalibreLibrary: false);

            sut.WriteTags(GivenBookFile(null, null), newDownload: true);

            Assert.That(_writer.Calls, Is.Empty);
        }

        [Test]
        public void should_not_write_series_tags_for_formats_with_no_writer()
        {
            var sut = BuildSubject(isCalibreLibrary: false);

            sut.WriteTags(GivenBookFile("Example Series", "3", "/books/example.pdf"), newDownload: true);

            Assert.That(_writer.Calls, Is.Empty);
        }

        [Test]
        public void should_not_write_directly_when_config_limits_tagging_to_new_files()
        {
            var sut = BuildSubject(isCalibreLibrary: false);

            sut.WriteTags(GivenBookFile("Example Series", "3"), newDownload: false);

            Assert.That(_writer.Calls, Is.Empty);
        }

        [Test]
        public void should_retag_epubs_without_a_calibre_id_when_backfilling_an_author()
        {
            var sut = BuildSubject(isCalibreLibrary: false);

            ((AuthorServiceProxy)_authorService).Authors = new List<Author> { new Author { Id = 1, Name = "An Example Author" } };
            ((MediaFileServiceProxy)_mediaFileService).Files = new List<BookFile> { GivenBookFile("Example Series", "3") };

            sut.RetagAuthor(new RetagAuthorCommand { AuthorIds = new List<int> { 1 } });

            Assert.That(_writer.Calls, Has.Count.EqualTo(1));
            Assert.That(_writer.Calls[0].SeriesName, Is.EqualTo("Example Series"));
        }

        [Test]
        public void should_keep_backfilling_the_rest_of_an_author_when_one_file_cannot_be_written()
        {
            var sut = BuildSubject(isCalibreLibrary: false);

            _writer.ThrowForPath = "/books/broken.epub";

            ((AuthorServiceProxy)_authorService).Authors = new List<Author> { new Author { Id = 1, Name = "An Example Author" } };
            ((MediaFileServiceProxy)_mediaFileService).Files = new List<BookFile>
            {
                GivenBookFile("Example Series", "1", "/books/broken.epub"),
                GivenBookFile("Example Series", "2", "/books/intact.epub")
            };

            Assert.DoesNotThrow(() => sut.RetagAuthor(new RetagAuthorCommand { AuthorIds = new List<int> { 1 } }));

            // One unreadable book must not strand the rest of the library untagged.
            Assert.That(_writer.Calls.Select(x => x.Path),
                Is.EqualTo(new[] { "/books/broken.epub", "/books/intact.epub" }));
        }

        [Test]
        public void should_sync_epubs_without_a_calibre_id()
        {
            var sut = BuildSubject(isCalibreLibrary: false, writeBookTags: WriteBookTagsType.Sync);

            var edition = new Edition
            {
                Id = 1,
                BookId = 1,
                Title = "An Example Book",
                Book = new Book { SeriesName = "Example Series", SeriesPosition = "3" },
                BookFiles = new List<BookFile> { GivenBookFile("Example Series", "3") }
            };

            sut.SyncTags(new List<Edition> { edition });

            Assert.That(_writer.Calls, Has.Count.EqualTo(1));
        }
    }
}
