using System;
using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles.BookImport;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class UnitTagConsensusBuilderFixture
    {
        [Test]
        public void build_consensus_should_keep_stable_values_and_drop_singletons_for_repeated_keys()
        {
            var tags = new[]
            {
                new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ALBUM"] = new List<string> { "Dune" },
                    ["TITLE"] = new List<string> { "Track 1" },
                    ["ALBUMARTIST"] = new List<string> { "Frank Herbert" }
                },
                new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ALBUM"] = new List<string> { "Dune" },
                    ["TITLE"] = new List<string> { "Track 2" },
                    ["ALBUMARTIST"] = new List<string> { "Frank Herbert" }
                },
                new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ALBUM"] = new List<string> { "Dune" },
                    ["TITLE"] = new List<string> { "Track 3" },
                    ["ALBUMARTIST"] = new List<string> { "Frank Herbert" }
                }
            };

            var result = UnitTagConsensusBuilder.BuildConsensus(tags, totalFileCount: tags.Length);

            Assert.That(result.ContainsKey("ALBUM"), Is.True);
            Assert.That(result["ALBUM"], Is.EquivalentTo(new[] { "Dune" }));
            Assert.That(result.ContainsKey("ALBUMARTIST"), Is.True);
            Assert.That(result["ALBUMARTIST"], Is.EquivalentTo(new[] { "Frank Herbert" }));
            Assert.That(result.ContainsKey("TITLE"), Is.False, "singleton per-file titles should be removed for repeated keys");
        }

        [Test]
        public void build_consensus_should_fallback_to_single_tagset_when_only_one_is_available()
        {
            var single = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["ALBUM"] = new List<string> { "Dreamer of Dune" },
                ["TITLE"] = new List<string> { "Chapter 1" }
            };

            var result = UnitTagConsensusBuilder.BuildConsensus(new[] { single }, totalFileCount: 1);

            Assert.That(result["ALBUM"], Is.EquivalentTo(new[] { "Dreamer of Dune" }));
            Assert.That(result["TITLE"], Is.EquivalentTo(new[] { "Chapter 1" }));
        }

        [Test]
        public void build_consensus_should_not_accept_sparse_two_file_repeats_for_large_units()
        {
            var sparse = new[]
            {
                new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ALBUM"] = new List<string> { "Dune" },
                    ["TITLE"] = new List<string> { "Part 1" }
                },
                new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ALBUM"] = new List<string> { "Dune" },
                    ["TITLE"] = new List<string> { "Part 1" }
                }
            };

            var result = UnitTagConsensusBuilder.BuildConsensus(sparse, totalFileCount: 6);

            Assert.That(result, Is.Empty, "two tagged files should not define consensus for a six-file unit");
        }
    }
}
