using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using Chaptarr.Api.V1.Books;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Commands;

namespace Chaptarr.Core.Test.MediaFiles
{
    public partial class RetagMediaScopeFixture
    {
        [TestCase(401, false)]
        [TestCase(403, false)]
        [TestCase(401, true)]
        [TestCase(403, true)]
        public void access_refusal_stops_that_library_but_not_the_other_library(int status, bool write)
        {
            foreach (var id in Enumerable.Range(1, 1600)) Add(id);
            Add(1601, root: Other);
            var read = _preview;
            if (write) _write = file => { if (file.Id == 1) throw ResponseError(status); };
            else _preview = (ids, settings) => settings.Library == "first" ? throw ResponseError(status) : read(ids, settings);
            var batch = _ebooks.RetagFiles(new RetagFilesCommand(), _files, new TagBatch());
            Assert.That((batch.Unavailable, batch.Written, batch.Failed, batch.Incomplete), Is.EqualTo((write ? 1599 : 1600, 1, write ? 1 : 0, 0)));
            Assert.That(_attempts, Is.EqualTo(write ? new[] { 1, 1601 } : new[] { 1601 }));
            Assert.That(_messages.Logs.Count(line => line.StartsWith("Warn:") && line.Contains("Calibre refused access")), Is.EqualTo(1));
            Assert.That(_health.GetStatus(_gate.GetKey(_roots[0].CalibreSettings)).LastErrorMessage, Does.Contain("Calibre refused access"));
        }

        [Test]
        public void unexpected_html_200_reopens_backoff_without_claiming_recovery_or_logging_bulk_url()
        {
            Add(1);
            var key = _gate.GetKey(_roots[0].CalibreSettings);
            _gate.ReportException(key, ResponseError(504));
            var state = _health.GetStatus(key);
            state.RateLimitedUntil = DateTime.UtcNow.AddSeconds(-1);
            _messages.Logs.Clear();
            _preview = (_, _) => throw new UnexpectedHtmlContentException(new HttpResponse(
                new HttpRequest("http://localhost/ajax/books?ids=1,2,3"), new HttpHeader(), "<html>login</html>", HttpStatusCode.OK));
            var batch = _ebooks.RetagFiles(new RetagFilesCommand(), _files, new TagBatch());
            Assert.That((batch.Unavailable, batch.Written), Is.EqualTo((1, 0)));
            Assert.That(state.IsHealthy, Is.False);
            Assert.That(state.ProbeInProgress, Is.False);
            Assert.That(_gate.CanBegin(key, out _), Is.False);
            Assert.That(_attempts, Is.Empty);
            var warnings = _messages.Logs.Where(line => line.StartsWith("Warn:")).ToList();
            Assert.That(warnings, Is.EqualTo(new[]
            {
                $"Warn: Calibre {key} is unavailable after 2 consecutive failure(s). Retrying in 3m. Last error: Calibre returned a web page instead of API data (HTTP 200)"
            }));
        }

        [Test]
        public void later_page_failure_replaces_a_write_trigger_in_the_same_batch()
        {
            foreach (var id in Enumerable.Range(1, 751)) Add(id);
            var key = _gate.GetKey(_roots[0].CalibreSettings);
            _write = file => throw new HttpRequestException(HttpRequestError.ConnectionError, "refused");
            // Advance the existing cooldown as the first page checks its remaining files;
            // the next page must perform real work and replace the earlier file context.
            var reads = 0;
            var read = _preview;
            _preview = (ids, settings) => ++reads == 1 ? read(ids, settings) : throw ResponseError(504);
            var batch = new TagBatch();
            // Two calls intentionally share the command's batch, as its per-author passes do.
            _ebooks.RetagFiles(new RetagFilesCommand(), _files.Take(750).ToList(), batch);
            Assert.That(batch.UnavailableLibraries[key].TriggerFile, Is.EqualTo(_files[0].Path));
            _health.GetStatus(key).RateLimitedUntil = DateTime.UtcNow.AddSeconds(-1);
            _ebooks.RetagFiles(new RetagFilesCommand(), _files.Skip(750).ToList(), batch);
            Assert.That(batch.UnavailableLibraries[key].TriggerFile, Is.Null);
            Assert.That(batch.Unavailable, Is.EqualTo(751));
        }

        [TestCase("failed", "Warn")]
        [TestCase("incomplete", "Warn")]
        [TestCase("unavailable", "Warn")]
        [TestCase("success", null)]
        public void automatic_summary_level_discriminates_real_outcomes(string outcome, string level)
        {
            var file = Add(1);
            if (outcome == "failed") _write = _ => throw ResponseError(404);
            if (outcome == "incomplete") _outcome = _ => NzbDrone.Core.Books.Calibre.CalibreTagUpdateResult.Incomplete;
            if (outcome == "unavailable") _write = _ => throw new HttpRequestException(HttpRequestError.ConnectionError, "refused");
            _ebooks.WriteTags(file, true);
            if (level == null)
            {
                Assert.That(_messages.Logs, Has.None.Contains("Calibre tags:"));
                return;
            }
            var summary = _messages.Logs.Single(line => line.Contains("Calibre tags:"));
            Assert.That(summary, Does.StartWith(level + ":"));
            Assert.That(summary, Does.Contain("1 pages/1 ids read").And.Contain("0 missing roots"));
        }

        [Test]
        public void symlink_only_and_success_share_one_quiet_batch_level_rule()
        {
            var batch = new TagBatch();
            batch.SkippedSymlinkPaths.Add(System.IO.Path.Combine(Root, "protected.epub"));
            Assert.That(batch.HasProblems, Is.False);
            batch.Written = 1;
            Assert.That(batch.HasProblems, Is.False);
        }

        [TestCase("0", true)]
        [TestCase("60", false)]
        public void preview_retry_eligibility_is_decided_by_server_and_mapped_to_api(string seconds, bool retryNow)
        {
            Add(1);
            var response = new HttpResponse(new HttpRequest("http://localhost/"),
                new HttpHeader { ["Retry-After"] = seconds }, "", HttpStatusCode.TooManyRequests);
            _preview = (_, _) => throw new TooManyRequestsException(response.Request, response);
            var preview = _ebooks.GetRetagPreviewsByAuthor(1).Single();
            Assert.That(preview.RetryNow, Is.EqualTo(retryNow));
            Assert.That(preview.ToResource().RetryNow, Is.EqualTo(retryNow));
        }
    }
}
