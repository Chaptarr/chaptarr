using System;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Chaptarr.Core.Test.Common;
using Dapper;
using Microsoft.Data.Sqlite;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration.Framework;
using NzbDrone.Core.Lifecycle;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    public class FileTagCacheMigrationFixture
    {
        private sealed class AppFolder : IAppFolderInfo
        {
            public string AppDataFolder { get; set; }
            public string TempFolder => AppDataFolder;
            public string StartUpFolder => AppDataFolder;
        }

        private sealed class CachedRead
        {
            public string Path { get; set; }
            public long MtimeNs { get; set; }
            public long SizeBytes { get; set; }
            public string TagsJson { get; set; }
            public int? DurationSeconds { get; set; }
            public string ExtractionStatus { get; set; }
        }

        private string _folder;
        private StagingDbContext _staging;
        private readonly Logger _logger = LogManager.GetCurrentClassLogger();

        [SetUp]
        public void Setup()
        {
            _folder = Directory.CreateDirectory(Path.Combine(TestContext.CurrentContext.WorkDirectory, "cache_move_" + Guid.NewGuid().ToString("N"))).FullName;
            _staging = new StagingDbContext(new AppFolder { AppDataFolder = _folder }, _logger);
            _staging.InitializeDatabase();
        }

        [TearDown]
        public void Cleanup()
        {
            using (var connection = (SqliteConnection)_staging.OpenConnection())
            {
                SqliteConnection.ClearPool(connection);
            }

            Directory.Delete(_folder, true);
        }

        private void SeedOldCache(bool withStatus, int count)
        {
            using var connection = _staging.OpenConnection();
            connection.Execute($@"
                CREATE TABLE file_tag_cache(
                    path TEXT PRIMARY KEY, mtime_ns INTEGER NOT NULL, size_bytes INTEGER NOT NULL,
                    tags_json TEXT NOT NULL, duration_seconds INTEGER,
                    {(withStatus ? "extraction_status TEXT," : string.Empty)} updated_at INTEGER NOT NULL);
                CREATE TABLE staging_metadata(key TEXT PRIMARY KEY, value TEXT, updated_at INTEGER);
                INSERT INTO staging_metadata VALUES('current_command_id', '123', 1);
            ");
            using var transaction = connection.BeginTransaction();
            connection.Execute($@"
                INSERT INTO file_tag_cache(path, mtime_ns, size_bytes, tags_json, duration_seconds,
                    {(withStatus ? "extraction_status," : string.Empty)} updated_at)
                VALUES(@path, 1700000000000000000, 5000000000, @tags, 120,
                    {(withStatus ? "@status," : string.Empty)} 123)",
                Enumerable.Range(0, count).Select(i => new
                {
                    path = Path.Combine(_folder, i + ".m4b"),
                    tags = i % 2 == 0 ? "{}" : "{\"title\":[\"Cached tags\"]}",
                    status = i % 3 == 0 ? null : i % 3 == 1 ? "evidence" : "tagless"
                }), transaction);
            transaction.Commit();
        }

        private static int OldCacheExists(IDbConnection source) => source.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='file_tag_cache'");

        [TestCase(false)]
        [TestCase(true)]
        public void tag_cache_belongs_to_cache_database(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var connection = database.OpenConnection();
                Assert.That(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM file_tag_cache"), Is.Zero);
            }, MigrationType.Cache);

            using var source = _staging.OpenConnection();
            Assert.That(OldCacheExists(source), Is.Zero);
            Assert.That(source.Query<string>("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'"),
                Is.EquivalentTo(new[] { "ingest_queue", "import_results" }));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void extraction_dispositions_and_expiry_round_trip_in_cache_database(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var cache = new FileTagCacheRepository(new CacheDatabase(database), _logger);
                foreach (var status in new[] { "evidence", "noisy_only", "tagless" })
                {
                    var path = Path.Combine(_folder, status + ".m4b");
                    cache.Upsert(path, 1700000000000000000, 5000000000, "{}", 120, status);
                    Assert.That(cache.TryGet(path, 1700000000000000000, 5000000000, out var tags, out var duration, out var savedStatus), Is.True);
                    Assert.That((tags, duration, savedStatus), Is.EqualTo(("{}", 120, status)));
                    Assert.That(cache.TryGet(path, 1, 5000000000, out _, out _, out _), Is.False);
                    Assert.That(cache.TryGet(path, 1700000000000000000, 1, out _, out _, out _), Is.False);
                }

                Assert.Throws<ArgumentOutOfRangeException>(() => cache.Upsert("invalid", 1, 1, "{}", null, "failed"));
                using var connection = database.OpenConnection();
                Assert.That(() => connection.Execute(@"
                    INSERT INTO file_tag_cache(path, mtime_ns, size_bytes, tags_json, extraction_status, updated_at)
                    VALUES('invalid', 1, 1, '{}', 'failed', 1)"), Throws.Exception);
                connection.Execute("UPDATE file_tag_cache SET updated_at=1 WHERE extraction_status='tagless'");
                cache.PurgeOld();
                Assert.That(connection.Query<string>("SELECT extraction_status FROM file_tag_cache"), Is.EquivalentTo(new[] { "evidence", "noisy_only" }));
                _staging.InitializeDatabase();
                using var source = _staging.OpenConnection();
                Assert.That(OldCacheExists(source), Is.Zero);
            }, MigrationType.Cache);
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void upgrade_preserves_cached_values_newer_destination_and_active_work(bool postgres, bool withStatus)
        {
            SeedOldCache(withStatus, 2049);
            using var source = _staging.OpenConnection();
            source.Execute(@"
                INSERT INTO ingest_queue(id,path,mtime_ns,size_bytes,tags_json,status,created_at,updated_at)
                VALUES(1,'queued',1,1,'{}','queued',1,1), (2,'running',1,1,'{}','in_progress',1,1), (3,'completed',1,1,'{}','done',1,1);
                INSERT INTO import_results(queue_item_id,path,outcome,created_at) VALUES(3,'completed','unmapped',1);
            ");
            _staging.InitializeDatabase();
            Assert.That(source.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE name='staging_metadata'"), Is.Zero);
            Assert.That(source.Query<string>("SELECT status FROM ingest_queue ORDER BY id"), Is.EqualTo(new[] { "queued", "queued", "done" }));

            DualDatabaseFixture.Run(postgres, database =>
            {
                var cache = new FileTagCacheRepository(new CacheDatabase(database), _logger);
                var newerPath = Path.Combine(_folder, "0.m4b");
                cache.Upsert(newerPath, 7, 8, "{\"title\":[\"Newer tags\"]}", 9, "evidence");
                var elapsed = Stopwatch.StartNew();
                cache.MoveFromStaging(_staging);
                TestContext.WriteLine($"Moved 2049 cached files to {(postgres ? "PostgreSQL" : "SQLite")} in {elapsed.ElapsedMilliseconds} ms (17 parameter-bounded batches).");
                Assert.That(OldCacheExists(source), Is.Zero);
                using var destination = database.OpenConnection();
                var saved = destination.Query<CachedRead>(@"
                    SELECT path AS Path, mtime_ns AS MtimeNs, size_bytes AS SizeBytes,
                           tags_json AS TagsJson, duration_seconds AS DurationSeconds,
                           extraction_status AS ExtractionStatus FROM file_tag_cache")
                    .ToDictionary(row => row.Path);
                Assert.That(saved, Has.Count.EqualTo(2049));
                for (var i = 1; i < 2049; i++)
                {
                    var row = saved[Path.Combine(_folder, i + ".m4b")];
                    Assert.That((row.MtimeNs, row.SizeBytes), Is.EqualTo((1700000000000000000, 5000000000)));
                    Assert.That(row.TagsJson, Is.EqualTo(i % 2 == 0 ? "{}" : "{\"title\":[\"Cached tags\"]}"));
                    Assert.That(row.DurationSeconds, Is.EqualTo(120));
                    Assert.That(row.ExtractionStatus, Is.EqualTo(!withStatus || i % 3 == 0 ? null : i % 3 == 1 ? "evidence" : "tagless"));
                }

                Assert.That(cache.TryGet(newerPath, 7, 8, out var newerTags, out var newerDuration, out var newerStatus), Is.True);
                Assert.That((newerTags, newerDuration, newerStatus), Is.EqualTo(("{\"title\":[\"Newer tags\"]}", 9, "evidence")));
                cache.MoveFromStaging(_staging);
                _staging.InitializeDatabase();
                Assert.That(OldCacheExists(source), Is.Zero);
                Assert.That(source.ExecuteScalar<int>("SELECT COUNT(*) FROM ingest_queue"), Is.EqualTo(3));
                Assert.That(source.ExecuteScalar<int>("SELECT COUNT(*) FROM import_results"), Is.EqualTo(1));
            }, MigrationType.Cache);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void failed_later_batch_retains_source_and_rolls_back_then_retries(bool postgres)
        {
            SeedOldCache(true, 129);
            using var source = _staging.OpenConnection();
            source.Execute("UPDATE file_tag_cache SET extraction_status='failed' WHERE rowid=129");
            DualDatabaseFixture.Run(postgres, database =>
            {
                var cache = new FileTagCacheRepository(new CacheDatabase(database), _logger);
                cache.Upsert("existing", 1, 1, "{}", null, "tagless");
                cache.MoveFromStaging(_staging);
                Assert.That(OldCacheExists(source), Is.EqualTo(1));
                Assert.That(source.ExecuteScalar<int>("SELECT COUNT(*) FROM file_tag_cache"), Is.EqualTo(129));
                using var destination = database.OpenConnection();
                Assert.That(destination.ExecuteScalar<int>("SELECT COUNT(*) FROM file_tag_cache"), Is.EqualTo(1));
                source.Execute("UPDATE file_tag_cache SET extraction_status='tagless' WHERE rowid=129");
                cache.MoveFromStaging(_staging);
                Assert.That(OldCacheExists(source), Is.Zero);
                Assert.That(destination.ExecuteScalar<int>("SELECT COUNT(*) FROM file_tag_cache"), Is.EqualTo(130));
                Assert.That(cache.TryGet("existing", 1, 1, out _, out _, out _), Is.True);
            }, MigrationType.Cache);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void startup_moves_old_cache_and_replays_a_copy_completed_before_source_removal(bool postgres)
        {
            SeedOldCache(true, 3);
            _staging.InitializeDatabase();
            DualDatabaseFixture.Run(postgres, database =>
            {
                var cache = new FileTagCacheRepository(new CacheDatabase(database), _logger);
                var queue = new IngestQueueRepository(_staging, _logger);
                var initializer = new StagingDbInitializer(new StagingResidualQueueSweeper(queue, null, null, null, _logger), cache, _staging, _logger);
                initializer.Handle(new ApplicationStartedEvent());
                using var source = _staging.OpenConnection();
                Assert.That(OldCacheExists(source), Is.Zero);
                using var destination = database.OpenConnection();
                Assert.That(destination.ExecuteScalar<int>("SELECT COUNT(*) FROM file_tag_cache"), Is.EqualTo(3));
                destination.Execute("UPDATE file_tag_cache SET tags_json='newer', updated_at=124");
                SeedOldCache(true, 3);
                _staging.InitializeDatabase();
                initializer.Handle(new ApplicationStartedEvent());
                Assert.That(destination.Query<string>("SELECT tags_json FROM file_tag_cache"), Is.EqualTo(new[] { "newer", "newer", "newer" }));
                Assert.That(OldCacheExists(source), Is.Zero);
            }, MigrationType.Cache);
        }
    }
}
