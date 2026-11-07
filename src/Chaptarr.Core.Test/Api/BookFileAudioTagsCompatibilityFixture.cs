using System.Collections.Generic;
using System.Reflection;
using Chaptarr.Api.V1.BookFiles;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class BookFileAudioTagsCompatibilityFixture
    {
        private class MediaFileServiceProxy : DispatchProxy
        {
            public BookFile File { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IMediaFileService.Get) && targetMethod.ReturnType == typeof(BookFile))
                {
                    return File;
                }

                throw new System.NotImplementedException($"IMediaFileService.{targetMethod?.Name}");
            }
        }

        private sealed class TestBookFileController : BookFileController
        {
            public TestBookFileController(IMediaFileService mediaFileService)
                : base(null, mediaFileService, null, null, null, null, null, null, LogManager.GetCurrentClassLogger())
            {
            }

            public BookFileResource GetResource(int id)
            {
                return base.GetResourceById(id);
            }
        }

        [Test]
        public void get_by_id_should_keep_the_legacy_audio_tags_shape_and_values()
        {
            var file = new BookFile
            {
                Id = 7,
                Path = "/books/Author/Book.m4b",
                Quality = new QualityModel { Quality = Quality.MP3 },
                MediaInfo = new MediaInfoModel { Duration = System.TimeSpan.FromMinutes(42) },
                AllTags = new Dictionary<string, List<string>>
                {
                    ["TITLE"] = new List<string> { "The Book" },
                    ["ALBUM"] = new List<string> { "The Book" },
                    ["ALBUMARTIST"] = new List<string> { "Author Name" },
                    ["NARRATOR"] = new List<string> { "Narrator Name" },
                    ["ISBN"] = new List<string> { "9781234567890" },
                    ["DISCNUMBER"] = new List<string> { "2" },
                    ["TOTALDISCS"] = new List<string> { "3" },
                    ["TRACKNUMBER"] = new List<string> { "4/12" },
                    ["DATE"] = new List<string> { "2022-06-01" }
                }
            };
            var mediaFileService = DispatchProxy.Create<IMediaFileService, MediaFileServiceProxy>();
            ((MediaFileServiceProxy)(object)mediaFileService).File = file;
            var controller = new TestBookFileController(mediaFileService);

            var resource = controller.GetResource(file.Id);

            Assert.That(resource.AudioTags, Is.Not.Null);
            Assert.That(resource.AudioTags.Title, Is.EqualTo("The Book"));
            Assert.That(resource.AudioTags.BookTitle, Is.EqualTo("The Book"));
            Assert.That(resource.AudioTags.Authors, Is.EqualTo(new[] { "Author Name" }));
            Assert.That(resource.AudioTags.AuthorTitle, Is.EqualTo("Author Name"));
            Assert.That(resource.AudioTags.Narrator, Is.EqualTo("Narrator Name"));
            Assert.That(resource.AudioTags.Isbn, Is.EqualTo("9781234567890"));
            Assert.That(resource.AudioTags.DiscNumber, Is.EqualTo(2));
            Assert.That(resource.AudioTags.DiscCount, Is.EqualTo(3));
            Assert.That(resource.AudioTags.TrackNumbers, Is.EqualTo(new[] { 4 }));
            Assert.That(resource.AudioTags.Year, Is.EqualTo(2022));
            Assert.That(resource.AudioTags.Duration, Is.EqualTo(System.TimeSpan.FromMinutes(42)));
        }
    }
}
