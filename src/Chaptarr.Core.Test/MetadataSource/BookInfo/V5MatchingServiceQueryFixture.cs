using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MetadataSource.BookInfo;

namespace Chaptarr.Core.Test.MetadataSource.BookInfo
{
    [TestFixture]
    public class V5MatchingServiceQueryFixture
    {
        [Test]
        public void build_effective_query_should_fall_back_to_the_shared_embedded_query_builder()
        {
            var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["TITLE"] = new List<string> { "Dune" },
                ["ARTIST"] = new List<string> { "Frank Herbert" },
                ["GENRE"] = new List<string> { "Science Fiction" },
                ["COMMENT"] = new List<string> { "legacy plot summary" },
                ["filename"] = new List<string> { "Dune.m4b" },
                ["ENCODEDBY"] = new List<string> { "qaac" }
            };

            var expected = CanonicalMatchInputBuilder.BuildEmbeddedQuery(tags);
            var actual = InvokeBuildEffectiveQuery(string.Empty, tags);

            Assert.That(actual, Is.EqualTo(expected));
            Assert.That(actual, Is.EqualTo("dune frank herbert"));
        }

        [Test]
        public void build_effective_query_should_preserve_an_explicit_query()
        {
            var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["TITLE"] = new List<string> { "Dune" },
                ["ARTIST"] = new List<string> { "Frank Herbert" }
            };

            var actual = InvokeBuildEffectiveQuery("explicit query", tags);

            Assert.That(actual, Is.EqualTo("explicit query"));
        }

        private static string InvokeBuildEffectiveQuery(string query, IDictionary<string, List<string>> tags)
        {
            var method = typeof(V5MatchingService).GetMethod("BuildEffectiveQuery", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null, $"Unable to locate BuildEffectiveQuery on {typeof(V5MatchingService).FullName}");

            return (string)method.Invoke(null, new object[] { query, tags });
        }
    }
}
