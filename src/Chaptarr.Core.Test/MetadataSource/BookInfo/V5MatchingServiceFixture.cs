using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using NzbDrone.Core.MetadataSource.BookInfo;

namespace Chaptarr.Core.Test.MetadataSource.BookInfo
{
    [TestFixture]
    public class V5MatchingServiceFixture
    {
        [Test]
        public void build_effective_query_should_fall_back_to_embedded_tags_when_query_is_empty()
        {
            var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["TITLE"] = new List<string> { "Dune" },
                ["ARTIST"] = new List<string> { "Frank Herbert" },
                ["GENRE"] = new List<string> { "Science Fiction" }
            };

            var effective = InvokePrivateStaticStringMethod("BuildEffectiveQuery", string.Empty, tags);

            Assert.That(effective, Is.EqualTo("dune frank herbert"));
        }

        [Test]
        public void build_v5_request_tags_should_filter_trash_and_preserve_file_name_hint()
        {
            var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["TITLE"] = "Dune",
                ["ARTIST"] = "Frank Herbert",
                ["GENRE"] = "Science Fiction",
                ["COMMENT"] = "legacy plot summary",
                ["filename"] = "Dune.m4b",
                ["path"] = "/audiobooks/Frank Herbert/Dune.m4b"
            };

            var result = InvokePrivateStaticTagMapMethod(tags, "/audiobooks/Frank Herbert/Dune.m4b");

            Assert.That(result.Keys, Is.EquivalentTo(new[] { "TITLE", "ARTIST", "file_name" }));
            Assert.That(result["file_name"], Is.EqualTo(new[] { "Dune.m4b" }));
            Assert.That(result.ContainsKey("GENRE"), Is.False);
            Assert.That(result.ContainsKey("COMMENT"), Is.False);
            Assert.That(result.ContainsKey("filename"), Is.False);
            Assert.That(result.ContainsKey("path"), Is.False);
        }

        [Test]
        public void build_v5_request_tags_should_be_deterministic_across_insertion_order()
        {
            var forward = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["TITLE"] = "Dune",
                ["ARTIST"] = "Frank Herbert",
                ["ALBUM"] = "Dune"
            };
            var reverse = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ALBUM"] = "Dune",
                ["ARTIST"] = "Frank Herbert",
                ["TITLE"] = "Dune"
            };

            var first = InvokePrivateStaticTagMapMethod(forward, null);
            var second = InvokePrivateStaticTagMapMethod(reverse, null);

            Assert.That(first.Keys, Is.EqualTo(second.Keys));
            foreach (var key in first.Keys)
            {
                Assert.That(first[key], Is.EqualTo(second[key]));
            }
        }

        [Test]
        public void oversized_field_should_not_hide_later_field_that_still_fits_budget()
        {
            var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < 11; index++)
            {
                tags[$"A{index:00}"] = new string('漢', 256);
            }

            tags["Z_SMALL"] = "still useful";

            var result = InvokePrivateStaticTagMapMethod(tags, null);

            Assert.Multiple(() =>
            {
                Assert.That(result.ContainsKey("A10"), Is.False, "The eleventh 768-byte value should exceed the 8-KiB budget.");
                Assert.That(result["Z_SMALL"], Is.EqualTo(new[] { "still useful" }), "A later small value must be considered after an oversized field is skipped.");
            });
        }

        [Test]
        public void build_v5_request_tags_should_preserve_separate_values_of_one_field()
        {
            var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["MP4:----"] = new List<string> { "Impact Winter", "3" }
            };

            var result = InvokePrivateStaticTagMapMethod(tags, null);

            Assert.That(result["MP4:----"], Is.EqualTo(new[] { "Impact Winter", "3" }));
        }

        [Test]
        public void missing_file_path_should_not_emit_filename_evidence()
        {
            var result = InvokePrivateStaticTagMapMethod(
                new Dictionary<string, string> { ["TITLE"] = "Dune" },
                null);

            Assert.That(result.ContainsKey("file_name"), Is.False);
        }

        private static string InvokePrivateStaticStringMethod(string methodName, string query, IDictionary<string, List<string>> tags)
        {
            var method = typeof(V5MatchingService).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null, $"Unable to locate {methodName} on {typeof(V5MatchingService).FullName}");

            return (string)method.Invoke(null, new object[] { query, tags });
        }

        private static Dictionary<string, List<string>> InvokePrivateStaticTagMapMethod(Dictionary<string, string> tags, string filePath)
        {
            var logicalTags = tags.ToDictionary(kv => kv.Key, kv => new List<string> { kv.Value }, StringComparer.OrdinalIgnoreCase);
            return InvokePrivateStaticTagMapMethod(logicalTags, filePath);
        }

        private static Dictionary<string, List<string>> InvokePrivateStaticTagMapMethod(Dictionary<string, List<string>> tags, string filePath)
        {
            var method = typeof(V5MatchingService).GetMethod("BuildV5RequestTags", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null, $"Unable to locate BuildV5RequestTags on {typeof(V5MatchingService).FullName}");

            return (Dictionary<string, List<string>>)method.Invoke(null, new object[] { tags, filePath });
        }
    }
}
