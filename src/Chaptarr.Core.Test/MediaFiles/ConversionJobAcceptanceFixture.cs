using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class ConversionJobAcceptanceFixture
    {
        [TestCase(ConversionJobStatus.Queued, true)]
        [TestCase(ConversionJobStatus.Converting, true)]
        [TestCase(ConversionJobStatus.ReadyToImport, true)]
        [TestCase(ConversionJobStatus.Completed, false)]
        [TestCase(ConversionJobStatus.Failed, false)]
        [TestCase(ConversionJobStatus.Cancelling, false)]
        [TestCase(ConversionJobStatus.Cancelled, false)]
        public void only_accepted_nonterminal_conversion_states_supply_the_saved_request(ConversionJobStatus status, bool accepted)
        {
            var request = Request();
            var job = new ConversionJob { DownloadId = request.DownloadId, Status = status, RequestJson = JsonSerializer.Serialize(request) };

            var saved = job.GetAcceptedRequest();

            Assert.That(saved != null, Is.EqualTo(accepted));
            if (saved != null)
            {
                Assert.That(saved.EditionId, Is.EqualTo(request.EditionId));
                Assert.That(saved.TargetQualityId, Is.EqualTo(request.TargetQualityId));
                Assert.That(saved.AudioBitrate, Is.EqualTo(request.AudioBitrate));
                Assert.That(saved.TagSignature, Is.EqualTo(request.TagSignature));
            }
        }

        [TestCase("corrupt")]
        [TestCase("missing")]
        [TestCase("discard")]
        [TestCase("other-download")]
        [TestCase("empty-sources")]
        [TestCase("null-source")]
        [TestCase("blank-source")]
        [TestCase("missing-edition")]
        [TestCase("invalid-edition")]
        public void invalid_or_discarded_requests_never_grant_acceptance(string condition)
        {
            var request = Request();
            if (condition == "discard") request.DiscardRequested = true;
            if (condition == "other-download") request.DownloadId = "different-download";
            if (condition == "empty-sources") request.Sources.Clear();
            if (condition == "null-source") request.Sources[0] = null;
            if (condition == "blank-source") request.Sources[0].Path = " ";
            if (condition == "missing-edition") request.EditionId = 0;
            if (condition == "invalid-edition") request.EditionId = -1;
            var job = new ConversionJob
            {
                DownloadId = "accepted-download",
                Status = ConversionJobStatus.Queued,
                RequestJson = condition == "corrupt" ? "{" : condition == "missing" ? null : JsonSerializer.Serialize(request)
            };

            Assert.That(job.GetAcceptedRequest(), Is.Null);
        }

        [TestCase("unchanged", true)]
        [TestCase("reordered", true)]
        [TestCase("missing-part", false)]
        [TestCase("extra-part", false)]
        [TestCase("other-book", false)]
        [TestCase("changed-size", false)]
        [TestCase("changed-time", false)]
        [TestCase("missing-source", false)]
        public void acceptance_requires_the_same_recording_and_unchanged_source_signatures(string condition, bool matches)
        {
            var directory = Path.Combine(Path.GetTempPath(), "conversion-accepted-signatures-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var request = Request();
                request.Sources = Enumerable.Range(1, 2).Select(part =>
                {
                    var path = Path.Combine(directory, "part-" + part + ".mp3");
                    File.WriteAllText(path, "source part " + part);
                    return new ConversionArtifactSource { Path = path, Size = new FileInfo(path).Length, ModifiedUtcTicks = File.GetLastWriteTimeUtc(path).Ticks };
                }).ToList();
                var paths = request.Sources.Select(source => source.Path).ToList();
                if (condition == "reordered") paths.Reverse();
                if (condition == "missing-part") paths.RemoveAt(0);
                if (condition == "extra-part") paths.Add(Path.Combine(directory, "another-recording.mp3"));
                if (condition == "changed-size") File.AppendAllText(paths[0], "changed");
                if (condition == "changed-time") File.SetLastWriteTimeUtc(paths[0], new DateTime(request.Sources[0].ModifiedUtcTicks, DateTimeKind.Utc).AddMinutes(1));
                if (condition == "missing-source") File.Delete(paths[0]);

                Assert.That(request.MatchesSources(condition == "other-book" ? 88 : request.BookId, paths), Is.EqualTo(matches));
                if (condition == "unchanged")
                {
                    Assert.That(request.MatchesSources(88, paths, requireBookIdentity: false), Is.True);
                    request.BookId = 0;
                    Assert.That(request.MatchesSources(88, paths), Is.True);
                    request.Sources.Clear();
                    Assert.That(request.MatchesSources(88, Array.Empty<string>()), Is.False);
                }
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        private static ConversionJobRequest Request() => new()
        {
            DownloadId = "accepted-download",
            BookId = 7,
            EditionId = 13,
            TargetQualityId = 12,
            AudioBitrate = 64,
            TagSignature = "accepted-tags",
            Sources = new List<ConversionArtifactSource> { new() { Path = "/download/source.mp3", Size = 10, ModifiedUtcTicks = 1 } }
        };
    }
}
