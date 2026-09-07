using System;
using System.IO;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients.Direct;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class DirectDownloadClientStatePersistenceFixture
    {
        private string _tempFolder;
        private DirectDownloadClientStateStore _store;
        private const int ClientId = 1;

        [SetUp]
        public void SetUp()
        {
            _tempFolder = Path.Combine(Path.GetTempPath(), "chaptarr-state-persistence-test", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempFolder);
            _store = new DirectDownloadClientStateStore(new TestDiskProvider(), LogManager.GetCurrentClassLogger());
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempFolder))
            {
                Directory.Delete(_tempFolder, recursive: true);
            }
        }

        // ── Baseline characterization: existing fields round-trip through JSON ──

        [Test]
        public void should_round_trip_existing_state_fields_through_json()
        {
            var state = new DirectDownloadClientState
            {
                DownloadId = "direct-test-001",
                Title = "Isaac Asimov - Foundation [epub]",
                DownloadUrl = "https://downloads.example/foundation.epub",
                Status = DownloadItemStatus.Downloading,
                OutputFilePath = "/staging/client-1/direct-test-001/Foundation.epub",
                PartFilePath = "/staging/client-1/direct-test-001/Foundation.epub.part",
                Message = "Retrying after error (attempt 2/3): timeout",
                TotalSize = 1024000,
                DownloadedBytes = 512000,
                AttemptCount = 2,
                CreatedAtUtc = new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc),
                UpdatedAtUtc = new DateTime(2025, 1, 15, 10, 5, 0, DateTimeKind.Utc)
            };

            var json = state.ToJson();
            var deserialized = Json.Deserialize<DirectDownloadClientState>(json);

            Assert.That(deserialized.DownloadId, Is.EqualTo("direct-test-001"));
            Assert.That(deserialized.Title, Is.EqualTo("Isaac Asimov - Foundation [epub]"));
            Assert.That(deserialized.DownloadUrl, Is.EqualTo("https://downloads.example/foundation.epub"));
            Assert.That(deserialized.Status, Is.EqualTo(DownloadItemStatus.Downloading));
            Assert.That(deserialized.OutputFilePath, Is.EqualTo("/staging/client-1/direct-test-001/Foundation.epub"));
            Assert.That(deserialized.PartFilePath, Is.EqualTo("/staging/client-1/direct-test-001/Foundation.epub.part"));
            Assert.That(deserialized.Message, Is.EqualTo("Retrying after error (attempt 2/3): timeout"));
            Assert.That(deserialized.TotalSize, Is.EqualTo(1024000));
            Assert.That(deserialized.DownloadedBytes, Is.EqualTo(512000));
            Assert.That(deserialized.AttemptCount, Is.EqualTo(2));
            Assert.That(deserialized.CreatedAtUtc, Is.EqualTo(new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc)));
            Assert.That(deserialized.UpdatedAtUtc, Is.EqualTo(new DateTime(2025, 1, 15, 10, 5, 0, DateTimeKind.Utc)));
        }

        [Test]
        public void should_serialize_enum_status_as_camel_case_string()
        {
            var state = new DirectDownloadClientState
            {
                DownloadId = "enum-check",
                Status = DownloadItemStatus.Completed,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };

            var json = state.ToJson();

            Assert.That(json, Does.Contain("\"status\": \"completed\""));
            Assert.That(json, Does.Not.Contain("\"status\": \"Completed\""));
            Assert.That(json, Does.Not.Match("\"status\":\\s*\\d"));
        }

        [Test]
        public void should_omit_null_fields_in_serialized_json()
        {
            var state = new DirectDownloadClientState
            {
                DownloadId = "null-check",
                Status = DownloadItemStatus.Queued,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };

            var json = state.ToJson();

            Assert.That(json, Does.Not.Contain("\"message\""));
            Assert.That(json, Does.Not.Contain("\"importedAtUtc\""));
        }

        [Test]
        public void should_deserialize_json_with_missing_optional_fields_as_defaults()
        {
            var json = "{\"downloadId\":\"sparse\",\"title\":\"Test\",\"downloadUrl\":\"https://example.com/file.epub\",\"status\":\"queued\",\"createdAtUtc\":\"2025-01-01T00:00:00Z\",\"updatedAtUtc\":\"2025-01-01T00:00:00Z\"}";

            var state = Json.Deserialize<DirectDownloadClientState>(json);

            Assert.That(state.DownloadId, Is.EqualTo("sparse"));
            Assert.That(state.Status, Is.EqualTo(DownloadItemStatus.Queued));
            Assert.That(state.TotalSize, Is.EqualTo(0));
            Assert.That(state.DownloadedBytes, Is.EqualTo(0));
            Assert.That(state.AttemptCount, Is.EqualTo(0));
            Assert.That(state.Message, Is.Null);
            Assert.That(state.ImportedAtUtc, Is.Null);
        }

        // ── Backward compatibility: old JSON without new fields ──

        [Test]
        public void should_deserialize_legacy_json_without_resolved_url_field()
        {
            // Simulates a state file written before ResolvedUrl was added.
            var json = "{\"downloadId\":\"legacy-001\",\"title\":\"Legacy Book\",\"downloadUrl\":\"https://old.example/book.epub\",\"status\":\"downloading\",\"outputFilePath\":\"/out/book.epub\",\"partFilePath\":\"/out/book.epub.part\",\"totalSize\":500,\"downloadedBytes\":100,\"attemptCount\":1,\"createdAtUtc\":\"2025-01-01T00:00:00Z\",\"updatedAtUtc\":\"2025-01-01T00:01:00Z\"}";

            var state = Json.Deserialize<DirectDownloadClientState>(json);

            Assert.That(state.DownloadId, Is.EqualTo("legacy-001"));
            Assert.That(state.ResolvedUrl, Is.Null, "Legacy JSON without resolvedUrl should deserialize to null");
            Assert.That(state.FallbackMode, Is.EqualTo(DirectDownloadFallbackMode.None), "Legacy JSON without fallbackMode should default to None");
        }

        // ── New field: ResolvedUrl round-trip ──

        [Test]
        public void should_round_trip_resolved_url_through_json()
        {
            var state = new DirectDownloadClientState
            {
                DownloadId = "resolved-url-001",
                Title = "Book With Resolved URL",
                DownloadUrl = "https://catalog.example/md5/abc123",
                ResolvedUrl = "https://cdn.example/abc123/book.epub?key=token123",
                Status = DownloadItemStatus.Downloading,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };

            var json = state.ToJson();
            var deserialized = Json.Deserialize<DirectDownloadClientState>(json);

            Assert.That(deserialized.DownloadUrl, Is.EqualTo("https://catalog.example/md5/abc123"), "Original URL should be preserved");
            Assert.That(deserialized.ResolvedUrl, Is.EqualTo("https://cdn.example/abc123/book.epub?key=token123"), "API-resolved URL should survive round-trip");
        }

        [Test]
        public void should_serialize_resolved_url_in_camel_case()
        {
            var state = new DirectDownloadClientState
            {
                DownloadId = "camel-check",
                ResolvedUrl = "https://cdn.example/file.epub",
                Status = DownloadItemStatus.Queued,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };

            var json = state.ToJson();

            Assert.That(json, Does.Contain("\"resolvedUrl\""));
            Assert.That(json, Does.Not.Contain("\"ResolvedUrl\""));
        }

        [Test]
        public void should_omit_resolved_url_when_null()
        {
            var state = new DirectDownloadClientState
            {
                DownloadId = "omit-null",
                ResolvedUrl = null,
                Status = DownloadItemStatus.Queued,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };

            var json = state.ToJson();

            Assert.That(json, Does.Not.Contain("resolvedUrl"));
        }

        // ── New field: FallbackMode round-trip ──

        [Test]
        public void should_round_trip_deferred_playwright_fallback_mode_through_json()
        {
            var state = new DirectDownloadClientState
            {
                DownloadId = "fallback-001",
                Title = "Book Needing Browser Fallback",
                DownloadUrl = "https://catalog.example/md5/def456",
                ResolvedUrl = null,
                FallbackMode = DirectDownloadFallbackMode.DeferredPlaywright,
                Status = DownloadItemStatus.Downloading,
                Message = "API resolution failed, browser fallback deferred",
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };

            var json = state.ToJson();
            var deserialized = Json.Deserialize<DirectDownloadClientState>(json);

            Assert.That(deserialized.FallbackMode, Is.EqualTo(DirectDownloadFallbackMode.DeferredPlaywright));
            Assert.That(deserialized.ResolvedUrl, Is.Null);
        }

        [Test]
        public void should_serialize_fallback_mode_as_camel_case_string()
        {
            var state = new DirectDownloadClientState
            {
                DownloadId = "enum-fallback",
                FallbackMode = DirectDownloadFallbackMode.DeferredPlaywright,
                Status = DownloadItemStatus.Queued,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };

            var json = state.ToJson();

            Assert.That(json, Does.Contain("\"deferredPlaywright\""));
            Assert.That(json, Does.Not.Contain("\"DeferredPlaywright\""));
            Assert.That(json, Does.Not.Match("\"fallbackMode\":\\s*\\d"));
        }

        [Test]
        public void should_default_fallback_mode_to_none_for_missing_field()
        {
            var json = "{\"downloadId\":\"no-fallback\",\"status\":\"queued\",\"createdAtUtc\":\"2025-01-01T00:00:00Z\",\"updatedAtUtc\":\"2025-01-01T00:00:00Z\"}";

            var state = Json.Deserialize<DirectDownloadClientState>(json);

            Assert.That(state.FallbackMode, Is.EqualTo(DirectDownloadFallbackMode.None));
        }

        // ── State store integration: save/load with new fields ──

        [Test]
        public void should_persist_resolved_url_and_fallback_mode_through_state_store()
        {
            var state = new DirectDownloadClientState
            {
                DownloadId = "store-integration-001",
                Title = "Persisted Book",
                DownloadUrl = "https://info.example/md5/aaa111",
                ResolvedUrl = "https://cdn.example/aaa111/book.epub",
                FallbackMode = DirectDownloadFallbackMode.DeferredPlaywright,
                Status = DownloadItemStatus.Downloading,
                OutputFilePath = Path.Combine(_tempFolder, "client-1/store-integration-001/Book.epub"),
                PartFilePath = Path.Combine(_tempFolder, "client-1/store-integration-001/Book.epub.part"),
                TotalSize = 2048000,
                DownloadedBytes = 1024000,
                AttemptCount = 1,
                CreatedAtUtc = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc),
                UpdatedAtUtc = new DateTime(2025, 6, 1, 12, 5, 0, DateTimeKind.Utc)
            };

            _store.Save(_tempFolder, ClientId, state);

            var loaded = _store.Find(_tempFolder, ClientId, "store-integration-001");

            Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded.DownloadId, Is.EqualTo("store-integration-001"));
            Assert.That(loaded.DownloadUrl, Is.EqualTo("https://info.example/md5/aaa111"));
            Assert.That(loaded.ResolvedUrl, Is.EqualTo("https://cdn.example/aaa111/book.epub"));
            Assert.That(loaded.FallbackMode, Is.EqualTo(DirectDownloadFallbackMode.DeferredPlaywright));
            Assert.That(loaded.TotalSize, Is.EqualTo(2048000));
            Assert.That(loaded.DownloadedBytes, Is.EqualTo(1024000));
        }

        [Test]
        public void should_load_legacy_state_file_with_new_fields_defaulting()
        {
            // Write a legacy JSON state file directly (no ResolvedUrl / FallbackMode)
            var downloadId = "legacy-store-001";
            var stateDir = Path.Combine(_tempFolder, $"client-{ClientId}", downloadId);
            Directory.CreateDirectory(stateDir);
            var stateFile = Path.Combine(stateDir, "direct-download-state.json");

            var legacyJson = $"{{\"downloadId\":\"{downloadId}\",\"title\":\"Legacy State\",\"downloadUrl\":\"https://old.example/book.epub\",\"status\":\"completed\",\"outputFilePath\":\"{Path.Combine(stateDir, "Book.epub").Replace("\\", "\\\\")}\",\"totalSize\":4096,\"downloadedBytes\":4096,\"createdAtUtc\":\"2025-03-01T00:00:00Z\",\"updatedAtUtc\":\"2025-03-01T00:01:00Z\"}}";
            File.WriteAllText(stateFile, legacyJson);

            var loaded = _store.Find(_tempFolder, ClientId, downloadId);

            Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded.DownloadId, Is.EqualTo(downloadId));
            Assert.That(loaded.ResolvedUrl, Is.Null, "Legacy file should not have ResolvedUrl");
            Assert.That(loaded.FallbackMode, Is.EqualTo(DirectDownloadFallbackMode.None), "Legacy file should default FallbackMode to None");
            Assert.That(loaded.Status, Is.EqualTo(DownloadItemStatus.Completed));
        }

        [Test]
        public void should_load_all_states_including_new_fields_across_multiple_downloads()
        {
            // State 1: no new fields
            var state1 = new DirectDownloadClientState
            {
                DownloadId = "multi-001",
                Title = "No Resolved URL",
                DownloadUrl = "https://example.com/a.epub",
                Status = DownloadItemStatus.Completed,
                OutputFilePath = Path.Combine(_tempFolder, "client-1/multi-001/a.epub"),
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };

            // State 2: with resolved URL
            var state2 = new DirectDownloadClientState
            {
                DownloadId = "multi-002",
                Title = "Has Resolved URL",
                DownloadUrl = "https://catalog.example/md5/bbb222",
                ResolvedUrl = "https://cdn.example/bbb222/book.epub",
                FallbackMode = DirectDownloadFallbackMode.DeferredPlaywright,
                Status = DownloadItemStatus.Downloading,
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(1),
                UpdatedAtUtc = DateTime.UtcNow.AddMinutes(1)
            };

            _store.Save(_tempFolder, ClientId, state1);
            _store.Save(_tempFolder, ClientId, state2);

            var all = _store.LoadAll(_tempFolder, ClientId);

            Assert.That(all, Has.Count.EqualTo(2));
            // Ordered by CreatedAtUtc
            var loaded1 = System.Linq.Enumerable.First(all);
            var loaded2 = System.Linq.Enumerable.Last(all);

            Assert.That(loaded1.DownloadId, Is.EqualTo("multi-001"));
            Assert.That(loaded1.ResolvedUrl, Is.Null);
            Assert.That(loaded1.FallbackMode, Is.EqualTo(DirectDownloadFallbackMode.None));

            Assert.That(loaded2.DownloadId, Is.EqualTo("multi-002"));
            Assert.That(loaded2.ResolvedUrl, Is.EqualTo("https://cdn.example/bbb222/book.epub"));
            Assert.That(loaded2.FallbackMode, Is.EqualTo(DirectDownloadFallbackMode.DeferredPlaywright));
        }

        [Test]
        public void should_preserve_resolved_url_and_fallback_mode_after_save_and_reload_cycle()
        {
            var original = new DirectDownloadClientState
            {
                DownloadId = "cycle-001",
                Title = "Cycle Test",
                DownloadUrl = "https://info.example/md5/ccc333",
                ResolvedUrl = "https://fast.cdn.example/ccc333/download.epub?token=abc",
                FallbackMode = DirectDownloadFallbackMode.DeferredPlaywright,
                Status = DownloadItemStatus.Downloading,
                CreatedAtUtc = new DateTime(2025, 7, 1, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAtUtc = new DateTime(2025, 7, 1, 0, 0, 0, DateTimeKind.Utc)
            };

            _store.Save(_tempFolder, ClientId, original);
            var reloaded = _store.Find(_tempFolder, ClientId, "cycle-001");

            // Simulate a restart: save again from reloaded state
            _store.Save(_tempFolder, ClientId, reloaded);
            var final = _store.Find(_tempFolder, ClientId, "cycle-001");

            Assert.That(final.ResolvedUrl, Is.EqualTo("https://fast.cdn.example/ccc333/download.epub?token=abc"));
            Assert.That(final.FallbackMode, Is.EqualTo(DirectDownloadFallbackMode.DeferredPlaywright));
            Assert.That(final.DownloadUrl, Is.EqualTo("https://info.example/md5/ccc333"));
        }

        // ── Interrupted / repeated operation classes ──

        [Test]
        public void should_survive_repeated_save_cycles_without_corrupting_new_fields()
        {
            var state = new DirectDownloadClientState
            {
                DownloadId = "repeat-save",
                Title = "Repeated Save",
                DownloadUrl = "https://example.com/repeat.epub",
                ResolvedUrl = "https://cdn.example/repeat-resolved.epub",
                FallbackMode = DirectDownloadFallbackMode.DeferredPlaywright,
                Status = DownloadItemStatus.Downloading,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };

            for (var i = 0; i < 10; i++)
            {
                _store.Save(_tempFolder, ClientId, state);
                var loaded = _store.Find(_tempFolder, ClientId, "repeat-save");
                state = loaded;
            }

            Assert.That(state.ResolvedUrl, Is.EqualTo("https://cdn.example/repeat-resolved.epub"));
            Assert.That(state.FallbackMode, Is.EqualTo(DirectDownloadFallbackMode.DeferredPlaywright));
        }

        [Test]
        public void should_overwrite_resolved_url_on_subsequent_save()
        {
            var state = new DirectDownloadClientState
            {
                DownloadId = "overwrite-001",
                Title = "Overwrite Test",
                DownloadUrl = "https://info.example/overwrite",
                ResolvedUrl = "https://old-cdn.example/file.epub",
                FallbackMode = DirectDownloadFallbackMode.None,
                Status = DownloadItemStatus.Downloading,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };

            _store.Save(_tempFolder, ClientId, state);

            // Simulate re-resolution with new URL and deferred fallback
            state.ResolvedUrl = "https://new-cdn.example/file.epub";
            state.FallbackMode = DirectDownloadFallbackMode.DeferredPlaywright;
            _store.Save(_tempFolder, ClientId, state);

            var loaded = _store.Find(_tempFolder, ClientId, "overwrite-001");

            Assert.That(loaded.ResolvedUrl, Is.EqualTo("https://new-cdn.example/file.epub"));
            Assert.That(loaded.FallbackMode, Is.EqualTo(DirectDownloadFallbackMode.DeferredPlaywright));
        }

        private sealed class TestDiskProvider : DiskProviderBase
        {
            public TestDiskProvider() : base(new System.IO.Abstractions.FileSystem()) { }
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
