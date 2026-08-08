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
    public class TagExclusionPolicyFixture
    {
        private sealed class NullAudioTagService : IAudioTagService
        {
            public Dictionary<string, List<string>> ReadAllTags(string file) => throw new NotImplementedException();
            public (Dictionary<string, List<string>> Tags, int? DurationSeconds) ReadAllTagsAndDuration(string file) => throw new NotImplementedException();
            public void WriteTags(BookFile trackfile, bool newDownload, bool force = false) => throw new NotImplementedException();
            public void SyncTags(List<Edition> tracks) => throw new NotImplementedException();
            public List<RetagBookFilePreview> GetRetagPreviewsByAuthor(int authorId) => throw new NotImplementedException();
            public List<RetagBookFilePreview> GetRetagPreviewsByBook(int bookId) => throw new NotImplementedException();
            public void RetagFiles(RetagFilesCommand message) => throw new NotImplementedException();
            public void RetagAuthor(RetagAuthorCommand message) => throw new NotImplementedException();
        }

        private sealed class StaticEBookTagService : IEBookTagService
        {
            private readonly Dictionary<string, List<string>> _tags;

            public StaticEBookTagService(Dictionary<string, List<string>> tags)
            {
                _tags = tags;
            }

            public Dictionary<string, List<string>> ReadAllTags(IFileInfo file) => new Dictionary<string, List<string>>(_tags, StringComparer.OrdinalIgnoreCase);
            public void WriteTags(BookFile trackfile, bool newDownload, bool force = false) => throw new NotImplementedException();
            public void SyncTags(List<Edition> books) => throw new NotImplementedException();
            public List<RetagBookFilePreview> GetRetagPreviewsByAuthor(int authorId) => throw new NotImplementedException();
            public List<RetagBookFilePreview> GetRetagPreviewsByBook(int bookId) => throw new NotImplementedException();
            public void RetagFiles(RetagFilesCommand message) => throw new NotImplementedException();
            public void RetagAuthor(RetagAuthorCommand message) => throw new NotImplementedException();
        }

        [Test]
        public void is_extraction_noise_key_should_match_current_cleanup_rules_only()
        {
            Assert.That(TagExclusionPolicy.IsExtractionNoiseKey("ENCODEDBY"), Is.True);
            Assert.That(TagExclusionPolicy.IsExtractionNoiseKey("__custom"), Is.True);
            Assert.That(TagExclusionPolicy.IsExtractionNoiseKey("genre"), Is.False);
            Assert.That(TagExclusionPolicy.IsExtractionNoiseKey("title"), Is.False);
        }

        [Test]
        public void is_excluded_from_matching_should_cover_common_trash_fields()
        {
            Assert.That(TagExclusionPolicy.IsExcludedFromMatching("genre"), Is.True);
            Assert.That(TagExclusionPolicy.IsExcludedFromMatching("comment"), Is.True);
            Assert.That(TagExclusionPolicy.IsExcludedFromMatching("description"), Is.True);
            Assert.That(TagExclusionPolicy.IsExcludedFromMatching("filename"), Is.True);
            Assert.That(TagExclusionPolicy.IsExcludedFromMatching("ID3v2:TXXX:REPLAYGAIN_TRACK_GAIN"), Is.True);
            Assert.That(TagExclusionPolicy.IsExcludedFromMatching("ID3v2:TXXX:ENCODEDBY"), Is.True);
            Assert.That(TagExclusionPolicy.IsExcludedFromMatching("©cpy"), Is.True);
            Assert.That(TagExclusionPolicy.IsExcludedFromMatching("MP4:©cpy"), Is.True);
            Assert.That(TagExclusionPolicy.IsExcludedFromMatching("TCOP"), Is.True);
            Assert.That(TagExclusionPolicy.IsExcludedFromMatching("ID3v2:TCOP"), Is.True);
            Assert.That(TagExclusionPolicy.IsExcludedFromMatching("cprt"), Is.True);
            Assert.That(TagExclusionPolicy.IsExcludedFromMatching("MP4:cprt"), Is.True);
            Assert.That(TagExclusionPolicy.IsExcludedFromMatching("copyright"), Is.True);
            Assert.That(TagExclusionPolicy.IsExcludedFromMatching("XIPH:COPYRIGHT"), Is.True);
            Assert.That(TagExclusionPolicy.IsExcludedFromMatching("rights"), Is.True);
            Assert.That(TagExclusionPolicy.IsExcludedFromMatching("title"), Is.False);
            Assert.That(TagExclusionPolicy.IsExcludedFromMatching("artist"), Is.False);
        }

        [Test]
        public void metadata_tag_service_should_only_strip_current_extraction_noise()
        {
            var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"tag_policy_{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);

            var path = Path.Combine(root, "sample.pdf");
            File.WriteAllText(path, "content");

            try
            {
                var fileSystem = new FileSystem();
                var ebookTagService = new StaticEBookTagService(new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "TITLE", new List<string> { "Example Title" } },
                    { "ENCODEDBY", new List<string> { "Tool" } },
                    { "__INTERNAL", new List<string> { "Synthetic" } },
                    { "GENRE", new List<string> { "Fiction" } },
                    { "COPYRIGHT", new List<string> { "© 2026 Example Rights Holder" } }
                });

                var sut = new MetadataTagService(new NullAudioTagService(), ebookTagService, LogManager.GetLogger("test"));
                var tags = sut.ReadAllTags(fileSystem.FileInfo.FromFileName(path));

                Assert.That(tags.ContainsKey("TITLE"), Is.True);
                Assert.That(tags.ContainsKey("GENRE"), Is.True);
                Assert.That(tags.ContainsKey("COPYRIGHT"), Is.True);
                Assert.That(tags.ContainsKey("ENCODEDBY"), Is.False);
                Assert.That(tags.ContainsKey("__INTERNAL"), Is.False);
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
