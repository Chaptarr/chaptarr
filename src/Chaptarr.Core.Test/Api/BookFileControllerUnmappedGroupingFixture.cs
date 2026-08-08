using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Chaptarr.Api.V1.BookFiles;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class BookFileControllerUnmappedGroupingFixture
    {
        private class MediaFileServiceProxy : DispatchProxy
        {
            public List<BookFile> Files { get; set; } = new List<BookFile>();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IMediaFileService.GetUnmappedFiles))
                {
                    return Files;
                }

                throw new NotImplementedException($"IMediaFileService.{targetMethod?.Name}");
            }
        }

        private class RootFolderServiceProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IRootFolderService.GetBestRootFolderPath))
                {
                    return "/audiobooks";
                }

                throw new NotImplementedException($"IRootFolderService.{targetMethod?.Name}");
            }
        }

        [Test]
        public void unmapped_api_should_report_three_units_for_two_ebooks_and_one_audiobook()
        {
            var mediaFileService = DispatchProxy.Create<IMediaFileService, MediaFileServiceProxy>();
            ((MediaFileServiceProxy)(object)mediaFileService).Files = new List<BookFile>
            {
                CreateFile(1, "/audiobooks/audiobooks/audiobooks/Freida McFadden/Freida McFadden - The Housemaid Is Watching.epub", "ebook"),
                CreateFile(2, "/audiobooks/audiobooks/audiobooks/Freida McFadden/Freida McFadden - The Boyfriend - A Psychological Thriller.epub", "ebook"),
                CreateFile(3, "/audiobooks/audiobooks/Jim Murphy/Inner Excellence - Julian Mehne/Inner Excellence.m4b", "audiobook")
            };
            var rootFolderService = DispatchProxy.Create<IRootFolderService, RootFolderServiceProxy>();
            var controller = new BookFileController(
                null,
                mediaFileService,
                null,
                null,
                null,
                null,
                null,
                rootFolderService,
                LogManager.GetCurrentClassLogger());

            var resources = controller.GetBookFiles(
                null,
                new List<int>(),
                new List<int>(),
                unmapped: true);

            Assert.That(resources, Has.Count.EqualTo(3));
            Assert.That(resources.Select(resource => resource.ImportUnitKey).Distinct().Count(), Is.EqualTo(3));
            Assert.That(resources.All(resource => !string.IsNullOrWhiteSpace(resource.ImportUnitKey)), Is.True);
            Assert.That(resources.All(resource => !string.IsNullOrWhiteSpace(resource.ImportUnitRoot)), Is.True);
        }

        private static BookFile CreateFile(int id, string path, string mediaType)
        {
            return new BookFile
            {
                Id = id,
                EditionId = 0,
                Path = path,
                MediaType = mediaType,
                AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            };
        }
    }
}
