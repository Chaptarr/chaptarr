using System;
using System.Diagnostics;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Common.Instrumentation;

namespace Chaptarr.Core.Test.Common
{
    [TestFixture]
    public class LogRedactionBoundaryFixture
    {
        [TestCase("https://books.example/rest/ping?u=one&p=two&u=three&p=four", "https://books.example/rest/ping?u=(removed)&p=(removed)&u=(removed)&p=(removed)")]
        [TestCase("https://books.example/rest/rest/ping?p=one&amp;u=two", "https://books.example/rest/rest/ping?p=(removed)&amp;u=(removed)")]
        [TestCase("https://books.example/rest/ping?u=\"one two\"&p='three four'", "https://books.example/rest/ping?u=\"(removed)\"&p='(removed)'")]
        [TestCase("https://books.example/search?u=keep&p=2", "https://books.example/search?u=keep&p=2")]
        [TestCase("https://books.example/rest/ping?u=one https://books.example/search?u=keep&p=2", "https://books.example/rest/ping?u=(removed) https://books.example/search?u=keep&p=2")]
        [TestCase("Token of Love - key moments of a life", "Token of Love - key moments of a life")]
        [TestCase("/books/Token of Love/x@y.mp3", "/books/Token of Love/x@y.mp3")]
        [TestCase("user interface: 3 books, key evidence retained", "user interface: 3 books, key evidence retained")]
        public void preserve_diagnostics_and_bound_short_credentials_to_their_protocol(string input, string expected)
        {
            Assert.That(CleanseLogMessage.Cleanse(input), Is.EqualTo(expected));
        }

        [TestCase("subsonic-query")]
        [TestCase("subsonic-no-query")]
        public void repeated_url_fragments_have_linear_growth(string kind)
        {
            string Input(int count) => kind == "subsonic-query"
                ? "/rest/ping?" + string.Concat(Enumerable.Repeat("&p=synthetic", count))
                : string.Concat(Enumerable.Repeat("/rest/x", count));

            double Measure(string input)
            {
                CleanseLogMessage.Cleanse(input);
                var best = double.MaxValue;
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    var watch = Stopwatch.StartNew();
                    CleanseLogMessage.Cleanse(input);
                    best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
                }
                return best;
            }

            // At least 16 KiB and 64 KiB: large enough to expose repeated-prefix backtracking.
            var small = Input(2400);
            var large = Input(9600);
            var smallMs = Measure(small);
            var largeMs = Measure(large);
            var ratio = largeMs / smallMs;
            TestContext.Out.WriteLine($"{kind}: {small.Length} chars {smallMs:F3} ms; {large.Length} chars {largeMs:F3} ms; growth {ratio:F2}x");
            Assert.That(ratio, Is.LessThan(8), "Quadrupling a log line must not cause quadratic redaction work");
            var result = CleanseLogMessage.Cleanse(large);
            if (kind == "subsonic-query") Assert.That(result, Does.Not.Contain("synthetic"));
            else Assert.That(result, Is.EqualTo(large));
        }
    }
}
