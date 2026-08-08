using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Commands;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class MetadataTagServiceCleanupFixture
    {
        private sealed class StubAudioTagService : IAudioTagService
        {
            public Dictionary<string, List<string>> TagsToReturn { get; set; }

            public Dictionary<string, List<string>> ReadAllTags(string file) => TagsToReturn;

            public (Dictionary<string, List<string>> Tags, int? DurationSeconds) ReadAllTagsAndDuration(string file)
            {
                return (TagsToReturn, 123);
            }

            public void WriteTags(BookFile trackfile, bool newDownload, bool force = false) => throw new NotImplementedException();
            public void SyncTags(List<Edition> tracks) => throw new NotImplementedException();
            public List<RetagBookFilePreview> GetRetagPreviewsByAuthor(int authorId) => throw new NotImplementedException();
            public List<RetagBookFilePreview> GetRetagPreviewsByBook(int bookId) => throw new NotImplementedException();
            public void RetagFiles(RetagFilesCommand message) => throw new NotImplementedException();
            public void RetagAuthor(RetagAuthorCommand message) => throw new NotImplementedException();
        }

        private sealed class StubEBookTagService : IEBookTagService
        {
            public Dictionary<string, List<string>> ReadAllTags(IFileInfo file) => throw new NotImplementedException();
            public void WriteTags(BookFile trackfile, bool newDownload, bool force = false) => throw new NotImplementedException();
            public void SyncTags(List<Edition> books) => throw new NotImplementedException();
            public List<RetagBookFilePreview> GetRetagPreviewsByAuthor(int authorId) => throw new NotImplementedException();
            public List<RetagBookFilePreview> GetRetagPreviewsByBook(int bookId) => throw new NotImplementedException();
            public void RetagFiles(RetagFilesCommand message) => throw new NotImplementedException();
            public void RetagAuthor(RetagAuthorCommand message) => throw new NotImplementedException();
        }

        [Test]
        public void readalltags_should_strip_encoder_noise_but_keep_user_visible_metadata()
        {
            var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"metatag_cleanup_{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);

            var path = Path.Combine(root, "sample.mp3");
            File.WriteAllText(path, "audio");

            try
            {
                var audioTagService = new StubAudioTagService
                {
                    TagsToReturn = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        { "TITLE", new List<string> { "Dune" } },
                        { "COMMENT", new List<string> { "legacy plot summary" } },
                        { "ENCODEDBY", new List<string> { "qaac" } },
                        { "__hidden", new List<string> { "internal" } }
                    }
                };

                var ebookTagService = new StubEBookTagService();
                var sut = new MetadataTagService(audioTagService, ebookTagService, LogManager.GetLogger("test"));
                var tags = sut.ReadAllTags(new FileSystem().FileInfo.FromFileName(path));

                Assert.That(tags.ContainsKey("TITLE"), Is.True);
                Assert.That(tags.ContainsKey("COMMENT"), Is.True);
                Assert.That(tags.ContainsKey("ENCODEDBY"), Is.False);
                Assert.That(tags.ContainsKey("__hidden"), Is.False);
            }
            finally
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch
                {
                }
            }
        }
    }
}
