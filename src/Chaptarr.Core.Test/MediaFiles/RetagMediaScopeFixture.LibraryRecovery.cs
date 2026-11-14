using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.Http;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Commands;

namespace Chaptarr.Core.Test.MediaFiles
{
    public partial class RetagMediaScopeFixture
    {
        [TestCase("404", false)]
        [TestCase("401", false)]
        [TestCase("login", false)]
        [TestCase("redirect", false)]
        [TestCase("login", true)]
        [TestCase("invalid", true)]
        [TestCase("redirect", true)]
        public void unusable_library_stays_backed_off_on_recovery_and_other_library_continues(string failure, bool write)
        {
            foreach (var id in Enumerable.Range(1, 1600)) Add(id);
            Add(1601, root: Other);
            var key = _gate.GetKey(_roots[0].CalibreSettings);
            _gate.ReportException(key, ResponseError(504));
            _health.GetStatus(key).RateLimitedUntil = DateTime.UtcNow.AddSeconds(-1);
            _messages.Logs.Clear();
            Exception Failure() => failure switch
            {
                "login" => new UnexpectedHtmlContentException(new HttpResponse(
                    new HttpRequest("http://localhost/ajax/books?ids=" + string.Join(",", Enumerable.Range(1, 750))),
                    new HttpHeader(), "<html>login</html>", HttpStatusCode.OK)),
                "invalid" => new CalibreInvalidResponseException("Calibre did not confirm the change"),
                "redirect" => ResponseError(302),
                _ => ResponseError(int.Parse(failure, CultureInfo.InvariantCulture))
            };
            var reads = 0;
            var read = _preview;
            _preview = (ids, settings) =>
            {
                if (settings.Library == "first") { reads++; if (!write) throw Failure(); }
                return read(ids, settings);
            };
            if (write) _write = file => { if (file.Id == 1) throw Failure(); };
            var batch = _ebooks.RetagFiles(new RetagFilesCommand(), _files, new TagBatch());
            Assert.That(reads, Is.EqualTo(1));
            Assert.That(batch.Written, Is.EqualTo(1));
            Assert.That(batch.Failed, Is.EqualTo(write ? 1 : 0));
            Assert.That(batch.Unavailable, Is.EqualTo(write ? 1599 : 1600));
            Assert.That(_attempts, Is.EqualTo(write ? new[] { 1, 1601 } : new[] { 1601 }));
            Assert.That(_gate.CanBegin(key, out _), Is.False);
            Assert.That(_health.GetStatus(key).ProbeInProgress, Is.False);
            var warnings = _messages.Logs.Where(line => line.StartsWith("Warn:")).ToList();
            Assert.That(warnings.Count(line => line.Contains("is unavailable after")), Is.EqualTo(1));
            Assert.That(warnings, Has.None.Contains("?ids=").And.None.Contains("ajax/books")); // absent-literal: URI query separator is added at runtime; bulk IDs must remain redacted.
            Assert.That(warnings.All(line => line.Length < 500), Is.True, string.Join("\n", warnings));
            Assert.That(_messages.Logs, Has.None.StartsWith("Error:"));
        }

        [Test]
        public void synthetic_per_book_failure_policy_does_not_pause_other_books()
        {
            Add(1); Add(2);
            _write = file => { if (file.Id == 1) throw new CalibreException("Calibre did not confirm the change"); };
            var batch = _ebooks.RetagFiles(new RetagFilesCommand(), _files, new TagBatch());
            Assert.That((batch.Failed, batch.Written, batch.Unavailable), Is.EqualTo((1, 1, 0)));
            Assert.That(_attempts, Is.EqualTo(new[] { 1, 2 }));
        }

        [Test]
        public void local_json_bookkeeping_failure_does_not_pause_calibre()
        {
            Add(1); Add(2);
            _write = file =>
            {
                if (file.Id == 1) throw new CalibreException("metadata accepted", new Newtonsoft.Json.JsonSerializationException("local bookkeeping")) { WriteAccepted = true };
            };
            var batch = _ebooks.RetagFiles(new RetagFilesCommand(), _files, new TagBatch());
            Assert.That((batch.Incomplete, batch.Written, batch.Unavailable), Is.EqualTo((1, 1, 0)));
            Assert.That(_attempts, Is.EqualTo(new[] { 1, 2 }));
        }

    }
}
