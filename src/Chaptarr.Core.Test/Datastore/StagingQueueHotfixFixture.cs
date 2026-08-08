using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    public class StagingQueueHotfixFixture
    {
        private sealed class TestAppFolderInfo : IAppFolderInfo
        {
            public TestAppFolderInfo(string appDataFolder)
            {
                AppDataFolder = appDataFolder;
                TempFolder = appDataFolder;
                StartUpFolder = appDataFolder;
            }

            public string AppDataFolder { get; }
            public string TempFolder { get; }
            public string StartUpFolder { get; }
        }

        private sealed class TestStagingDbContext : IStagingDbContext
        {
            private readonly string _connectionString;

            public TestStagingDbContext(string connectionString)
            {
                _connectionString = connectionString;
            }

            public IDbConnection OpenConnection()
            {
                var connection = new SqliteConnection(_connectionString);
                connection.Open();
                connection.Execute("PRAGMA foreign_keys=ON;");
                return connection;
            }

            public void InitializeDatabase()
            {
            }
        }

        [Test]
        public void initialize_database_should_repair_import_results_constraint_and_round_trip_new_outcomes()
        {
            var appDataFolder = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"staging_repair_{Guid.NewGuid():N}");
            Directory.CreateDirectory(appDataFolder);
            var databasePath = Path.Combine(appDataFolder, "staging.db");
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                ForeignKeys = true
            }.ToString();

            try
            {
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    connection.Execute("PRAGMA foreign_keys=ON;");
                    CreateOldStagingSchema(connection);
                    connection.Execute(@"
                        INSERT INTO ingest_queue (id, path, mtime_ns, size_bytes, tags_json, status, attempts, created_at, updated_at)
                        VALUES (1, '/scan/book.epub', 0, 0, '{}', 'done', 0, 1, 1);

                        INSERT INTO import_results (id, queue_item_id, path, outcome, created_at)
                        VALUES (1, 1, '/scan/book.epub', 'imported', 1);
                    ");
                }

                var context = new StagingDbContext(new TestAppFolderInfo(appDataFolder), LogManager.GetLogger("test"));
                context.InitializeDatabase();

                var repository = new IngestQueueRepository(context, LogManager.GetLogger("test"));
                repository.CompleteItemWithResult(1, "/scan/book.epub", ImportOutcome.AlreadyLinked);
                Assert.That(repository.GetImportResults().Any(result => result.Outcome == ImportOutcome.AlreadyLinked), Is.True);

                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    connection.Execute("PRAGMA foreign_keys=ON;");

                    Assert.DoesNotThrow(() => connection.Execute(@"
                        INSERT INTO import_results (queue_item_id, path, outcome, created_at)
                        VALUES (1, '/scan/book.epub', 'ignored', 2);
                    "));

                    var outcomes = connection.Query<string>("SELECT outcome FROM import_results ORDER BY id;");
                    Assert.That(outcomes, Is.EquivalentTo(new[] { "imported", "alreadylinked", "ignored" }));
                }
            }
            finally
            {
                try
                {
                    if (Directory.Exists(appDataFolder))
                    {
                        Directory.Delete(appDataFolder, recursive: true);
                    }
                }
                catch
                {
                }
            }
        }

        [Test]
        public void file_tag_cache_should_round_trip_distinct_extraction_dispositions_with_real_ddl()
        {
            var appDataFolder = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"staging_tag_dispositions_{Guid.NewGuid():N}");
            Directory.CreateDirectory(appDataFolder);

            try
            {
                var context = new StagingDbContext(new TestAppFolderInfo(appDataFolder), LogManager.GetLogger("test"));
                context.InitializeDatabase();
                var repository = new FileTagCacheRepository(context, LogManager.GetLogger("test"));

                repository.Upsert("/scan/evidence.m4b", 1, 1, "{\"title\":[\"Book\"]}", 10, "evidence");
                repository.Upsert("/scan/noisy.m4b", 2, 2, "{\"comment\":[\"Promo\"]}", 20, "noisy_only");
                repository.Upsert("/scan/tagless.m4b", 3, 3, "{}", 30, "tagless");

                Assert.That(repository.TryGet("/scan/evidence.m4b", 1, 1, out _, out _, out var evidenceStatus), Is.True);
                Assert.That(repository.TryGet("/scan/noisy.m4b", 2, 2, out _, out _, out var noisyStatus), Is.True);
                Assert.That(repository.TryGet("/scan/tagless.m4b", 3, 3, out _, out _, out var taglessStatus), Is.True);
                Assert.That(evidenceStatus, Is.EqualTo("evidence"));
                Assert.That(noisyStatus, Is.EqualTo("noisy_only"));
                Assert.That(taglessStatus, Is.EqualTo("tagless"));
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    repository.Upsert("/scan/invalid-through-repository.m4b", 4, 4, "{}", null, "failed"));

                using var connection = context.OpenConnection();
                var tableSql = connection.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type='table' AND name='file_tag_cache';");
                Assert.That(tableSql, Does.Contain("'noisy_only'"));
                Assert.Throws<SqliteException>(() => connection.Execute(@"
                    INSERT INTO file_tag_cache(path, mtime_ns, size_bytes, tags_json, extraction_status, updated_at)
                    VALUES('/scan/invalid.m4b', 4, 4, '{}', 'failed', 1);
                "));
            }
            finally
            {
                try
                {
                    if (Directory.Exists(appDataFolder))
                    {
                        Directory.Delete(appDataFolder, recursive: true);
                    }
                }
                catch
                {
                }
            }
        }

        [Test]
        public void forced_rescan_should_requeue_unchanged_completed_item_with_real_ddl()
        {
            var appDataFolder = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"staging_forced_rescan_{Guid.NewGuid():N}");
            Directory.CreateDirectory(appDataFolder);

            try
            {
                var context = new StagingDbContext(new TestAppFolderInfo(appDataFolder), LogManager.GetLogger("test"));
                context.InitializeDatabase();
                var repository = new IngestQueueRepository(context, LogManager.GetLogger("test"));
                const string path = "/scan/unchanged.epub";

                repository.InsertBatch(new List<IngestQueueItem>
                {
                    new ()
                    {
                        Path = path,
                        MtimeNs = 100,
                        SizeBytes = 200,
                        TagsJson = "{\"TITLE\":[\"Old\"]}",
                        DurationSeconds = 10
                    }
                });

                var id = repository.GetQueuedItems().Single().Id;
                using (var connection = context.OpenConnection())
                {
                    connection.Execute(
                        "UPDATE ingest_queue SET status = 'done', attempts = 3, err = 'OLD_FAILURE' WHERE id = @id;",
                        new { id });
                }

                repository.InsertBatch(new List<IngestQueueItem>
                {
                    new ()
                    {
                        Path = path,
                        MtimeNs = 100,
                        SizeBytes = 200,
                        TagsJson = "{\"TITLE\":[\"Ignored\"]}",
                        DurationSeconds = 20
                    }
                });

                using (var connection = context.OpenConnection())
                {
                    var unchanged = connection.QuerySingle<IngestQueueItem>(@"
                        SELECT status AS Status, attempts AS Attempts, err AS Err,
                               tags_json AS TagsJson, duration_seconds AS DurationSeconds
                        FROM ingest_queue WHERE id = @id;", new { id });
                    Assert.That(unchanged.Status, Is.EqualTo("done"));
                    Assert.That(unchanged.Attempts, Is.EqualTo(3));
                    Assert.That(unchanged.Err, Is.EqualTo("OLD_FAILURE"));
                    Assert.That(unchanged.TagsJson, Is.EqualTo("{\"TITLE\":[\"Old\"]}"));
                    Assert.That(unchanged.DurationSeconds, Is.EqualTo(10));
                }

                repository.InsertBatch(new List<IngestQueueItem>
                {
                    new ()
                    {
                        Path = path,
                        MtimeNs = 100,
                        SizeBytes = 200,
                        TagsJson = "{\"TITLE\":[\"Fresh\"]}",
                        DurationSeconds = 30,
                        ForceRequeue = true
                    }
                });

                using (var connection = context.OpenConnection())
                {
                    var requeued = connection.QuerySingle<IngestQueueItem>(@"
                        SELECT status AS Status, attempts AS Attempts, err AS Err,
                               tags_json AS TagsJson, duration_seconds AS DurationSeconds
                        FROM ingest_queue WHERE id = @id;", new { id });
                    Assert.That(requeued.Status, Is.EqualTo("queued"));
                    Assert.That(requeued.Attempts, Is.Zero);
                    Assert.That(requeued.Err, Is.Null);
                    Assert.That(requeued.TagsJson, Is.EqualTo("{\"TITLE\":[\"Fresh\"]}"));
                    Assert.That(requeued.DurationSeconds, Is.EqualTo(30));
                }
            }
            finally
            {
                try
                {
                    if (Directory.Exists(appDataFolder))
                    {
                        Directory.Delete(appDataFolder, recursive: true);
                    }
                }
                catch
                {
                }
            }
        }

        [Test]
        public void requeue_failed_paths_should_retry_only_failed_items_observed_in_current_scan()
        {
            var appDataFolder = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"staging_failed_retry_{Guid.NewGuid():N}");
            Directory.CreateDirectory(appDataFolder);
            var databasePath = Path.Combine(appDataFolder, "staging.db");
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                ForeignKeys = true
            }.ToString();

            try
            {
                var context = new StagingDbContext(new TestAppFolderInfo(appDataFolder), LogManager.GetLogger("test"));
                context.InitializeDatabase();

                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    connection.Execute("PRAGMA foreign_keys=ON;");
                    connection.Execute(@"
                        INSERT INTO ingest_queue (id, path, mtime_ns, size_bytes, tags_json, status, attempts, created_at, updated_at)
                        VALUES
                            (1, '/books/failed.epub', 0, 100, '{}', 'done', 2, 1, 1),
                            (2, '/books/unmapped.epub', 0, 100, '{}', 'done', 2, 1, 1),
                            (3, '/books/not-seen.epub', 0, 100, '{}', 'done', 2, 1, 1),
                            (4, '/books/imported.epub', 0, 100, '{}', 'done', 2, 1, 1);

                        INSERT INTO import_results (queue_item_id, path, outcome, created_at)
                        VALUES
                            (1, '/books/failed.epub', 'failed', 1),
                            (2, '/books/unmapped.epub', 'unmapped', 1),
                            (3, '/books/not-seen.epub', 'failed', 1),
                            (4, '/books/imported.epub', 'imported', 1);
                    ");
                }

                var repository = new IngestQueueRepository(context, LogManager.GetLogger("test"));
                var requeued = repository.RequeueFailedPaths(new[]
                {
                    "/books/failed.epub",
                    "/books/unmapped.epub",
                    "/books/imported.epub"
                });

                Assert.That(requeued, Is.EqualTo(1));

                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    var rows = connection.Query<IngestQueueItem>(
                        "SELECT id AS Id, status AS Status, attempts AS Attempts FROM ingest_queue ORDER BY id;").ToList();

                    Assert.That(rows.Single(row => row.Id == 1).Status, Is.EqualTo("queued"));
                    Assert.That(rows.Single(row => row.Id == 1).Attempts, Is.EqualTo(2), "Scheduled retry must preserve attempt history");
                    Assert.That(rows.Where(row => row.Id != 1).All(row => row.Status == "done"), Is.True);
                }
            }
            finally
            {
                try
                {
                    if (Directory.Exists(appDataFolder))
                    {
                        Directory.Delete(appDataFolder, recursive: true);
                    }
                }
                catch
                {
                }
            }
        }

        [Test]
        public void complete_item_with_result_should_mark_done_when_result_history_write_fails()
        {
            var databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"staging_complete_{Guid.NewGuid():N}.db");
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                ForeignKeys = true
            }.ToString();

            try
            {
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    connection.Execute("PRAGMA foreign_keys=ON;");
                    CreateOldStagingSchema(connection);
                    connection.Execute(@"
                        INSERT INTO ingest_queue (id, path, mtime_ns, size_bytes, tags_json, status, attempts, created_at, updated_at)
                        VALUES (1, '/scan/book.epub', 0, 0, '{}', 'in_progress', 1, 1, 1);
                    ");
                }

                var sut = new IngestQueueRepository(new TestStagingDbContext(connectionString), LogManager.GetLogger("test"));

                Assert.DoesNotThrow(() => sut.CompleteItemWithResult(1, "/scan/book.epub", ImportOutcome.Ignored, errorMessage: "ROOT_FOLDER_TYPE_Ebook", statusError: "ROOT_FOLDER_TYPE_Ebook"));

                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    var row = connection.QuerySingle<(string Status, string Err)>("SELECT status, err FROM ingest_queue WHERE id = 1;");
                    var resultCount = connection.QuerySingle<int>("SELECT COUNT(*) FROM import_results;");

                    Assert.That(row.Status, Is.EqualTo("done"));
                    Assert.That(row.Err, Is.EqualTo("ROOT_FOLDER_TYPE_Ebook"));
                    Assert.That(resultCount, Is.EqualTo(0));
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(databasePath))
                    {
                        File.Delete(databasePath);
                    }
                }
                catch
                {
                }
            }
        }

        [Test]
        public void root_type_mismatch_should_be_visible_as_unmapped()
        {
            var appDataFolder = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"staging_root_mismatch_{Guid.NewGuid():N}");
            Directory.CreateDirectory(appDataFolder);
            var rootPath = Path.Combine(appDataFolder, "ebooks");
            Directory.CreateDirectory(rootPath);
            var filePath = Path.Combine(rootPath, "misplaced.m4b");
            File.WriteAllText(filePath, "audio");

            try
            {
                var mediaFileService = new RecordingMediaFileService();
                var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["TITLE"] = new List<string> { "Misplaced Audio" }
                };

                var (outcome, reason) = StagingQueueFileDispositionHelper.EnsureVisibleOrIgnored(
                    filePath,
                    tags,
                    durationSeconds: 1234,
                    mediaFileService,
                    new RealDiskProvider(),
                    _ => new RootFolder { Path = rootPath, FolderType = FolderType.Ebook },
                    LogManager.GetLogger("test"),
                    "[test]");

                var unmapped = mediaFileService.GetUnmappedFiles().Single();
                Assert.That(outcome, Is.EqualTo(ImportOutcome.Unmapped));
                Assert.That(reason, Is.EqualTo("ROOT_FOLDER_TYPE_Ebook"));
                Assert.That(unmapped.Path, Is.EqualTo(filePath));
                Assert.That(unmapped.MediaType, Is.EqualTo("audiobook"));
                Assert.That(unmapped.DurationSeconds, Is.EqualTo(1234));
                Assert.That(unmapped.AllTags?["TITLE"], Is.EquivalentTo(new[] { "Misplaced Audio" }));
            }
            finally
            {
                try
                {
                    if (Directory.Exists(appDataFolder))
                    {
                        Directory.Delete(appDataFolder, recursive: true);
                    }
                }
                catch
                {
                }
            }
        }

        [Test]
        public void recover_in_progress_updated_before_should_requeue_only_previous_command_items_under_path()
        {
            var databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"staging_recover_{Guid.NewGuid():N}.db");
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                ForeignKeys = true
            }.ToString();

            try
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    connection.Execute("PRAGMA foreign_keys=ON;");
                    CreateOldStagingSchema(connection);
                    connection.Execute(@"
                        INSERT INTO ingest_queue (id, path, mtime_ns, size_bytes, tags_json, status, attempts, created_at, updated_at)
                        VALUES
                            (1, '/scan/old.epub', 0, 0, '{}', 'in_progress', 1, @now, @old),
                            (2, '/scan/fresh.epub', 0, 0, '{}', 'in_progress', 1, @now, @now),
                            (3, '/other/old.epub', 0, 0, '{}', 'in_progress', 1, @now, @old);
                    ", new { now, old = now - 120 });
                }

                var sut = new IngestQueueRepository(new TestStagingDbContext(connectionString), LogManager.GetLogger("test"));

                var recovered = sut.RecoverInProgressUpdatedBefore("/scan", updatedBefore: now - 30);

                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    var statuses = connection.Query<(int Id, string Status)>("SELECT id, status FROM ingest_queue ORDER BY id;");

                    Assert.That(recovered, Is.EqualTo(1));
                    Assert.That(statuses, Is.EquivalentTo(new[]
                    {
                        (1, "queued"),
                        (2, "in_progress"),
                        (3, "in_progress")
                    }));
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(databasePath))
                    {
                        File.Delete(databasePath);
                    }
                }
                catch
                {
                }
            }
        }

        private sealed class RecordingMediaFileService : IMediaFileService
        {
            private readonly Dictionary<string, BookFile> _files = new(StringComparer.OrdinalIgnoreCase);

            public BookFile Add(BookFile bookFile)
            {
                _files[bookFile.Path] = bookFile;
                return bookFile;
            }

            public void Seed(BookFile bookFile)
            {
                _files[bookFile.Path] = bookFile;
            }

            public void AddMany(List<BookFile> bookFiles) => throw new NotImplementedException();
            public void Update(BookFile bookFile) => throw new NotImplementedException();
            public void Update(List<BookFile> bookFiles) => throw new NotImplementedException();
            public void Delete(BookFile bookFile, DeleteMediaFileReason reason) => throw new NotImplementedException();
            public void DeleteMany(List<BookFile> bookFiles, DeleteMediaFileReason reason) => throw new NotImplementedException();
            public List<IFileInfo> FilterUnchangedFiles(List<IFileInfo> files, FilterFilesType filter) => throw new NotImplementedException();
            public List<BookFile> GetFilesByAuthor(int authorId) => throw new NotImplementedException();
            public List<BookFile> GetFilesByBook(int bookId) => throw new NotImplementedException();
            public List<BookFile> GetFilesByBooks(List<int> bookIds) => throw new NotImplementedException();
            public List<BookFile> GetFilesByEdition(int editionId) => throw new NotImplementedException();
            public List<BookFile> GetUnmappedFiles() => _files.Values.Where(file => file.EditionId == 0).ToList();
            public BookFile Get(int id) => _files.Values.FirstOrDefault(file => file.Id == id);
            public List<BookFile> Get(IEnumerable<int> ids) => _files.Values.Where(file => ids.Contains(file.Id)).ToList();
            public List<BookFile> GetFilesWithBasePath(string path) => _files.Values.Where(file => file.Path.StartsWith(path, StringComparison.OrdinalIgnoreCase)).ToList();
            public List<BookFile> GetFilesWithBasePath(string path, string mediaType) => _files.Values.Where(file => file.Path.StartsWith(path, StringComparison.OrdinalIgnoreCase) && string.Equals(file.MediaType, mediaType, StringComparison.OrdinalIgnoreCase)).ToList();
            public List<BookFile> GetFileWithPath(List<string> path) => _files.Values.Where(file => path.Contains(file.Path, StringComparer.OrdinalIgnoreCase)).ToList();
            public BookFile GetFileWithPath(string path) => _files.TryGetValue(path, out var file) ? file : null;
            public void UpdateMediaInfo(List<BookFile> bookFiles) => throw new NotImplementedException();
            public List<BookFile> GetFilesByAuthorAndMediaType(int authorId, string mediaType) => throw new NotImplementedException();
        }

        private sealed class StubRootFolderService : IRootFolderService
        {
            private readonly RootFolder _rootFolder;

            public StubRootFolderService(RootFolder rootFolder)
            {
                _rootFolder = rootFolder;
            }

            public List<RootFolder> All() => _rootFolder == null ? new List<RootFolder>() : new List<RootFolder> { _rootFolder };
            public List<RootFolder> AllWithSpaceStats() => All();
            public RootFolder Add(RootFolder rootFolder) => throw new NotImplementedException();
            public RootFolder Update(RootFolder rootFolder) => throw new NotImplementedException();
            public void Remove(int id) => throw new NotImplementedException();
            public RootFolder Get(int id) => throw new NotImplementedException();
            public List<RootFolder> AllForTag(int tagId) => throw new NotImplementedException();
            public RootFolder GetBestRootFolder(string path) => _rootFolder;
            public RootFolder GetBestRootFolder(string path, List<RootFolder> allRootFolders) => _rootFolder;
            public string GetBestRootFolderPath(string path) => _rootFolder?.Path;
            public string GetBestRootFolderPath(string path, List<RootFolder> allRootFolders) => _rootFolder?.Path;
        }

        private sealed class RealDiskProvider : IDiskProvider
        {
            private readonly FileSystem _fileSystem = new();

            public long? GetAvailableSpace(string path) => throw new NotImplementedException();
            public void InheritFolderPermissions(string filename) => throw new NotImplementedException();
            public void SetEveryonePermissions(string filename) => throw new NotImplementedException();
            public void SetFilePermissions(string path, string mask, string group) => throw new NotImplementedException();
            public void SetPermissions(string path, string mask, string group) => throw new NotImplementedException();
            public void CopyPermissions(string sourcePath, string targetPath) => throw new NotImplementedException();
            public long? GetTotalSize(string path) => throw new NotImplementedException();
            public DateTime FolderGetCreationTime(string path) => Directory.GetCreationTime(path);
            public DateTime FolderGetLastWrite(string path) => Directory.GetLastWriteTime(path);
            public DateTime FileGetLastWrite(string path) => File.GetLastWriteTime(path);
            public void EnsureFolder(string path) => Directory.CreateDirectory(path);
            public bool FolderExists(string path) => Directory.Exists(path);
            public bool FileExists(string path) => File.Exists(path);
            public bool FileExistsCanonical(string path) => File.Exists(path);
            public bool FileExists(string path, StringComparison stringComparison) => File.Exists(path);
            public bool FolderWritable(string path) => throw new NotImplementedException();
            public bool FolderEmpty(string path) => throw new NotImplementedException();
            public IEnumerable<string> GetDirectories(string path) => Directory.EnumerateDirectories(path);
            public IEnumerable<string> GetFiles(string path, bool recursive) => Directory.EnumerateFiles(path, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
            public long GetFolderSize(string path) => throw new NotImplementedException();
            public long GetFileSize(string path) => new FileInfo(path).Length;
            public void CreateFolder(string path) => Directory.CreateDirectory(path);
            public void DeleteFile(string path) => File.Delete(path);
            public void CloneFile(string source, string destination, bool overwrite = false) => throw new NotImplementedException();
            public void CopyFile(string source, string destination, bool overwrite = false) => File.Copy(source, destination, overwrite);
            public void MoveFile(string source, string destination, bool overwrite = false) => throw new NotImplementedException();
            public void MoveFolder(string source, string destination) => throw new NotImplementedException();
            public bool TryRenameFile(string source, string destination) => throw new NotImplementedException();
            public bool TryCreateHardLink(string source, string destination) => throw new NotImplementedException();
            public int? GetFileLinkCount(string path) => 1;
            public bool TryCreateRefLink(string source, string destination) => throw new NotImplementedException();
            public void DeleteFolder(string path, bool recursive) => Directory.Delete(path, recursive);
            public string ReadAllText(string filePath) => File.ReadAllText(filePath);
            public void WriteAllText(string filename, string contents) => File.WriteAllText(filename, contents);
            public void FolderSetLastWriteTime(string path, DateTime dateTime) => Directory.SetLastWriteTime(path, dateTime);
            public void FileSetLastWriteTime(string path, DateTime dateTime) => File.SetLastWriteTime(path, dateTime);
            public bool IsFileLocked(string path) => false;
            public string GetPathRoot(string path) => Path.GetPathRoot(path);
            public string GetParentFolder(string path) => Path.GetDirectoryName(path);
            public FileAttributes GetFileAttributes(string path) => File.GetAttributes(path);
            public void EmptyFolder(string path) => throw new NotImplementedException();
            public string GetVolumeLabel(string path) => throw new NotImplementedException();
            public FileStream OpenReadStream(string path) => File.OpenRead(path);
            public FileStream OpenWriteStream(string path) => File.OpenWrite(path);
            public List<IMount> GetMounts() => throw new NotImplementedException();
            public IMount GetMount(string path) => throw new NotImplementedException();
            public IDirectoryInfo GetDirectoryInfo(string path) => _fileSystem.DirectoryInfo.FromDirectoryName(path);
            public List<IDirectoryInfo> GetDirectoryInfos(string path) => throw new NotImplementedException();
            public IFileInfo GetFileInfo(string path) => _fileSystem.FileInfo.FromFileName(path);
            public List<IFileInfo> GetFileInfos(string path, bool recursive = false) => throw new NotImplementedException();
            public void RemoveEmptySubfolders(string path) => throw new NotImplementedException();
            public void SaveStream(Stream stream, string path) => throw new NotImplementedException();
            public bool IsValidFolderPermissionMask(string mask) => throw new NotImplementedException();
        }

        [Test]
        public void sweep_all_residual_items_should_move_eligible_files_to_unmapped_and_complete_queue()
        {
            var appDataFolder = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"staging_sweep_unmapped_{Guid.NewGuid():N}");
            Directory.CreateDirectory(appDataFolder);
            var rootPath = Path.Combine(appDataFolder, "library");
            Directory.CreateDirectory(rootPath);
            var filePath = Path.Combine(rootPath, "book.epub");
            File.WriteAllText(filePath, "test");

            try
            {
                var context = new StagingDbContext(new TestAppFolderInfo(appDataFolder), LogManager.GetLogger("test"));
                context.InitializeDatabase();

                using (var connection = context.OpenConnection())
                {
                    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    connection.Execute(@"
                        INSERT INTO ingest_queue (id, path, mtime_ns, size_bytes, tags_json, duration_seconds, status, attempts, err, created_at, updated_at)
                        VALUES (1, @path, 0, 4, '{}', 600, 'queued', 0, 'PENDING_AUTHOR_IMPORT', @now, @now);
                    ", new { path = filePath, now });
                }

                var mediaFileService = new RecordingMediaFileService();
                var repo = new IngestQueueRepository(context, LogManager.GetLogger("test"));
                var sweeper = new StagingResidualQueueSweeper(
                    repo,
                    mediaFileService,
                    new RealDiskProvider(),
                    new StubRootFolderService(new RootFolder { Path = rootPath, FolderType = FolderType.Ebook }),
                    LogManager.GetLogger("test"));

                var swept = sweeper.SweepAllResidualItems();

                Assert.That(swept, Is.EqualTo(1));
                Assert.That(mediaFileService.GetUnmappedFiles(), Has.Count.EqualTo(1));
                Assert.That(mediaFileService.GetUnmappedFiles().Single().Path, Is.EqualTo(filePath));

                using (var connection = context.OpenConnection())
                {
                    var queueRow = connection.QuerySingle<(string Status, string Err)>("SELECT status, err FROM ingest_queue WHERE id = 1;");
                    var resultRow = connection.QuerySingle<(string Outcome, string ErrorMessage)>("SELECT outcome, error_message FROM import_results WHERE queue_item_id = 1;");

                    Assert.That(queueRow.Status, Is.EqualTo("done"));
                    Assert.That(queueRow.Err, Is.EqualTo("PENDING_AUTHOR_IMPORT"));
                    Assert.That(resultRow.Outcome, Is.EqualTo("unmapped"));
                    Assert.That(resultRow.ErrorMessage, Is.EqualTo("PENDING_AUTHOR_IMPORT"));
                }
            }
            finally
            {
                try
                {
                    if (Directory.Exists(appDataFolder))
                    {
                        Directory.Delete(appDataFolder, recursive: true);
                    }
                }
                catch
                {
                }
            }
        }

        [Test]
        public void sweep_all_residual_items_should_ignore_missing_or_already_tracked_files()
        {
            var appDataFolder = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"staging_sweep_ignored_{Guid.NewGuid():N}");
            Directory.CreateDirectory(appDataFolder);
            var rootPath = Path.Combine(appDataFolder, "library");
            Directory.CreateDirectory(rootPath);
            var trackedFilePath = Path.Combine(rootPath, "tracked.epub");
            var missingFilePath = Path.Combine(rootPath, "missing.epub");
            File.WriteAllText(trackedFilePath, "tracked");

            try
            {
                var context = new StagingDbContext(new TestAppFolderInfo(appDataFolder), LogManager.GetLogger("test"));
                context.InitializeDatabase();

                using (var connection = context.OpenConnection())
                {
                    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    connection.Execute(@"
                        INSERT INTO ingest_queue (id, path, mtime_ns, size_bytes, tags_json, duration_seconds, status, attempts, err, created_at, updated_at)
                        VALUES
                            (1, @trackedPath, 0, 7, '{}', NULL, 'queued', 0, NULL, @now, @now),
                            (2, @missingPath, 0, 7, '{}', NULL, 'queued', 0, NULL, @now, @now);
                    ", new { trackedPath = trackedFilePath, missingPath = missingFilePath, now });
                }

                var mediaFileService = new RecordingMediaFileService();
                mediaFileService.Seed(new BookFile { Id = 10, Path = trackedFilePath, EditionId = 123, MediaType = "ebook" });

                var repo = new IngestQueueRepository(context, LogManager.GetLogger("test"));
                var sweeper = new StagingResidualQueueSweeper(
                    repo,
                    mediaFileService,
                    new RealDiskProvider(),
                    new StubRootFolderService(new RootFolder { Path = rootPath, FolderType = FolderType.Ebook }),
                    LogManager.GetLogger("test"));

                var swept = sweeper.SweepAllResidualItems();

                Assert.That(swept, Is.EqualTo(2));
                Assert.That(mediaFileService.GetUnmappedFiles(), Is.Empty);

                using (var connection = context.OpenConnection())
                {
                    var queueRows = connection.Query<(int Id, string Status, string Err)>("SELECT id, status, err FROM ingest_queue ORDER BY id;").ToList();
                    var resultRows = connection.Query<(int QueueItemId, string Outcome, string ErrorMessage)>("SELECT queue_item_id, outcome, error_message FROM import_results ORDER BY queue_item_id;").ToList();

                    Assert.That(queueRows, Is.EquivalentTo(new[]
                    {
                        (1, "done", "ALREADY_TRACKED"),
                        (2, "done", "FILE_MISSING")
                    }));

                    Assert.That(resultRows, Is.EquivalentTo(new[]
                    {
                        (1, "ignored", "ALREADY_TRACKED"),
                        (2, "ignored", "FILE_MISSING")
                    }));
                }
            }
            finally
            {
                try
                {
                    if (Directory.Exists(appDataFolder))
                    {
                        Directory.Delete(appDataFolder, recursive: true);
                    }
                }
                catch
                {
                }
            }
        }

        private static void CreateOldStagingSchema(IDbConnection connection)
        {
            connection.Execute(@"
                CREATE TABLE ingest_queue(
                    id INTEGER PRIMARY KEY,
                    path TEXT NOT NULL UNIQUE,
                    mtime_ns INTEGER NOT NULL,
                    size_bytes INTEGER NOT NULL,
                    tags_json TEXT NOT NULL,
                    duration_seconds INTEGER,
                    status TEXT NOT NULL DEFAULT 'queued',
                    attempts INTEGER NOT NULL DEFAULT 0,
                    err TEXT,
                    created_at INTEGER NOT NULL,
                    updated_at INTEGER NOT NULL
                );

                CREATE TABLE import_results(
                    id INTEGER PRIMARY KEY,
                    queue_item_id INTEGER NOT NULL,
                    path TEXT NOT NULL,
                    outcome TEXT NOT NULL CHECK(outcome IN ('imported', 'unmapped', 'failed')),
                    book_id INTEGER,
                    author_id INTEGER,
                    quality TEXT,
                    error_message TEXT,
                    created_at INTEGER NOT NULL,
                    FOREIGN KEY(queue_item_id) REFERENCES ingest_queue(id)
                );

                CREATE TABLE staging_metadata(
                    key TEXT PRIMARY KEY,
                    value TEXT,
                    updated_at INTEGER NOT NULL
                );
            ");
        }
    }
}
