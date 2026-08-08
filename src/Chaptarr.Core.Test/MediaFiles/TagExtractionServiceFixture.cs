using System;
using System.Collections.Generic;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles.TagExtraction;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class TagExtractionServiceFixture
    {
        private sealed class StubExtractor : ITagExtractorWithDuration
        {
            private readonly bool _available;

            public StubExtractor(
                string name,
                int priority,
                Dictionary<string, List<string>> tags = null,
                int? durationSeconds = null,
                bool available = true,
                Exception error = null)
            {
                Name = name;
                Priority = priority;
                Tags = tags ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                DurationSeconds = durationSeconds;
                _available = available;
                Error = error;
            }

            public int AvailabilityChecks { get; private set; }
            public int ExtractionCalls { get; private set; }
            public Dictionary<string, List<string>> Tags { get; }
            public int? DurationSeconds { get; }
            public Exception Error { get; }

            public bool IsAvailable
            {
                get
                {
                    AvailabilityChecks++;
                    return _available;
                }
            }

            public int Priority { get; }
            public string Name { get; }

            public Dictionary<string, List<string>> ExtractTags(string path)
            {
                ExtractionCalls++;
                if (Error != null)
                {
                    throw Error;
                }

                return Clone(Tags);
            }

            public (Dictionary<string, List<string>> Tags, int? DurationSeconds) ExtractTagsAndDuration(string path)
            {
                return (ExtractTags(path), DurationSeconds);
            }

            private static Dictionary<string, List<string>> Clone(Dictionary<string, List<string>> tags)
            {
                var clone = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in tags)
                {
                    clone[pair.Key] = pair.Value == null ? null : new List<string>(pair.Value);
                }

                return clone;
            }
        }

        [Test]
        public void should_not_even_probe_ffprobe_when_taglibsharp_has_match_evidence()
        {
            var tagLib = new StubExtractor("TagLibSharp", 2, Tags(("ID3v2:TIT2", "The Hobbit")), 36000);
            var ffprobe = new StubExtractor("FFprobe", 3, Tags(("title", "Wrong")), 1);
            var subject = Subject(tagLib, ffprobe);

            var result = subject.ExtractTagsAndDurationWithResult("book.m4b");

            Assert.That(result.Disposition, Is.EqualTo(TagExtractionDisposition.Evidence));
            Assert.That(result.Extractor, Is.EqualTo("TagLibSharp"));
            Assert.That(result.DurationSeconds, Is.EqualTo(36000));
            Assert.That(ffprobe.AvailabilityChecks, Is.Zero);
            Assert.That(ffprobe.ExtractionCalls, Is.Zero);
        }

        [Test]
        public void should_fall_back_after_excluded_only_tags_and_keep_taglibsharp_duration()
        {
            var tagLib = new StubExtractor(
                "TagLibSharp",
                2,
                Tags(("ID3v2:COMM:eng", "From the author of Dune"), ("ID3v2:TCON", "Science Fiction")),
                36000);
            var ffprobe = new StubExtractor("FFprobe", 3, Tags(("title", "Dune Messiah")), 35990);
            var subject = Subject(tagLib, ffprobe);

            var result = subject.ExtractTagsAndDurationWithResult("book.m4b");

            Assert.That(result.Disposition, Is.EqualTo(TagExtractionDisposition.Evidence));
            Assert.That(result.Extractor, Is.EqualTo("FFprobe"));
            Assert.That(result.Tags["title"], Is.EquivalentTo(new[] { "Dune Messiah" }));
            Assert.That(result.DurationSeconds, Is.EqualTo(36000));
            Assert.That(ffprobe.ExtractionCalls, Is.EqualTo(1));
        }

        [Test]
        public void should_preserve_noisy_only_as_distinct_from_tagless_when_fallback_finds_nothing()
        {
            var tagLib = new StubExtractor("TagLibSharp", 2, Tags(("comment", "Promotional copy")), 123);
            var ffprobe = new StubExtractor("FFprobe", 3);
            var subject = Subject(tagLib, ffprobe);

            var result = subject.ExtractTagsAndDurationWithResult("book.m4b");

            Assert.That(result.Disposition, Is.EqualTo(TagExtractionDisposition.NoisyOnly));
            Assert.That(result.Extractor, Is.EqualTo("TagLibSharp"));
            Assert.That(result.Tags, Does.ContainKey("comment"));
            Assert.That(result.DurationSeconds, Is.EqualTo(123));
        }

        [Test]
        public void should_report_tagless_when_both_readers_succeed_without_any_values()
        {
            var result = Subject(
                    new StubExtractor("TagLibSharp", 2),
                    new StubExtractor("FFprobe", 3))
                .ExtractTagsAndDurationWithResult("book.m4b");

            Assert.That(result.Disposition, Is.EqualTo(TagExtractionDisposition.Tagless));
            Assert.That(result.Succeeded, Is.True);
        }

        [Test]
        public void should_report_failed_and_throw_from_legacy_api_when_both_readers_fail()
        {
            var subject = Subject(
                new StubExtractor("TagLibSharp", 2, error: new InvalidOperationException("taglib failed")),
                new StubExtractor("FFprobe", 3, error: new InvalidOperationException("ffprobe failed")));

            var result = subject.ExtractTagsAndDurationWithResult("book.m4b");

            Assert.That(result.Disposition, Is.EqualTo(TagExtractionDisposition.Failed));
            Assert.That(result.Succeeded, Is.False);
            var exception = Assert.Throws<TagExtractionException>(() => subject.ExtractTagsAndDuration("book.m4b"));
            Assert.That(exception.Reason, Is.EqualTo(TagExtractionResult.FailureReason));
        }

        [Test]
        public void should_use_ffprobe_when_taglibsharp_throws()
        {
            var result = Subject(
                    new StubExtractor("TagLibSharp", 2, error: new InvalidOperationException("taglib failed")),
                    new StubExtractor("FFprobe", 3, Tags(("album", "The Hobbit")), 40000))
                .ExtractTagsAndDurationWithResult("book.m4b");

            Assert.That(result.Disposition, Is.EqualTo(TagExtractionDisposition.Evidence));
            Assert.That(result.Extractor, Is.EqualTo("FFprobe"));
            Assert.That(result.DurationSeconds, Is.EqualTo(40000));
        }

        private static TagExtractionService Subject(params ITagExtractor[] extractors)
        {
            return new TagExtractionService(
                extractors,
                new DurationResolverStub(),
                LogManager.GetLogger("tag-extraction-test"));
        }

        private static Dictionary<string, List<string>> Tags(params (string Key, string Value)[] values)
        {
            var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in values)
            {
                tags[key] = new List<string> { value };
            }

            return tags;
        }
    }
}
