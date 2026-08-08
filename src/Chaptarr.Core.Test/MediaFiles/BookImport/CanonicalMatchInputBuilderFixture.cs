using System;
using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles.BookImport;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class CanonicalMatchInputBuilderFixture
    {
        [Test]
        public void build_embedded_query_should_drop_trash_keys_and_deduplicate_values()
        {
            var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["TITLE"] = new List<string> { "Alpha", "Alpha" },
                ["ALBUM"] = new List<string> { "Beta" },
                ["genre"] = new List<string> { "Gamma" },
                ["ENCODEDBY"] = new List<string> { "Delta" },
                ["comment"] = new List<string> { "Epsilon" }
            };

            var query = CanonicalMatchInputBuilder.BuildEmbeddedQuery(tags);
            var tokens = query.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            Assert.That(tokens, Is.EquivalentTo(new[] { "alpha", "beta" }));
            Assert.That(tokens, Has.Length.EqualTo(2));
        }

        [Test]
        public void build_path_derived_tags_should_use_folder_names_and_filename()
        {
            var tags = CanonicalMatchInputBuilder.BuildPathDerivedTags(
                "/audiobooks/J.K. Rowling/Harry Potter/Harry Potter and the Order of the Phoenix.m4b",
                "/audiobooks/J.K. Rowling/Harry Potter",
                "/audiobooks/J.K. Rowling");

            Assert.That(tags["ALBUM"], Is.EquivalentTo(new[] { "Harry Potter" }));
            Assert.That(tags["ARTIST"], Is.EquivalentTo(new[] { "J.K. Rowling" }));
            Assert.That(tags["ALBUMARTIST"], Is.EquivalentTo(new[] { "J.K. Rowling" }));
            Assert.That(tags["AUTHOR"], Is.EquivalentTo(new[] { "J.K. Rowling" }));
            Assert.That(tags["TITLE"], Is.EquivalentTo(new[] { "Harry Potter and the Order of the Phoenix" }));
        }
    }
}
