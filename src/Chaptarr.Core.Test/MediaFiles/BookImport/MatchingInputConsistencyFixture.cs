using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MetadataSource.BookInfo;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class MatchingInputConsistencyFixture
    {
        [TestCase("genre")]
        [TestCase("GENRE")]
        [TestCase("comment")]
        [TestCase("ID3v2:COMM:Description")]
        [TestCase("pathcomponents")]
        [TestCase("filename")]
        [TestCase("ENCODEDBY")]
        [TestCase("REPLAYGAIN_TRACK_GAIN")]
        [TestCase("MP4:©cpy")]
        [TestCase("ID3v2:TCOP")]
        [TestCase("XIPH:COPYRIGHT")]
        [TestCase("rights")]
        public void is_excluded_from_matching_should_reject_known_trash_keys(string key)
        {
            Assert.That(FileMatchingService.IsExcludedFromMatching(key), Is.True);
        }

        [TestCase("title")]
        [TestCase("artist")]
        [TestCase("album")]
        [TestCase("narrator")]
        public void is_excluded_from_matching_should_keep_identity_candidate_keys(string key)
        {
            Assert.That(FileMatchingService.IsExcludedFromMatching(key), Is.False);
        }

        [Test]
        public void canonical_embedded_query_builder_should_exclude_trash_and_dedupe_values()
        {
            var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                { "TITLE", new List<string> { "Dune" } },
                { "ARTIST", new List<string> { "Frank Herbert" } },
                { "ALBUM", new List<string> { "Dune" } },
                { "GENRE", new List<string> { "Science Fiction" } },
                { "COMMENT", new List<string> { "legacy plot summary" } },
                { "MP4:©cpy", new List<string> { "© 1990 George R. R. Martin and the Wild Card Trust" } },
                { "pathcomponents", new List<string> { "audiobooks", "Frank Herbert", "Dune" } },
                { "filename", new List<string> { "Dune.m4b" } },
                { "ENCODEDBY", new List<string> { "qaac" } }
            };

            var expected = "dune frank herbert";
            var canonicalQuery = CanonicalMatchInputBuilder.BuildEmbeddedQuery(tags);

            Assert.That(canonicalQuery, Is.EqualTo(expected));
        }

        [Test]
        public void v5_request_builder_should_exclude_trash_and_path_keys_and_add_filename_hint_only()
        {
            var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "TITLE", "Dune" },
                { "ARTIST", "Frank Herbert" },
                { "ALBUM", "Dune" },
                { "GENRE", "Science Fiction" },
                { "COMMENT", "legacy plot summary" },
                { "ID3v2:TCOP", "© 1990 George R. R. Martin and the Wild Card Trust" },
                { "path", "/audiobooks/Frank Herbert/Dune" },
                { "folder", "Frank Herbert" },
                { "filename", "Dune.m4b" },
                { "ENCODEDBY", "qaac" }
            };

            var request = InvokePrivateStaticV5Builder(tags, "/audiobooks/Frank Herbert/Dune.m4b");

            Assert.That(request.Keys, Is.EquivalentTo(new[] { "TITLE", "ARTIST", "ALBUM", "file_name" }));
            Assert.That(request["file_name"], Is.EqualTo(new[] { "Dune.m4b" }));
            Assert.That(request.ContainsKey("GENRE"), Is.False);
            Assert.That(request.ContainsKey("COMMENT"), Is.False);
            Assert.That(request.ContainsKey("ID3v2:TCOP"), Is.False);
            Assert.That(request.ContainsKey("path"), Is.False);
            Assert.That(request.ContainsKey("folder"), Is.False);
            Assert.That(request.ContainsKey("filename"), Is.False);
            Assert.That(request.ContainsKey("ENCODEDBY"), Is.False);
        }

        private static Dictionary<string, List<string>> InvokePrivateStaticV5Builder(Dictionary<string, string> tags, string filePath)
        {
            var logicalTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var tag in tags)
            {
                logicalTags[tag.Key] = new List<string> { tag.Value };
            }

            var method = typeof(V5MatchingService).GetMethod("BuildV5RequestTags", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null, "Unable to locate BuildV5RequestTags on V5MatchingService");

            return (Dictionary<string, List<string>>)method.Invoke(null, new object[] { logicalTags, filePath });
        }
    }
}
