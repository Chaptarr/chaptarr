using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentValidation.Results;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients.Direct;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.DirectDownload;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.ThingiProvider;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class DirectDownloadGrabFlowFixture
    {
        private const string CatalogUrl = "https://catalog.example/md5/abc123def456abc123def456abc123de";

        // ── Branch 1: API success → resolved URL stored, FallbackMode=None ──

        [Test]
        public async Task api_success_stores_resolved_url_and_completes_download()
        {
            using var scenario = new GrabFlowScenario();
            scenario.RegisterFastDownloadApi(CatalogUrl, apiKey: "test-key-123", resolvedUrl: "https://cdn.example/resolved-dune.epub");
            scenario.RegisterBinary("https://cdn.example/resolved-dune.epub", "application/epub+zip", "api-resolved-body");

            var client = scenario.BuildClient();
            var downloadId = await client.Download(BuildCatalogRemoteBook(), scenario.CreateIndexer(apiKey: "test-key-123"));

            var state = scenario.LoadState(downloadId);
            Assert.That(state.ResolvedUrl, Is.EqualTo("https://cdn.example/resolved-dune.epub"));
            Assert.That(state.FallbackMode, Is.EqualTo(DirectDownloadFallbackMode.None));
            Assert.That(state.DownloadUrl, Is.EqualTo("https://cdn.example/resolved-dune.epub"));

            await scenario.WaitForStatus(client, downloadId, DownloadItemStatus.Completed);
            Assert.That(File.ReadAllText(scenario.OutputPath(downloadId)), Is.EqualTo("api-resolved-body"));
        }

        // ── Branch 2: API unavailable (no key) + fallback enabled → DeferredPlaywright ──

        [Test]
        public async Task api_unavailable_with_fallback_enabled_defers_playwright_and_stores_original_url()
        {
            using var scenario = new GrabFlowScenario();
            scenario.IndexerSettings.EnableSlowFallback = true;
            scenario.BrowserResolver.SlowDownloadUrl = "https://slow.example/dune-slow.epub";
            scenario.RegisterBinary("https://slow.example/dune-slow.epub", "application/epub+zip", "browser-resolved-body");

            var client = scenario.BuildClient();
            var downloadId = await client.Download(BuildCatalogRemoteBook(), scenario.CreateIndexer(apiKey: null));

            var state = scenario.LoadState(downloadId);
            Assert.That(state.FallbackMode, Is.EqualTo(DirectDownloadFallbackMode.DeferredPlaywright));
            Assert.That(state.DownloadUrl, Is.EqualTo(CatalogUrl));
            Assert.That(state.ResolvedUrl, Is.Null, "ResolvedUrl must be null when API did not succeed");

            await scenario.WaitForStatus(client, downloadId, DownloadItemStatus.Completed);
            Assert.That(scenario.BrowserResolver.ResolveCalls, Is.GreaterThan(0), "Playwright should be invoked during transfer");
            Assert.That(File.ReadAllText(scenario.OutputPath(downloadId)), Is.EqualTo("browser-resolved-body"));
        }

        // ── Branch 3: API unavailable (no key) + fallback disabled → exception, no state ──

        [Test]
        public void api_unavailable_with_fallback_disabled_throws_and_creates_no_state()
        {
            using var scenario = new GrabFlowScenario();
            scenario.IndexerSettings.EnableSlowFallback = false;

            var client = scenario.BuildClient();

            var ex = Assert.ThrowsAsync<ReleaseDownloadException>(
                async () => await client.Download(BuildCatalogRemoteBook(), scenario.CreateIndexer(apiKey: null)));

            Assert.That(ex.Message, Does.Contain("browser fallback is disabled"));
            Assert.That(client.GetItems(), Is.Empty, "No state should be created on grab failure");
        }

        // ── Branch 4: NotApplicable → original URL, FallbackMode=None ──

        [Test]
        public async Task not_applicable_passes_through_original_url_without_fallback()
        {
            using var scenario = new GrabFlowScenario();
            scenario.RegisterBinary("https://downloads.example/dune.epub", "application/epub+zip", "passthrough-body");

            var client = scenario.BuildClient();
            var remoteBook = new RemoteBook
            {
                Release = new ReleaseInfo
                {
                    Guid = "Direct-Grab-NotApplicable",
                    Title = "Frank Herbert - Dune [epub]",
                    DownloadProtocol = DownloadProtocol.Direct,
                    DownloadUrl = "https://downloads.example/dune.epub",
                    Source = "MirrorIndex",
                    Container = "epub",
                    Size = 15
                }
            };
            var downloadId = await client.Download(remoteBook, indexer: null);

            var state = scenario.LoadState(downloadId);
            Assert.That(state.FallbackMode, Is.EqualTo(DirectDownloadFallbackMode.None));
            Assert.That(state.DownloadUrl, Is.EqualTo("https://downloads.example/dune.epub"));
            Assert.That(state.ResolvedUrl, Is.Null);

            await scenario.WaitForStatus(client, downloadId, DownloadItemStatus.Completed);
        }

        // ── Branch 5: Deferred Playwright does not overwrite durable source ──

        [Test]
        public async Task deferred_playwright_uses_transient_url_without_overwriting_durable_source()
        {
            using var scenario = new GrabFlowScenario();
            scenario.IndexerSettings.EnableSlowFallback = true;

            var transientUrls = new ConcurrentBag<string>();
            scenario.BrowserResolver.OnResolve = infoUrl =>
            {
                var transient = "https://slow.example/ephemeral-" + Guid.NewGuid().ToString("N")[..8] + ".epub";
                transientUrls.Add(transient);
                return transient;
            };

            scenario.Transport.AddRoute(
                url => url.StartsWith("https://slow.example/ephemeral-", StringComparison.OrdinalIgnoreCase),
                request => scenario.WriteBinaryResponse(request, "application/epub+zip", "transient-body"));

            var client = scenario.BuildClient();
            var downloadId = await client.Download(BuildCatalogRemoteBook(), scenario.CreateIndexer(apiKey: null));

            await scenario.WaitForStatus(client, downloadId, DownloadItemStatus.Completed);

            var state = scenario.LoadState(downloadId);
            Assert.That(state.DownloadUrl, Is.EqualTo(CatalogUrl),
                "Durable source URL must not be overwritten by transient Playwright URL");
            Assert.That(state.FallbackMode, Is.EqualTo(DirectDownloadFallbackMode.DeferredPlaywright));
        }

        // ── Branch 6: Deferred Playwright re-resolves on each retry ──

        [Test]
        public async Task deferred_playwright_re_resolves_on_retry_after_transient_failure()
        {
            using var scenario = new GrabFlowScenario();
            scenario.IndexerSettings.EnableSlowFallback = true;

            var resolveCount = 0;
            scenario.BrowserResolver.OnResolve = infoUrl =>
            {
                var count = Interlocked.Increment(ref resolveCount);
                return $"https://slow.example/retry-{count}.epub";
            };

            var attemptCount = 0;
            scenario.Transport.AddRoute(
                url => url.StartsWith("https://slow.example/retry-", StringComparison.OrdinalIgnoreCase),
                request =>
                {
                    var currentAttempt = Interlocked.Increment(ref attemptCount);
                    if (currentAttempt == 1)
                    {
                        throw new WebException("timeout", WebExceptionStatus.Timeout);
                    }

                    return scenario.WriteBinaryResponse(request, "application/epub+zip", "retried-body");
                });

            var client = scenario.BuildClient();
            var downloadId = await client.Download(BuildCatalogRemoteBook(), scenario.CreateIndexer(apiKey: null));

            await scenario.WaitForStatus(client, downloadId, DownloadItemStatus.Completed);

            Assert.That(resolveCount, Is.EqualTo(2), "Playwright should re-resolve on each attempt");
            Assert.That(attemptCount, Is.EqualTo(2));
        }

        // ── Branch 7: Deferred Playwright failure falls back to stored URL ──

        [Test]
        public async Task deferred_playwright_failure_falls_back_to_stored_url()
        {
            using var scenario = new GrabFlowScenario();
            scenario.IndexerSettings.EnableSlowFallback = true;
            scenario.BrowserResolver.ShouldFail = true;
            scenario.RegisterBinary(CatalogUrl, "application/epub+zip", "fallback-body");

            var client = scenario.BuildClient();
            var downloadId = await client.Download(BuildCatalogRemoteBook(), scenario.CreateIndexer(apiKey: null));

            await scenario.WaitForStatus(client, downloadId, DownloadItemStatus.Completed);
            Assert.That(File.ReadAllText(scenario.OutputPath(downloadId)), Is.EqualTo("fallback-body"));
        }

        // ── Branch 8: Stale DeferredPlaywright state persists across restart ──

        [Test]
        public async Task stale_deferred_playwright_state_preserves_mode_across_restart()
        {
            using var scenario = new GrabFlowScenario();
            scenario.BrowserResolver.SlowDownloadUrl = "https://slow.example/restart-resolved.epub";
            scenario.RegisterBinary("https://slow.example/restart-resolved.epub", "application/epub+zip", "restart-body");

            var downloadId = "DEFERRED-RESTART-TEST";
            var stateDir = Path.Combine(scenario.StagingFolder, $"client-42/{downloadId}");
            Directory.CreateDirectory(stateDir);
            File.WriteAllText(
                Path.Combine(stateDir, "direct-download-state.json"),
                scenario.SerializeState(downloadId, DownloadItemStatus.Downloading,
                    downloadUrl: CatalogUrl,
                    fallbackMode: DirectDownloadFallbackMode.DeferredPlaywright));

            var client = scenario.BuildClient();
            await scenario.WaitForStatus(client, downloadId, DownloadItemStatus.Completed);

            Assert.That(scenario.BrowserResolver.ResolveCalls, Is.GreaterThan(0),
                "Deferred Playwright should be invoked on restart for persisted DeferredPlaywright state");
            Assert.That(File.ReadAllText(scenario.OutputPath(downloadId)), Is.EqualTo("restart-body"));
        }

        // ── Branch 9: Post-grab failure affects only item status ──

        [Test]
        public async Task post_grab_download_failure_affects_only_item_status()
        {
            using var scenario = new GrabFlowScenario();
            scenario.RegisterFastDownloadApi(CatalogUrl, apiKey: "test-key-123", resolvedUrl: "https://cdn.example/will-fail.epub");
            scenario.Transport.AddRoute(
                url => url == "https://cdn.example/will-fail.epub",
                request => throw new WebException("connection refused", WebExceptionStatus.ConnectFailure));

            var client = scenario.BuildClient();
            var downloadId = await client.Download(BuildCatalogRemoteBook(), scenario.CreateIndexer(apiKey: "test-key-123"));

            await scenario.WaitForStatus(client, downloadId, DownloadItemStatus.Failed);

            var item = scenario.SingleItem(client, downloadId);
            Assert.That(item.Status, Is.EqualTo(DownloadItemStatus.Failed));
            Assert.That(item.Message, Does.Contain("attempts"));
        }

        // ── Branch 10: Null grab resolver passes through original URL ──

        [Test]
        public async Task null_grab_resolver_passes_through_original_url()
        {
            using var scenario = new GrabFlowScenario();
            scenario.RegisterBinary("https://downloads.example/dune.epub", "application/epub+zip", "no-resolver-body");

            var client = scenario.BuildClient(withGrabResolver: false);
            var remoteBook = new RemoteBook
            {
                Release = new ReleaseInfo
                {
                    Guid = "Direct-Grab-NullResolver",
                    Title = "Frank Herbert - Dune [epub]",
                    DownloadProtocol = DownloadProtocol.Direct,
                    DownloadUrl = "https://downloads.example/dune.epub",
                    Container = "epub",
                    Size = 16
                }
            };
            var downloadId = await client.Download(remoteBook, indexer: null);

            await scenario.WaitForStatus(client, downloadId, DownloadItemStatus.Completed);
            Assert.That(File.ReadAllText(scenario.OutputPath(downloadId)), Is.EqualTo("no-resolver-body"));
        }

        // ── Branch 11: Repeated interruptions then success with deferred mode ──

        [Test]
        public async Task repeated_interruptions_then_success_with_deferred_playwright()
        {
            using var scenario = new GrabFlowScenario();
            scenario.IndexerSettings.EnableSlowFallback = true;

            var resolveCount = 0;
            scenario.BrowserResolver.OnResolve = infoUrl =>
            {
                var count = Interlocked.Increment(ref resolveCount);
                return $"https://slow.example/interrupt-{count}.epub";
            };

            var attemptCount = 0;
            scenario.Transport.AddRoute(
                url => url.StartsWith("https://slow.example/interrupt-", StringComparison.OrdinalIgnoreCase),
                request =>
                {
                    var currentAttempt = Interlocked.Increment(ref attemptCount);
                    if (currentAttempt <= 2)
                    {
                        throw new WebException("connection reset", WebExceptionStatus.ReceiveFailure);
                    }

                    return scenario.WriteBinaryResponse(request, "application/epub+zip", "final-success-body");
                });

            var client = scenario.BuildClient();
            var downloadId = await client.Download(BuildCatalogRemoteBook(), scenario.CreateIndexer(apiKey: null));

            await scenario.WaitForStatus(client, downloadId, DownloadItemStatus.Completed);
            Assert.That(resolveCount, Is.EqualTo(3), "Playwright should re-resolve after each interruption");
            Assert.That(File.ReadAllText(scenario.OutputPath(downloadId)), Is.EqualTo("final-success-body"));
        }

        // ── Branch 12: API key present but API returns error → Unavailable with fallback ──

        [Test]
        public async Task api_error_with_fallback_enabled_defers_to_playwright()
        {
            using var scenario = new GrabFlowScenario();
            scenario.IndexerSettings.EnableSlowFallback = true;

            scenario.Transport.AddRoute(
                url => url.Contains("/dyn/api/fast_download.json", StringComparison.OrdinalIgnoreCase),
                request =>
                {
                    var headers = new HttpHeader();
                    headers.ContentType = "application/json";
                    var body = JsonSerializer.Serialize(new { error = "Invalid key" });
                    var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                    return Task.FromResult(new HttpResponse(request, headers, bytes, HttpStatusCode.OK));
                });

            scenario.BrowserResolver.SlowDownloadUrl = "https://slow.example/api-error-fallback.epub";
            scenario.RegisterBinary("https://slow.example/api-error-fallback.epub", "application/epub+zip", "api-error-fallback-body");

            var client = scenario.BuildClient();
            var downloadId = await client.Download(BuildCatalogRemoteBook(), scenario.CreateIndexer(apiKey: "bad-key"));

            var state = scenario.LoadState(downloadId);
            Assert.That(state.FallbackMode, Is.EqualTo(DirectDownloadFallbackMode.DeferredPlaywright));

            await scenario.WaitForStatus(client, downloadId, DownloadItemStatus.Completed);
            Assert.That(File.ReadAllText(scenario.OutputPath(downloadId)), Is.EqualTo("api-error-fallback-body"));
        }

        // ── Branch 13: Malformed API JSON + fallback enabled → DeferredPlaywright ──

        [Test]
        public async Task malformed_api_json_with_fallback_enabled_defers_to_playwright()
        {
            using var scenario = new GrabFlowScenario();
            scenario.IndexerSettings.EnableSlowFallback = true;

            // API returns garbage JSON — the resolver swallows the parse error and reports Unavailable.
            scenario.Transport.AddRoute(
                url => url.Contains("/dyn/api/fast_download.json", StringComparison.OrdinalIgnoreCase),
                request =>
                {
                    var headers = new HttpHeader();
                    headers.ContentType = "application/json";
                    var bytes = System.Text.Encoding.UTF8.GetBytes("{ not valid json !!! }");
                    return Task.FromResult(new HttpResponse(request, headers, bytes, HttpStatusCode.OK));
                });

            scenario.BrowserResolver.SlowDownloadUrl = "https://slow.example/malformed-fallback.epub";
            scenario.RegisterBinary("https://slow.example/malformed-fallback.epub", "application/epub+zip", "malformed-fallback-body");

            var client = scenario.BuildClient();
            var downloadId = await client.Download(BuildCatalogRemoteBook(), scenario.CreateIndexer(apiKey: "test-key-123"));

            var state = scenario.LoadState(downloadId);
            Assert.That(state.FallbackMode, Is.EqualTo(DirectDownloadFallbackMode.DeferredPlaywright));
            Assert.That(state.DownloadUrl, Is.EqualTo(CatalogUrl), "Durable URL must be the original catalog URL");
            Assert.That(state.ResolvedUrl, Is.Null, "ResolvedUrl must be null when API returned malformed JSON");

            await scenario.WaitForStatus(client, downloadId, DownloadItemStatus.Completed);
            Assert.That(File.ReadAllText(scenario.OutputPath(downloadId)), Is.EqualTo("malformed-fallback-body"));
        }

        // ── Branch 14: Malformed API JSON + fallback disabled → exception, no state ──

        [Test]
        public void malformed_api_json_with_fallback_disabled_throws_and_creates_no_state()
        {
            using var scenario = new GrabFlowScenario();
            scenario.IndexerSettings.EnableSlowFallback = false;

            scenario.Transport.AddRoute(
                url => url.Contains("/dyn/api/fast_download.json", StringComparison.OrdinalIgnoreCase),
                request =>
                {
                    var headers = new HttpHeader();
                    headers.ContentType = "application/json";
                    var bytes = System.Text.Encoding.UTF8.GetBytes("{ not valid json !!! }");
                    return Task.FromResult(new HttpResponse(request, headers, bytes, HttpStatusCode.OK));
                });

            var client = scenario.BuildClient();

            var ex = Assert.ThrowsAsync<ReleaseDownloadException>(
                async () => await client.Download(BuildCatalogRemoteBook(), scenario.CreateIndexer(apiKey: "test-key-123")));

            Assert.That(ex.Message, Does.Contain("browser fallback is disabled"));
            Assert.That(client.GetItems(), Is.Empty, "No state should be created on grab failure");
        }

        // ── Branch 15: Browser resolver throws during deferred transfer → falls back to stored URL ──

        [Test]
        public async Task browser_resolver_exception_during_deferred_transfer_fails_after_retries()
        {
            using var scenario = new GrabFlowScenario();
            scenario.IndexerSettings.EnableSlowFallback = true;
            scenario.BrowserResolver.OnResolve = infoUrl => throw new InvalidOperationException("Browser crashed");

            var client = scenario.BuildClient();
            var downloadId = await client.Download(BuildCatalogRemoteBook(), scenario.CreateIndexer(apiKey: null));

            var state = scenario.LoadState(downloadId);
            Assert.That(state.FallbackMode, Is.EqualTo(DirectDownloadFallbackMode.DeferredPlaywright));
            Assert.That(state.DownloadUrl, Is.EqualTo(CatalogUrl));

            await scenario.WaitForStatus(client, downloadId, DownloadItemStatus.Failed);
            var item = scenario.SingleItem(client, downloadId);
            Assert.That(item.Status, Is.EqualTo(DownloadItemStatus.Failed));
            Assert.That(item.Message, Does.Contain("Browser crashed"));
        }

        // ── Branch 16: Dirty worktree (stale .part) + DeferredPlaywright restart → recovers ──

        [Test]
        public async Task dirty_worktree_with_deferred_playwright_state_recovers_on_restart()
        {
            using var scenario = new GrabFlowScenario();
            scenario.BrowserResolver.SlowDownloadUrl = "https://slow.example/dirty-recover.epub";
            scenario.RegisterBinary("https://slow.example/dirty-recover.epub", "application/epub+zip", "recovered-body");

            var downloadId = "DIRTY-WORKTREE-TEST";
            var stateDir = Path.Combine(scenario.StagingFolder, $"client-42/{downloadId}");
            Directory.CreateDirectory(stateDir);

            // Leave a stale .part file from a prior interrupted attempt.
            File.WriteAllText(
                Path.Combine(stateDir, "Frank Herbert - Dune [epub].epub.part"),
                "stale-partial-data-from-crash");

            File.WriteAllText(
                Path.Combine(stateDir, "direct-download-state.json"),
                scenario.SerializeState(downloadId, DownloadItemStatus.Downloading,
                    downloadUrl: CatalogUrl,
                    fallbackMode: DirectDownloadFallbackMode.DeferredPlaywright));

            var client = scenario.BuildClient();
            await scenario.WaitForStatus(client, downloadId, DownloadItemStatus.Completed);

            Assert.That(scenario.BrowserResolver.ResolveCalls, Is.GreaterThan(0),
                "Browser should be invoked for DeferredPlaywright recovery");
            Assert.That(File.ReadAllText(scenario.OutputPath(downloadId)), Is.EqualTo("recovered-body"),
                "Downloaded file should contain fresh content, not stale partial data");

            // Stale .part file should have been cleaned up.
            Assert.That(File.Exists(Path.Combine(stateDir, "Frank Herbert - Dune [epub].epub.part")), Is.False,
                "Stale .part file should be removed after successful completion");
        }

        // ── Branch 17: Multiple concurrent downloads are isolated ──

        [Test]
        public async Task multiple_concurrent_downloads_are_isolated_when_one_fails()
        {
            using var scenario = new GrabFlowScenario();

            // First download will fail permanently.
            scenario.Transport.AddRoute(
                url => url == "https://cdn.example/will-fail.epub",
                request => throw new WebException("connection refused", WebExceptionStatus.ConnectFailure));

            scenario.RegisterFastDownloadApi(CatalogUrl, apiKey: "test-key-123", resolvedUrl: "https://cdn.example/will-fail.epub");

            // Second download uses a completely different catalog URL and will succeed.
            var successCatalogUrl = "https://catalog.example/md5/fff111aaa222bbb333ccc444ddd555ee";
            scenario.RegisterFastDownloadApi(successCatalogUrl, apiKey: "test-key-123", resolvedUrl: "https://cdn.example/success.epub");
            scenario.RegisterBinary("https://cdn.example/success.epub", "application/epub+zip", "success-body");

            var client = scenario.BuildClient();

            // Grab both.
            var failId = await client.Download(BuildCatalogRemoteBook(), scenario.CreateIndexer(apiKey: "test-key-123"));

            var successRemoteBook = new RemoteBook
            {
                Release = new ReleaseInfo
                {
                    Guid = "Direct-Grab-Flow-Success",
                    Title = "Frank Herbert - Dune [epub]",
                    Author = "Frank Herbert",
                    Book = "Dune",
                    DownloadProtocol = DownloadProtocol.Direct,
                    DownloadUrl = successCatalogUrl,
                    Source = "CatalogPage",
                    Container = "epub",
                    Size = 14
                }
            };
            var successId = await client.Download(successRemoteBook, scenario.CreateIndexer(apiKey: "test-key-123"));

            // Wait for the failing one to exhaust retries.
            await scenario.WaitForStatus(client, failId, DownloadItemStatus.Failed);

            // The successful download must complete independently.
            await scenario.WaitForStatus(client, successId, DownloadItemStatus.Completed);

            var failedItem = scenario.SingleItem(client, failId);
            Assert.That(failedItem.Status, Is.EqualTo(DownloadItemStatus.Failed));

            var successItem = scenario.SingleItem(client, successId);
            Assert.That(successItem.Status, Is.EqualTo(DownloadItemStatus.Completed));
            Assert.That(File.ReadAllText(scenario.OutputPath(successId)), Is.EqualTo("success-body"));
        }

        // ── Branch 18: RemoveItem cleans state and filesystem ──

        [Test]
        public async Task remove_item_cleans_state_file_and_directory()
        {
            using var scenario = new GrabFlowScenario();
            scenario.RegisterFastDownloadApi(CatalogUrl, apiKey: "test-key-123", resolvedUrl: "https://cdn.example/cleanup-test.epub");
            scenario.RegisterBinary("https://cdn.example/cleanup-test.epub", "application/epub+zip", "cleanup-body");

            var client = scenario.BuildClient();
            var downloadId = await client.Download(BuildCatalogRemoteBook(), scenario.CreateIndexer(apiKey: "test-key-123"));

            await scenario.WaitForStatus(client, downloadId, DownloadItemStatus.Completed);

            var stateDir = Path.Combine(scenario.StagingFolder, $"client-42/{downloadId}");
            Assert.That(Directory.Exists(stateDir), Is.True, "State directory should exist before removal");

            var item = scenario.SingleItem(client, downloadId);
            client.RemoveItem(item, deleteData: true);

            Assert.That(scenario.ContainsItem(client, downloadId), Is.False, "Item should be removed from GetItems");
            Assert.That(Directory.Exists(stateDir), Is.False, "State directory should be cleaned up after removal with deleteData=true");
        }

        // ── Branch 19: API key present but API returns empty JSON object → Unavailable with fallback ──

        [Test]
        public async Task api_returns_empty_json_object_with_fallback_enabled_defers_to_playwright()
        {
            using var scenario = new GrabFlowScenario();
            scenario.IndexerSettings.EnableSlowFallback = true;

            scenario.Transport.AddRoute(
                url => url.Contains("/dyn/api/fast_download.json", StringComparison.OrdinalIgnoreCase),
                request =>
                {
                    var headers = new HttpHeader();
                    headers.ContentType = "application/json";
                    var bytes = System.Text.Encoding.UTF8.GetBytes("{}");
                    return Task.FromResult(new HttpResponse(request, headers, bytes, HttpStatusCode.OK));
                });

            scenario.BrowserResolver.SlowDownloadUrl = "https://slow.example/empty-json-fallback.epub";
            scenario.RegisterBinary("https://slow.example/empty-json-fallback.epub", "application/epub+zip", "empty-json-fallback-body");

            var client = scenario.BuildClient();
            var downloadId = await client.Download(BuildCatalogRemoteBook(), scenario.CreateIndexer(apiKey: "test-key-123"));

            var state = scenario.LoadState(downloadId);
            Assert.That(state.FallbackMode, Is.EqualTo(DirectDownloadFallbackMode.DeferredPlaywright));
            Assert.That(state.ResolvedUrl, Is.Null, "ResolvedUrl must be null when API returned no download_url");

            await scenario.WaitForStatus(client, downloadId, DownloadItemStatus.Completed);
            Assert.That(File.ReadAllText(scenario.OutputPath(downloadId)), Is.EqualTo("empty-json-fallback-body"));
        }

        // ── Branch 20: RemoveItem with deleteData=false preserves file ──

        [Test]
        public async Task remove_item_without_delete_data_preserves_output_file()
        {
            using var scenario = new GrabFlowScenario();
            scenario.RegisterFastDownloadApi(CatalogUrl, apiKey: "test-key-123", resolvedUrl: "https://cdn.example/preserve-test.epub");
            scenario.RegisterBinary("https://cdn.example/preserve-test.epub", "application/epub+zip", "preserve-body");

            var client = scenario.BuildClient();
            var downloadId = await client.Download(BuildCatalogRemoteBook(), scenario.CreateIndexer(apiKey: "test-key-123"));

            await scenario.WaitForStatus(client, downloadId, DownloadItemStatus.Completed);

            var outputPath = scenario.OutputPath(downloadId);
            Assert.That(File.Exists(outputPath), Is.True, "Output file should exist before removal");

            var item = scenario.SingleItem(client, downloadId);
            client.RemoveItem(item, deleteData: false);

            Assert.That(File.Exists(outputPath), Is.True, "Output file should be preserved when deleteData=false");
            Assert.That(scenario.ContainsItem(client, downloadId), Is.False, "Item should be removed from tracking even with deleteData=false");
        }

        // ── Helpers ──

        private static RemoteBook BuildCatalogRemoteBook()
        {
            return new RemoteBook
            {
                Release = new ReleaseInfo
                {
                    Guid = "Direct-Grab-Flow-Test",
                    Title = "Frank Herbert - Dune [epub]",
                    Author = "Frank Herbert",
                    Book = "Dune",
                    DownloadProtocol = DownloadProtocol.Direct,
                    DownloadUrl = CatalogUrl,
                    Source = "CatalogPage",
                    Container = "epub",
                    Size = 14
                }
            };
        }

        // ── Scenario harness ──

        private sealed class GrabFlowScenario : IDisposable
        {
            public GrabFlowScenario()
            {
                StagingFolder = Path.Combine(Path.GetTempPath(), "chaptarr-grab-flow-tests", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(StagingFolder);
                Transport = new Chaptarr.Core.Test.Indexers.DirectDownloadTestHttp();
                BrowserResolver = new StubBrowserResolver();
                IndexerSettings = new DirectDownloadSettings { EnableSlowFallback = false };
            }

            public string StagingFolder { get; }
            public Chaptarr.Core.Test.Indexers.DirectDownloadTestHttp Transport { get; }
            public StubBrowserResolver BrowserResolver { get; }
            public DirectDownloadSettings IndexerSettings { get; }

            public DirectDownloadClient BuildClient(bool withGrabResolver = true)
            {
                DirectDownloadGrabUrlResolver grabResolver = null;
                if (withGrabResolver)
                {
                    grabResolver = new DirectDownloadGrabUrlResolver(Transport.CreateClient(), BrowserResolver);
                }

                return new DirectDownloadClient(
                    Transport.CreateClient(),
                    new TestDiskProvider(),
                    null,
                    LogManager.GetCurrentClassLogger(),
                    grabUrlResolver: grabResolver,
                    browserResolver: BrowserResolver)
                {
                    Definition = new DownloadClientDefinition
                    {
                        Id = 42,
                        Name = "Direct Download",
                        Protocol = DownloadProtocol.Direct,
                        Settings = new DirectDownloadClientSettings { StagingFolder = StagingFolder }
                    }
                };
            }

            public IIndexer CreateIndexer(string apiKey = null)
            {
                IndexerSettings.ApiKey = apiKey;
                return new StubIndexer(IndexerSettings);
            }

            public void RegisterBinary(string url, string contentType, string body)
            {
                Transport.AddRoute(candidate => candidate == url, request => WriteBinaryResponse(request, contentType, body));
            }

            public void RegisterFastDownloadApi(string catalogUrl, string apiKey, string resolvedUrl)
            {
                var md5 = ExtractMd5FromUrl(catalogUrl);

                Transport.AddRoute(
                    url => url.Contains("/dyn/api/fast_download.json", StringComparison.OrdinalIgnoreCase) &&
                           url.Contains($"md5={md5}", StringComparison.OrdinalIgnoreCase) &&
                           url.Contains($"key={Uri.EscapeDataString(apiKey)}", StringComparison.OrdinalIgnoreCase),
                    request =>
                    {
                        var headers = new HttpHeader();
                        headers.ContentType = "application/json";
                        var body = JsonSerializer.Serialize(new { download_url = resolvedUrl });
                        var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                        return Task.FromResult(new HttpResponse(request, headers, bytes, HttpStatusCode.OK));
                    });
            }

            public Task<HttpResponse> WriteBinaryResponse(HttpRequest request, string contentType, string body)
            {
                var headers = new HttpHeader();
                headers.ContentType = contentType;
                var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                return WriteResponseAsync(request, headers, bytes);
            }

            public DirectDownloadClientState LoadState(string downloadId)
            {
                var statePath = Path.Combine(StagingFolder, $"client-42/{downloadId}/direct-download-state.json");
                for (var attempt = 0; attempt < 10; attempt++)
                {
                    try
                    {
                        using var stream = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var reader = new StreamReader(stream);
                        var json = reader.ReadToEnd();
                        return STJson.Deserialize<DirectDownloadClientState>(json);
                    }
                    catch (IOException) when (attempt < 9)
                    {
                        Thread.Sleep(50);
                    }
                }

                Assert.Fail($"Could not read state file for {downloadId} after retries");
                return null;
            }

            public string OutputPath(string downloadId)
            {
                return Path.Combine(StagingFolder, $"client-42/{downloadId}/Frank Herbert - Dune [epub].epub");
            }

            public async Task WaitForStatus(DirectDownloadClient client, string downloadId, DownloadItemStatus status, int timeoutSeconds = 30)
            {
                var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
                while (DateTime.UtcNow < deadline)
                {
                    var item = SingleItem(client, downloadId, throwWhenMissing: false);
                    if (item != null && item.Status == status)
                    {
                        return;
                    }

                    _ = client.GetItems();
                    await Task.Delay(50);
                }

                Assert.Fail($"Timed out waiting for {status} for {downloadId}");
            }

            public DownloadClientItem WaitForItem(DirectDownloadClient client, string downloadId)
            {
                for (var i = 0; i < 40; i++)
                {
                    foreach (var item in client.GetItems())
                    {
                        if (item.DownloadId == downloadId)
                        {
                            return item;
                        }
                    }

                    Thread.Sleep(25);
                }

                Assert.Fail($"Download item '{downloadId}' was not found.");
                return null;
            }

            public DownloadClientItem SingleItem(DirectDownloadClient client, string downloadId, bool throwWhenMissing = true)
            {
                foreach (var item in client.GetItems())
                {
                    if (item.DownloadId == downloadId)
                    {
                        return item;
                    }
                }

                if (throwWhenMissing)
                {
                    Assert.Fail($"Download item '{downloadId}' was not found.");
                }

                return null;
            }

            public bool ContainsItem(DirectDownloadClient client, string downloadId)
            {
                foreach (var item in client.GetItems())
                {
                    if (item.DownloadId == downloadId)
                    {
                        return true;
                    }
                }

                return false;
            }

            public string SerializeState(string downloadId, DownloadItemStatus status,
                string downloadUrl = null, DirectDownloadFallbackMode fallbackMode = DirectDownloadFallbackMode.None)
            {
                var url = downloadUrl ?? "https://downloads.example/dune.epub";
                var outputPath = Path.Combine(StagingFolder, $"client-42/{downloadId}/Frank Herbert - Dune [epub].epub");
                var partPath = outputPath + ".part";
                return $"{{\"downloadId\":\"{downloadId}\",\"title\":\"Frank Herbert - Dune [epub]\",\"downloadUrl\":\"{url}\"," +
                       $"\"status\":{(int)status},\"fallbackMode\":{(int)fallbackMode}," +
                       $"\"outputFilePath\":\"{outputPath.Replace("\\", "\\\\")}\",\"partFilePath\":\"{partPath.Replace("\\", "\\\\")}\"," +
                       $"\"createdAtUtc\":\"{DateTime.UtcNow:O}\",\"updatedAtUtc\":\"{DateTime.UtcNow:O}\"}}";
            }

            public void Dispose()
            {
                if (Directory.Exists(StagingFolder))
                {
                    Directory.Delete(StagingFolder, recursive: true);
                }
            }

            private static string ExtractMd5FromUrl(string catalogUrl)
            {
                var marker = "/md5/";
                var index = catalogUrl.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                {
                    return null;
                }

                return catalogUrl.Substring(index + marker.Length, 32);
            }

            private static async Task<HttpResponse> WriteResponseAsync(HttpRequest request, HttpHeader headers, byte[] bytes)
            {
                if (request.ResponseStream != null)
                {
                    await request.ResponseStream.WriteAsync(bytes, 0, bytes.Length);
                }

                return new HttpResponse(request, headers, Array.Empty<byte>(), HttpStatusCode.OK);
            }
        }

        // ── Stubs ──

        public sealed class StubBrowserResolver : IBrowserDownloadResolver
        {
            public string SlowDownloadUrl { get; set; }
            public bool ShouldFail { get; set; }
            public Func<string, string> OnResolve { get; set; }
            public int ResolveCalls;

            public Task<bool> IsAvailableAsync() => Task.FromResult(!ShouldFail);

            public Task<string> TryResolveSlowDownloadUrlAsync(string infoUrl)
            {
                Interlocked.Increment(ref ResolveCalls);

                if (ShouldFail)
                {
                    return Task.FromResult<string>(null);
                }

                if (OnResolve != null)
                {
                    return Task.FromResult(OnResolve(infoUrl));
                }

                return Task.FromResult(SlowDownloadUrl);
            }
        }

        private sealed class StubIndexer : IIndexer
        {
            private IndexerDefinition _definition;

            public StubIndexer(DirectDownloadSettings settings)
            {
                _definition = new IndexerDefinition { Settings = settings };
            }

            public string Name => "Stub Direct";
            public IndexerDefinition Definition { get => _definition; set => _definition = value; }
            public bool SupportsRss => false;
            public bool SupportsSearch => false;
            public DownloadProtocol Protocol => DownloadProtocol.Direct;
            public Type ConfigContract => typeof(DirectDownloadSettings);
            public ProviderMessage Message => null;
            public IEnumerable<ProviderDefinition> DefaultDefinitions => Array.Empty<ProviderDefinition>();
            ProviderDefinition IProvider.Definition { get => _definition; set => _definition = (IndexerDefinition)value; }
            public ValidationResult Test() => new();
            public object RequestAction(string stage, IDictionary<string, string> query) => null;
            public Task<IList<ReleaseInfo>> FetchRecent() => Task.FromResult<IList<ReleaseInfo>>(Array.Empty<ReleaseInfo>());
            public Task<IList<ReleaseInfo>> Fetch(BookSearchCriteria searchCriteria) => Task.FromResult<IList<ReleaseInfo>>(Array.Empty<ReleaseInfo>());
            public Task<IList<ReleaseInfo>> Fetch(AuthorSearchCriteria searchCriteria) => Task.FromResult<IList<ReleaseInfo>>(Array.Empty<ReleaseInfo>());
            public HttpRequest GetDownloadRequest(string link) => new HttpRequest(link);
            public Task<HttpResponse> ExecuteDownloadRequestAsync(HttpRequest request) => throw new NotImplementedException();
        }

        private sealed class TestDiskProvider : DiskProviderBase
        {
            public TestDiskProvider()
                : base(new System.IO.Abstractions.FileSystem())
            {
            }

            public override long? GetAvailableSpace(string path) => null;
            public override void InheritFolderPermissions(string filename) { }
            public override void SetEveryonePermissions(string filename) { }
            public override void SetFilePermissions(string path, string mask, string group) { }
            public override void SetPermissions(string path, string mask, string group) { }
            public override void CopyPermissions(string sourcePath, string targetPath) { }
            public override bool TryCreateHardLink(string source, string destination) => false;
            public override long? GetTotalSize(string path) => null;
        }
    }
}
