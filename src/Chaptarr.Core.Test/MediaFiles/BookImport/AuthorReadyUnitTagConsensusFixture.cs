using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles.BookImport;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class AuthorReadyUnitTagConsensusFixture
    {
        [TestCase(6)]
        [TestCase(60)]
        public void queued_metadata_should_preserve_each_files_original_fields(int count)
        {
            foreach (var track in Enumerable.Range(1, count))
            {
                var item = QueueItem(track);
                var result = IngestFileMetadataReader.Read(item, null, null);
                Assert.Multiple(() =>
                {
                    Assert.That(result.Tags["ALBUM"], Is.EqualTo(new[] { "Wild Cards VII: Dead Man's Hand" }));
                    Assert.That(result.Tags["TITLE"], Is.EqualTo(new[] { $"Wild Cards VII: Dead Man's Hand - Chapter {track}" }));
                    Assert.That(result.DurationSeconds, Is.EqualTo(item.DurationSeconds));
                    Assert.That(result.ExtractionFailed, Is.False);
                });
            }
        }

        [Test]
        public void missing_metadata_must_not_borrow_neighboring_track_tags()
        {
            var items = Enumerable.Range(1, 6).Select(QueueItem).ToArray();
            items[0].TagsJson = "{}";
            var result = items.Select(item => IngestFileMetadataReader.Read(item, null, null)).ToArray();
            Assert.That(result[0].Tags, Is.Null.Or.Empty);
            Assert.That(result.Skip(1).SelectMany(value => value.Tags["TITLE"]),
                Is.EqualTo(Enumerable.Range(2, 5).Select(track => $"Wild Cards VII: Dead Man's Hand - Chapter {track}")));
        }

        private static IngestQueueItem QueueItem(int track) => new()
        {
            Id = track,
            Path = $"/library/George R.R. Martin/Wild Cards VII/{track:00}.mp3",
            DurationSeconds = 3000,
            TagsJson = JsonSerializer.Serialize(new Dictionary<string, string[]>
            {
                ["ALBUM"] = new[] { "Wild Cards VII: Dead Man's Hand" },
                ["ARTIST"] = new[] { "George R.R. Martin" },
                ["TITLE"] = new[] { $"Wild Cards VII: Dead Man's Hand - Chapter {track}" }
            }),
            Status = "queued"
        };
    }
}
