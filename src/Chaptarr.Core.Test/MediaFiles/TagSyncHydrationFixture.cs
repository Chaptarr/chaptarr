using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.TagExtraction;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.RootFolders;
using NUnit.Framework;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class TagSyncHydrationFixture
    {
        private class ThrowingProxy<T> : DispatchProxy where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"Unexpected call to {typeof(T).Name}.{targetMethod?.Name}");
            }
        }

        private class TagConfigServiceProxy : DispatchProxy
        {
            public WriteAudioTagsType WriteAudioTags { get; set; } = WriteAudioTagsType.No;
            public WriteBookTagsType WriteBookTags { get; set; } = WriteBookTagsType.NewFiles;

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    "get_WriteAudioTags" => WriteAudioTags,
                    "get_WriteBookTags" => WriteBookTags,
                    _ => throw new NotImplementedException($"Unexpected call to IConfigService.{targetMethod?.Name}")
                };
            }
        }

        private class MediaFileServiceProxy : DispatchProxy
        {
            public List<int> RequestedBookIds { get; private set; } = new List<int>();
            public List<BookFile> FilesByBooks { get; set; } = new List<BookFile>();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == "GetFilesByBooks")
                {
                    RequestedBookIds = ((List<int>)args[0]).ToList();
                    return FilesByBooks;
                }

                    throw new NotImplementedException($"Unexpected call to IMediaFileService.{targetMethod?.Name}");
            }
        }

        [Test]
        public void ebook_sync_tags_should_hydrate_missing_bookfiles()
        {
            var config = DispatchProxy.Create<IConfigService, TagConfigServiceProxy>();
            ((TagConfigServiceProxy)config).WriteBookTags = WriteBookTagsType.Sync;

            var mediaFileService = DispatchProxy.Create<IMediaFileService, MediaFileServiceProxy>();
            // Deliberately a format Chaptarr cannot write tags into, so this stays a test about
            // hydrating missing book files rather than about the tag writing that follows it.
            ((MediaFileServiceProxy)mediaFileService).FilesByBooks = new List<BookFile>
            {
                new BookFile { EditionId = 42, CalibreId = 0, Path = "/books/test.pdf" }
            };

            var sut = new EBookTagService(
                DispatchProxy.Create<IAuthorService, ThrowingProxy<IAuthorService>>(),
                mediaFileService,
                DispatchProxy.Create<IRootFolderService, ThrowingProxy<IRootFolderService>>(),
                config,
                DispatchProxy.Create<ICalibreProxy, ThrowingProxy<ICalibreProxy>>(),
                DispatchProxy.Create<IFileMutationSafetyService, ThrowingProxy<IFileMutationSafetyService>>(),
                DispatchProxy.Create<IEpubSeriesTagWriter, ThrowingProxy<IEpubSeriesTagWriter>>(),
                LogManager.GetLogger("test"));

            var edition = new Edition { Id = 42, BookId = 7 };

            Assert.DoesNotThrow(() => sut.SyncTags(new List<Edition> { edition }));
            Assert.That(((MediaFileServiceProxy)mediaFileService).RequestedBookIds, Is.EqualTo(new[] { 7 }));
            Assert.That(edition.BookFiles, Has.Count.EqualTo(1));
        }

        [Test]
        public void audio_sync_tags_should_hydrate_missing_bookfiles()
        {
            var config = DispatchProxy.Create<IConfigService, TagConfigServiceProxy>();
            ((TagConfigServiceProxy)config).WriteAudioTags = WriteAudioTagsType.Sync;

            var mediaFileService = DispatchProxy.Create<IMediaFileService, MediaFileServiceProxy>();
            ((MediaFileServiceProxy)mediaFileService).FilesByBooks = new List<BookFile>
            {
                new BookFile { EditionId = 84, Path = "/books/test.txt" }
            };

            var sut = new AudioTagService(
                config,
                mediaFileService,
                DispatchProxy.Create<IDiskProvider, ThrowingProxy<IDiskProvider>>(),
                DispatchProxy.Create<IRootFolderWatchingService, ThrowingProxy<IRootFolderWatchingService>>(),
                DispatchProxy.Create<IFileMutationSafetyService, ThrowingProxy<IFileMutationSafetyService>>(),
                DispatchProxy.Create<IAuthorService, ThrowingProxy<IAuthorService>>(),
                DispatchProxy.Create<IMapCoversToLocal, ThrowingProxy<IMapCoversToLocal>>(),
                DispatchProxy.Create<IEventAggregator, ThrowingProxy<IEventAggregator>>(),
                DispatchProxy.Create<IMediaInfoExtractor, ThrowingProxy<IMediaInfoExtractor>>(),
                LogManager.GetLogger("test"),
                DispatchProxy.Create<ITagExtractionService, ThrowingProxy<ITagExtractionService>>());

            var edition = new Edition { Id = 84, BookId = 9 };

            Assert.DoesNotThrow(() => sut.SyncTags(new List<Edition> { edition }));
            Assert.That(((MediaFileServiceProxy)mediaFileService).RequestedBookIds, Is.EqualTo(new[] { 9 }));
            Assert.That(edition.BookFiles, Has.Count.EqualTo(1));
        }
    }
}
