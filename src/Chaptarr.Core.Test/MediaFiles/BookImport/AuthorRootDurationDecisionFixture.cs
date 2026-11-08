using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles.BookImport.Services;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class AuthorRootDurationDecisionFixture
    {
        [TestCase(1000, "MergeMultipart", 1000, null, TestName = "should_merge_multipart_when_sum_matches_edition_duration")]
        [TestCase(1000, "SplitDuplicates", 2000, null, TestName = "should_split_duplicates_when_each_file_matches_edition_duration")]
        public void should_decide_merge_or_split_when_duration_is_decisive(int editionDuration, string expectedDecision, int expectedSum, string expectedReason)
        {
            var method = typeof(BookCoalescingHelper).GetMethod("DecideRecordingDurationAction", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);

            var perFile = expectedDecision == "MergeMultipart"
                ? new List<int?> { 400, 600 }
                : new List<int?> { 1000, 1000 };

            object[] args = { (int?)editionDuration, perFile, 0, 0, null };
            var decision = method.Invoke(null, args);

            Assert.That(decision?.ToString(), Is.EqualTo(expectedDecision));
            Assert.That((int)args[3], Is.EqualTo(expectedSum));
            Assert.That((string)args[4], Is.EqualTo(expectedReason));
        }

        [TestCase(36780, new[] { 36846, 36846 }, "SplitDuplicates")]
        [TestCase(36780, new[] { 36720, 60 }, "MergeMultipart")]
        [TestCase(480, new[] { 240, 240 }, "MergeMultipart")]
        [TestCase(480, new[] { 240, 240, 240, 240 }, "Unmapped")]
        [TestCase(36000, new[] { 36500, 36500 }, "Unmapped")]
        public void should_keep_existing_strict_complete_copy_tolerance(int expected, int[] durations, string decision)
        {
            Assert.That(BookCoalescingHelper.DecideRecordingDurationAction(expected,
                System.Array.ConvertAll(durations, value => (int?)value), out _, out _, out _).ToString(), Is.EqualTo(decision));
        }

        [Test]
        public void should_fail_closed_when_duration_is_missing_or_mismatched()
        {
            var method = typeof(BookCoalescingHelper).GetMethod("DecideRecordingDurationAction", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);

            // No edition duration
            {
                object[] args = { (int?)null, new List<int?> { 1000, 1000 }, 0, 0, null };
                var decision = method.Invoke(null, args);
                Assert.That(decision?.ToString(), Is.EqualTo("Unmapped"));
                Assert.That((string)args[4], Is.EqualTo("NO_EDITION_DURATION"));
            }

            // No file duration
            {
                object[] args = { (int?)1000, new List<int?> { null, 600 }, 0, 0, null };
                var decision = method.Invoke(null, args);
                Assert.That(decision?.ToString(), Is.EqualTo("Unmapped"));
                Assert.That((string)args[4], Is.EqualTo("NO_FILE_DURATION"));
            }

            // Mismatch (neither multipart sum nor duplicate-full-copies)
            {
                object[] args = { (int?)1000, new List<int?> { 400, 700 }, 0, 0, null };
                var decision = method.Invoke(null, args);
                Assert.That(decision?.ToString(), Is.EqualTo("Unmapped"));
                Assert.That((string)args[4], Is.EqualTo("DURATION_MISMATCH"));
            }
        }
    }
}

