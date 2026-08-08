using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles.BookImport.Services;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class AuthorReadyUnitTagConsensusFixture
    {
        [Test]
#pragma warning disable SYSLIB0050
        public void build_unit_tags_by_key_should_use_consensus_for_large_units()
        {
            var handler = (IngestQueueOnAuthorReadyHandler)FormatterServices.GetUninitializedObject(typeof(IngestQueueOnAuthorReadyHandler));
            var method = typeof(IngestQueueOnAuthorReadyHandler).GetMethod("BuildUnitTagsByKey", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);

            var claimed = new List<IngestQueueItem>
            {
                QueueItem(1, "/library/Frank Herbert/Dune/01.mp3", "{}"),
                QueueItem(2, "/library/Frank Herbert/Dune/02.mp3", "{\"ALBUM\":[\"Dune\"],\"ALBUMARTIST\":[\"Frank Herbert\"],\"TITLE\":[\"Track 2\"]}"),
                QueueItem(3, "/library/Frank Herbert/Dune/03.mp3", "{\"ALBUM\":[\"Dune\"],\"ALBUMARTIST\":[\"Frank Herbert\"],\"TITLE\":[\"Track 3\"]}"),
                QueueItem(4, "/library/Frank Herbert/Dune/04.mp3", "{\"ALBUM\":[\"Dune\"],\"ALBUMARTIST\":[\"Frank Herbert\"],\"TITLE\":[\"Track 4\"]}"),
                QueueItem(5, "/library/Frank Herbert/Dune/05.mp3", "{\"ALBUM\":[\"Dune\"],\"ALBUMARTIST\":[\"Frank Herbert\"],\"TITLE\":[\"Track 5\"]}"),
                QueueItem(6, "/library/Frank Herbert/Dune/06.mp3", "{\"ALBUM\":[\"Dune\"],\"ALBUMARTIST\":[\"Frank Herbert\"],\"TITLE\":[\"Track 6\"]}")
            };

            var extractionFailures = new HashSet<string>();
            var tagsByUnit = (Dictionary<string, Dictionary<string, List<string>>>)method.Invoke(handler, new object[] { claimed, extractionFailures });
            Assert.That(tagsByUnit, Is.Not.Null);
            Assert.That(extractionFailures, Is.Empty);
            Assert.That(tagsByUnit.Count, Is.EqualTo(1));

            var unitTags = default(Dictionary<string, List<string>>);
            foreach (var entry in tagsByUnit)
            {
                unitTags = entry.Value;
            }

            Assert.That(unitTags, Is.Not.Null);
            Assert.That(unitTags.ContainsKey("ALBUM"), Is.True);
            Assert.That(unitTags["ALBUM"], Is.EquivalentTo(new[] { "Dune" }));
            Assert.That(unitTags.ContainsKey("ALBUMARTIST"), Is.True);
            Assert.That(unitTags["ALBUMARTIST"], Is.EquivalentTo(new[] { "Frank Herbert" }));
            Assert.That(unitTags.ContainsKey("TITLE"), Is.False, "per-track titles should not dominate large unit tags");
        }

        [Test]
        public void build_unit_tags_by_key_should_not_promote_sparse_repeat_values_for_large_units()
        {
            var handler = (IngestQueueOnAuthorReadyHandler)FormatterServices.GetUninitializedObject(typeof(IngestQueueOnAuthorReadyHandler));
            var method = typeof(IngestQueueOnAuthorReadyHandler).GetMethod("BuildUnitTagsByKey", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);

            var claimed = new List<IngestQueueItem>
            {
                QueueItem(1, "/library/Frank Herbert/Dune/01.mp3", "{}"),
                QueueItem(2, "/library/Frank Herbert/Dune/02.mp3", "{}"),
                QueueItem(3, "/library/Frank Herbert/Dune/03.mp3", "{}"),
                QueueItem(4, "/library/Frank Herbert/Dune/04.mp3", "{}"),
                QueueItem(5, "/library/Frank Herbert/Dune/05.mp3", "{\"TITLE\":[\"Part 1\"],\"ALBUM\":[\"Dune\"]}"),
                QueueItem(6, "/library/Frank Herbert/Dune/06.mp3", "{\"TITLE\":[\"Part 1\"],\"ALBUM\":[\"Dune\"]}")
            };

            var extractionFailures = new HashSet<string>();
            var tagsByUnit = (Dictionary<string, Dictionary<string, List<string>>>)method.Invoke(handler, new object[] { claimed, extractionFailures });
            Assert.That(tagsByUnit, Is.Empty, "two matching tag sets should not define consensus for a six-file unit");
            Assert.That(extractionFailures, Is.Empty);
        }
#pragma warning restore SYSLIB0050

        private static IngestQueueItem QueueItem(int id, string path, string tagsJson)
        {
            return new IngestQueueItem
            {
                Id = id,
                Path = path,
                TagsJson = tagsJson,
                Status = "queued"
            };
        }
    }
}
