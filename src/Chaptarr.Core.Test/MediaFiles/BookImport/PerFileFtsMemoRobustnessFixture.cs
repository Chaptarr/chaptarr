using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles.BookImport;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class PerFileFtsMemoRobustnessFixture
    {
        [Test]
        public void a_result_that_cannot_be_serialized_should_be_returned_uncached_not_throw()
        {
            // System.Text.Json refuses NaN/Infinity; the memo must degrade to "no memo", never fail the match.
            var memo = new FileMatchingService.PerFileFtsMemo();
            var calls = 0;

            List<BookFtsMatch> Compute()
            {
                calls++;
                return new List<BookFtsMatch> { new BookFtsMatch { BookId = 1, MatchScore = double.NaN } };
            }

            var first = memo.GetOrCompute("recall", () => "k", Compute);
            var second = memo.GetOrCompute("recall", () => "k", Compute);

            Assert.That(first, Has.Count.EqualTo(1));
            Assert.That(second, Has.Count.EqualTo(1));
            Assert.That(calls, Is.EqualTo(2), "an unserializable value is recomputed, not cached");
        }

        [Test]
        public void a_key_that_cannot_be_built_should_bypass_the_memo_not_throw()
        {
            var memo = new FileMatchingService.PerFileFtsMemo();
            var calls = 0;

            var result = memo.GetOrCompute<int>("rank", () => throw new System.ArgumentException("NaN in key"), () => ++calls);

            Assert.That(result, Is.EqualTo(1));
            Assert.That(calls, Is.EqualTo(1));
        }
    }
}
