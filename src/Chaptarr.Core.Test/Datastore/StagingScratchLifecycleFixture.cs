using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Chaptarr.Core.Test.Common;
using Dapper;
using Microsoft.Data.Sqlite;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Housekeeping;
using NzbDrone.Core.Housekeeping.Housekeepers;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.MediaFiles.BookImport.Services;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    public class StagingScratchLifecycleFixture
    {
        private string _folder;
        private StagingDbContext _context;
        private IngestQueueRepository _queue;
        private readonly Logger _logger = LogManager.GetCurrentClassLogger();

        public class Proxy : DispatchProxy
        {
            public Func<MethodInfo, object[], object> Handler { get; set; }
            protected override object Invoke(MethodInfo method, object[] args) => Handler(method, args);
        }

        private static T Stub<T>(Func<MethodInfo, object[], object> handler) where T : class
        {
            var value = DispatchProxy.Create<T, Proxy>();
            ((Proxy)(object)value).Handler = handler;
            return value;
        }

        private static object Default(Type type) => type == typeof(void) || !type.IsValueType ? null : Activator.CreateInstance(type);

        [SetUp]
        public void Setup()
        {
            _folder = Directory.CreateDirectory(Path.Combine(TestContext.CurrentContext.WorkDirectory, "staging_scratch_" + Guid.NewGuid().ToString("N"))).FullName;
            var app = Stub<IAppFolderInfo>((method, args) => _folder);
            _context = new StagingDbContext(app, _logger);
            _context.InitializeDatabase();
            _queue = new IngestQueueRepository(_context, _logger);
        }

        [TearDown]
        public void Cleanup()
        {
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(_folder, "staging.db"),
                Cache = SqliteCacheMode.Private,
                Pooling = true
            }.ToString()))
            {
                SqliteConnection.ClearPool(connection);
            }

            Directory.Delete(_folder, true);
        }

        private string AddItem(string name, ImportOutcome? outcome = ImportOutcome.Unmapped)
        {
            var path = Path.Combine(_folder, name + ".epub");
            File.WriteAllText(path, "test");
            _queue.InsertBatch(new List<IngestQueueItem> { new IngestQueueItem { Path = path, TagsJson = "{}", SizeBytes = 4, MtimeNs = 1 } });
            if (outcome.HasValue)
            {
                using var connection = _context.OpenConnection();
                var id = connection.QuerySingle<int>("SELECT id FROM ingest_queue WHERE path=@path", new { path });
                _queue.CompleteItemWithResult(id, path, outcome.Value);
            }

            return path;
        }

        [TestCase(1)]
        [TestCase(8)]
        public async Task failed_staging_initialization_recovers_once_on_access_without_resetting_active_work(int callers)
        {
            var abandoned = AddItem("abandoned", null);
            var completed = AddItem("completed");
            using var factory = new LogFactory();
            var messages = new NLog.Targets.MemoryTarget { Layout = "${message}" };
            var logging = new NLog.Config.LoggingConfiguration();
            logging.AddRuleForAllLevels(messages);
            factory.Configuration = logging;
            var clock = new ManualTimeProvider();
            _context = new StagingDbContext(Stub<IAppFolderInfo>((_, _) => _folder), factory.GetLogger("staging-recovery"), clock);
            _queue = new IngestQueueRepository(_context, _logger);
            using var control = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(_folder, "staging.db"),
                Pooling = false
            }.ToString());
            control.Open();
            control.Execute(@"
                UPDATE ingest_queue SET status='in_progress' WHERE path=@abandoned;
                CREATE TRIGGER block_startup_recovery BEFORE UPDATE ON ingest_queue
                BEGIN SELECT RAISE(ABORT, 'blocked staging recovery'); END;", new { abandoned });

            var failure = Assert.Throws<SqliteException>(() => _context.InitializeDatabase());
            Assert.That(_context.InitializationError, Is.SameAs(failure));
            Assert.That(control.QuerySingle<string>("SELECT status FROM ingest_queue WHERE path=@abandoned", new { abandoned }), Is.EqualTo("in_progress"));
            control.Execute("DROP TRIGGER block_startup_recovery;");
            clock.Advance(StagingDbContext.InitializationRetryInterval);
            messages.Logs.Clear();

            using var ready = new CountdownEvent(callers);
            using var start = new ManualResetEventSlim();
            control.Execute("BEGIN IMMEDIATE;");
            var tasks = Enumerable.Range(0, callers).Select(_ => Task.Factory.StartNew(() =>
            {
                ready.Signal();
                start.Wait();
                using var connection = _context.OpenConnection();
                return connection.ExecuteScalar<int>("SELECT COUNT(*) FROM ingest_queue");
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            try
            {
                Assert.That(ready.Wait(TimeSpan.FromSeconds(10)), Is.True);
                start.Set();
                Assert.That(SpinWait.SpinUntil(() => messages.Logs.Contains("Initializing staging database") ||
                    tasks.All(task => task.IsCompleted), TimeSpan.FromSeconds(10)), Is.True);
            }
            finally
            {
                start.Set();
                control.Execute("ROLLBACK;");
                await Task.WhenAll(tasks);
            }

            Assert.That(tasks.Select(task => task.Result), Is.All.EqualTo(2));
            Assert.That(messages.Logs.Count(message => message == "Initializing staging database"), Is.EqualTo(1));
            Assert.That(_context.InitializationError, Is.Null);
            Assert.That(control.QuerySingle<string>("SELECT status FROM ingest_queue WHERE path=@abandoned", new { abandoned }), Is.EqualTo("queued"));
            Assert.That(_queue.GetImportResults().Single().Path, Is.EqualTo(completed));

            control.Execute("UPDATE ingest_queue SET status='in_progress' WHERE path=@abandoned", new { abandoned });
            Assert.That(_queue.GetQueueCount(), Is.EqualTo(1));
            Assert.That(control.QuerySingle<string>("SELECT status FROM ingest_queue WHERE path=@abandoned", new { abandoned }), Is.EqualTo("in_progress"));
            Assert.That(messages.Logs.Count(message => message == "Initializing staging database"), Is.EqualTo(1));
        }

        [Test]
        public void failed_staging_initialization_creates_schema_when_storage_becomes_available()
        {
            var folder = Path.Combine(_folder, "unavailable");
            var clock = new ManualTimeProvider();
            var context = new StagingDbContext(Stub<IAppFolderInfo>((_, _) => folder), _logger, clock);
            Assert.Throws<SqliteException>(() => context.InitializeDatabase());
            Directory.CreateDirectory(folder);
            clock.Advance(StagingDbContext.InitializationRetryInterval);

            using var connection = (SqliteConnection)context.OpenConnection();
            try
            {
                Assert.That(context.InitializationError, Is.Null);
                Assert.That(connection.Query<string>("SELECT name FROM sqlite_master WHERE type='table'"),
                    Does.Contain("ingest_queue").And.Contain("import_results"));
                var queue = new IngestQueueRepository(context, _logger);
                queue.InsertBatch(new List<IngestQueueItem> { new IngestQueueItem { Path = "recovered.epub", TagsJson = "{}", SizeBytes = 4, MtimeNs = 1 } });
                Assert.That(queue.GetQueueCount(), Is.EqualTo(1));
            }
            finally
            {
                SqliteConnection.ClearPool(connection);
            }
        }

        [Test]
        public void repeated_staging_access_during_an_outage_retries_once_per_interval_then_recovers()
        {
            var folder = Path.Combine(_folder, "unavailable-retry");
            var clock = new ManualTimeProvider();
            using var factory = new LogFactory();
            var messages = new NLog.Targets.MemoryTarget { Layout = "${message}" };
            var logging = new NLog.Config.LoggingConfiguration();
            logging.AddRuleForAllLevels(messages);
            factory.Configuration = logging;
            var context = new StagingDbContext(Stub<IAppFolderInfo>((_, _) => folder), factory.GetLogger("staging-retry"), clock);
            var firstFailure = Assert.Throws<SqliteException>(() => context.InitializeDatabase());
            for (var i = 0; i < 20; i++)
                Assert.That(Assert.Throws<SqliteException>(() => context.OpenConnection()), Is.SameAs(firstFailure));
            Assert.That(messages.Logs.Count(message => message == "Initializing staging database"), Is.EqualTo(1));
            clock.Advance(StagingDbContext.InitializationRetryInterval);
            Assert.Throws<SqliteException>(() => context.OpenConnection());
            Assert.That(messages.Logs.Count(message => message == "Initializing staging database"), Is.EqualTo(2));
            Directory.CreateDirectory(folder);
            clock.Advance(StagingDbContext.InitializationRetryInterval);
            using var connection = (SqliteConnection)context.OpenConnection();
            Assert.That(context.InitializationError, Is.Null);
            Assert.That(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM ingest_queue"), Is.Zero);
            SqliteConnection.ClearPool(connection);
        }

        [TestCase(false, false, 100)]
        [TestCase(false, true, 100)]
        [TestCase(true, false, 100)]
        [TestCase(true, true, 100)]
        [TestCase(false, true, 901)]
        [TestCase(true, true, 901)]
        public void deleted_book_queue_cleanup_batches_exact_owned_paths_and_preserves_colocated_files(bool postgres, bool deleteFiles, int trackCount)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                MediaFileService service = null;
                var events = Stub<IEventAggregator>((_, args) =>
                {
                    if (args[0] is BookDeletedEvent deleted) service.HandleAsync(deleted);
                    return null;
                });
                var author = new AuthorRepository(database, events, _logger).Insert(new Author
                {
                    Name = "Test Writer", CleanName = "testwriter", TitleSlug = "test-writer", Path = _folder, Added = DateTime.UtcNow
                });
                var books = new BookRepository(database, events);
                var editions = new EditionRepository(database, events);
                var files = new MediaFileRepository(database, events);
                Edition AddEdition(BookMediaType media, string slug)
                {
                    var book = books.Insert(new Book { AuthorId = author.Id, Title = slug, CleanTitle = slug,
                        TitleSlug = slug, MediaType = media, Added = DateTime.UtcNow });
                    return editions.Insert(new Edition { BookId = book.Id, Title = slug, ForeignEditionId = "local:" + slug,
                        ReadingFormatId = media == BookMediaType.Audiobook ? 2 : 3, Monitored = true });
                }

                var audio = AddEdition(BookMediaType.Audiobook, "the-lantern");
                var ebook = AddEdition(BookMediaType.Ebook, "the-lantern-ebook");
                var other = AddEdition(BookMediaType.Audiobook, "other-book");
                var selected = Enumerable.Range(1, trackCount).Select(index => new BookFile
                {
                    Path = Path.Combine(_folder, $"The Lantern - Part {index:0000}.mp3"), EditionId = audio.Id, MediaType = "audiobook"
                }).ToList();
                var neighborFiles = new List<BookFile>
                {
                    new BookFile { Path = Path.Combine(_folder, "The Lantern.epub"), EditionId = ebook.Id, MediaType = "ebook",
                        ReplicaPaths = new List<string> { Path.Combine(_folder, "The Lantern - copy.epub") } },
                    new BookFile { Path = Path.Combine(_folder, "Other Book.m4b"), EditionId = other.Id, MediaType = "audiobook" }
                };
                var storedFiles = selected.Concat(neighborFiles).ToList();
                storedFiles.ForEach(file =>
                {
                    file.Quality = new NzbDrone.Core.Qualities.QualityModel();
                    file.MediaInfo = new NzbDrone.Core.Parser.Model.MediaInfoModel();
                    file.DateAdded = file.Modified = DateTime.UtcNow;
                });
                files.InsertMany(storedFiles);
                var protectedPaths = neighborFiles.Select(file => file.Path).Concat(neighborFiles.SelectMany(file => file.ReplicaPaths))
                    .Concat(new[] { Path.Combine(_folder, "cover.jpg"), Path.Combine(_folder, "metadata.nfo"), selected[0].Path + ".jpg" }).ToArray();
                var allPaths = selected.Select(file => file.Path).Concat(protectedPaths).ToArray();
                foreach (var path in allPaths)
                {
                    File.WriteAllText(path, Path.GetFileName(path));
                }

                _queue.InsertBatch(allPaths.Select(path => new IngestQueueItem { Path = path, TagsJson = "{}", SizeBytes = 4, MtimeNs = 1 }).ToList());
                using (var connection = _context.OpenConnection())
                {
                    foreach (var path in new[] { selected[0].Path, protectedPaths[0], protectedPaths.Last() })
                    {
                        var id = connection.QuerySingle<int>("SELECT id FROM ingest_queue WHERE path=@path", new { path });
                        _queue.CompleteItemWithResult(id, path, ImportOutcome.Unmapped);
                    }
                }

                var opens = 0;
                var countedContext = Stub<IStagingDbContext>((method, args) =>
                {
                    Assert.That(method.Name, Is.EqualTo(nameof(IStagingDbContext.OpenConnection)));
                    opens++;
                    return _context.OpenConnection();
                });
                var queue = new IngestQueueRepository(countedContext, _logger);
                service = new MediaFileService(files, events, queue, null, _logger);
                var bookService = new BookService(books, new EditionService(editions, events, _logger), events,
                    Stub<IAuthorService>((method, _) => method.Name == nameof(IAuthorService.GetAuthor)
                        ? author : throw new NotSupportedException(method.Name)),
                    service, null, null, null, _logger,
                    deleteMediaFiles: new Lazy<IDeleteMediaFiles>(() => Stub<IDeleteMediaFiles>((method, _) =>
                        method.Name == nameof(IDeleteMediaFiles.DeleteBookFiles) ? selected.ToList() : throw new NotSupportedException(method.Name))));
                var timer = Stopwatch.StartNew();
                bookService.DeleteBook(audio.BookId, deleteFiles);
                timer.Stop();
                service.DeleteRecordsForAuthorDeletion(new List<BookFile>());

                using (var connection = _context.OpenConnection())
                {
                    Assert.That(connection.Query<string>("SELECT path FROM ingest_queue"), Is.EquivalentTo(protectedPaths));
                    Assert.That(connection.Query<string>("SELECT path FROM import_results"), Is.EquivalentTo(new[] { protectedPaths[0], protectedPaths.Last() }));
                }

                Assert.That(files.GetFilesByBook(ebook.BookId).Select(file => file.Id), Is.EqualTo(new[] { neighborFiles[0].Id }));
                Assert.That(files.GetFilesByBook(other.BookId).Select(file => file.Id), Is.EqualTo(new[] { neighborFiles[1].Id }));
                Assert.That(files.GetFileWithPath(selected.Select(file => file.Path).ToList()).Count, Is.EqualTo(deleteFiles ? 0 : trackCount));
                Assert.That(files.GetFilesByBook(audio.BookId), Is.Empty);
                foreach (var path in allPaths)
                {
                    Assert.That(File.ReadAllText(path), Is.EqualTo(Path.GetFileName(path)), "Queue cleanup cannot change file bytes");
                }

                TestContext.Progress.WriteLine($"Queue cleanup: main={(postgres ? "PostgreSQL" : "SQLite")}, files={trackCount}, delete={deleteFiles}, staging opens={opens}, elapsed={timer.Elapsed.TotalMilliseconds:F2}ms");
                Assert.That(opens, Is.EqualTo((trackCount + 899) / 900));
            });
        }

        [Test]
        public void batch_file_cleanup_attempts_unavailable_staging_once_and_keeps_main_cleanup_working()
        {
            var unavailable = new StagingDbContext(Stub<IAppFolderInfo>((_, _) => Path.Combine(_folder, "unavailable")), _logger);
            Assert.Throws<SqliteException>(() => unavailable.InitializeDatabase());
            var attempts = 0;
            var context = Stub<IStagingDbContext>((method, args) =>
            {
                attempts++;
                return unavailable.OpenConnection();
            });
            var selected = Enumerable.Range(1, 100).Select(index => new BookFile { Path = Path.Combine(_folder, $"Part {index}.mp3") }).ToList();
            var deletes = 0;
            var files = Stub<IMediaFileRepository>((method, args) =>
            {
                Assert.That(method.Name, Is.EqualTo(nameof(IMediaFileRepository.DeleteMany)));
                Assert.That(args[0], Is.SameAs(selected));
                deletes++;
                return null;
            });
            new MediaFileService(files, null, new IngestQueueRepository(context, _logger), null, _logger).DeleteRecordsForAuthorDeletion(selected);

            Assert.That(deletes, Is.EqualTo(1));
            Assert.That(unavailable.InitializationError, Is.Not.Null);
            Assert.That(attempts, Is.EqualTo(1));
        }

        private void VerifyHandoff(OrchestratorImportResult result, IMediaFileService files, IDiskProvider disk)
        {
            var scan = new DiskScanService(null, disk, null, files, null, _queue, null, null, null, null, null, null, _logger);
            typeof(DiskScanService).GetMethod("EnsureScannedFilesVisible", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(scan, new object[] { result, true });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void verified_main_database_handoff_releases_scratch_and_manual_retry_survives(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var repository = new MediaFileRepository(database, Stub<IEventAggregator>((method, args) => null));
                var reads = 0;
                var adds = 0;
                var files = Stub<IMediaFileService>((method, args) =>
                {
                    if (method.Name == nameof(IMediaFileService.GetFileWithPath))
                    {
                        reads++;
                        return repository.GetFileWithPath((List<string>)args[0]);
                    }

                    if (method.Name == nameof(IMediaFileService.AddMany))
                    {
                        adds++;
                        repository.InsertManyIgnoreDuplicatePaths((List<BookFile>)args[0]);
                    }

                    return null;
                });
                var disk = Stub<IDiskProvider>((method, args) => new FileSystem().FileInfo.FromFileName((string)args[0]));
                var paths = Enumerable.Range(1, 17).Select(id => AddItem("unmatched-" + id)).ToArray();
                var result = new OrchestratorImportResult
                {
                    ScannedFilePaths = paths.ToList(),
                    UnmappedFiles = paths.Select(path => new UnmappedFile { FilePath = path, Reason = "NO_MATCH" }).ToList(),
                    StagingResults = _queue.GetImportResults(paths)
                };
                VerifyHandoff(result, files, disk);
                var saved = repository.GetFileWithPath(paths.ToList());
                Assert.That(saved, Has.Count.EqualTo(17));
                Assert.That(saved.All(file => file.EditionId == 0), Is.True);
                using (var connection = _context.OpenConnection())
                {
                    Assert.That(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM ingest_queue"), Is.Zero);
                    Assert.That(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM import_results"), Is.Zero);
                }

                Assert.That(adds, Is.EqualTo(1));
                Assert.That(reads, Is.EqualTo(3));
                _context.InitializeDatabase();
                Stage(repository, paths, manual: false).GetAwaiter().GetResult();
                Assert.That(_queue.GetQueueCount(), Is.Zero, "Automatic scans must not repeat unchanged unmatched work");
                Stage(repository, paths, manual: true).GetAwaiter().GetResult();
                Assert.That(_queue.GetQueueCount(), Is.EqualTo(17), "Manual retry uses the main row even after all scratch history is gone");
            });
        }

        private async Task Stage(IMediaFileRepository files, string[] paths, bool manual, FolderType folderType = FolderType.Ebook, Logger logger = null)
        {
            var disk = Stub<IDiskProvider>((method, args) => method.Name switch
            {
                nameof(IDiskProvider.FolderExists) => true,
                nameof(IDiskProvider.GetDirectories) => Array.Empty<string>(),
                nameof(IDiskProvider.GetFiles) => paths,
                nameof(IDiskProvider.GetFileInfo) => new FileSystem().FileInfo.FromFileName((string)args[0]),
                _ => Default(method.ReturnType)
            });
            var commands = Stub<IManageCommandQueue>((method, args) => method.Name == nameof(IManageCommandQueue.Get)
                ? new CommandModel { Trigger = manual ? CommandTrigger.Manual : CommandTrigger.Scheduled }
                : Default(method.ReturnType));
            var orchestrator = (ImportOrchestratorV2)RuntimeHelpers.GetUninitializedObject(typeof(ImportOrchestratorV2));
            void Set(string name, object value) => typeof(ImportOrchestratorV2).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(orchestrator, value);
            Set("_diskProvider", disk);
            Set("_mediaFileRepository", files);
            Set("_ingestQueue", _queue);
            Set("_commandQueueManager", commands);
            Set("_logger", logger ?? _logger);
            Set("_eventAggregator", Stub<IEventAggregator>((method, args) => null));
            await (Task)typeof(ImportOrchestratorV2).GetMethod("StageFilesAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(orchestrator, new object[] { _folder, new RootFolder { Path = _folder, FolderType = folderType }, 123, null, FilterFilesType.Known, null });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void scheduled_retry_uses_saved_apply_failure_and_preserves_media_scope(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var repository = new MediaFileRepository(database, Stub<IEventAggregator>((method, args) => null));
                var failed = AddItem("failed");
                var unmatched = AddItem("unmatched");
                var otherMedia = Path.Combine(_folder, "other.m4b");
                File.WriteAllText(otherMedia, "audio");
                foreach (var path in new[] { failed, unmatched, otherMedia })
                {
                    var info = new FileInfo(path);
                    repository.InsertManyIgnoreDuplicatePaths(new List<BookFile> { new BookFile
                    {
                        Path = path, Size = info.Length, Modified = info.LastWriteTimeUtc, DateAdded = DateTime.UtcNow,
                        EditionId = 0, MediaType = path == otherMedia ? "audiobook" : "ebook",
                        Quality = new NzbDrone.Core.Qualities.QualityModel(),
                        MediaInfo = new NzbDrone.Core.Parser.Model.MediaInfoModel(),
                        MatchDetails = path == unmatched ? "NO_MATCH" : "APPLY_FAILED:write failed"
                    } });
                }

                Assert.That(repository.GetFileStatsWithBasePath(_folder), Has.Count.EqualTo(3));
                _queue.PurgeCompletedResults(_queue.GetImportResults().Select(result => result.Id));
                Stage(repository, new[] { failed, unmatched, otherMedia }, manual: false).GetAwaiter().GetResult();
                Assert.That(_queue.GetQueuedItems().Select(item => item.Path), Is.EqualTo(new[] { failed }));

                var queued = _queue.GetQueuedItems().Single();
                _queue.CompleteItemWithResult(queued.Id, failed, ImportOutcome.Unmapped, errorMessage: "NO_MATCH");
                var files = Stub<IMediaFileService>((method, args) =>
                {
                    if (method.Name == nameof(IMediaFileService.GetFileWithPath))
                    {
                        return repository.GetFileWithPath((List<string>)args[0]);
                    }

                    if (method.Name == nameof(IMediaFileService.Update))
                    {
                        repository.UpdateMany((List<BookFile>)args[0]);
                    }

                    return null;
                });
                var disk = Stub<IDiskProvider>((method, args) => new FileSystem().FileInfo.FromFileName((string)args[0]));
                VerifyHandoff(new OrchestratorImportResult
                {
                    ScannedFilePaths = new List<string> { failed },
                    UnmappedFiles = new List<UnmappedFile> { new UnmappedFile { FilePath = failed, Reason = "NO_MATCH" } },
                    StagingResults = _queue.GetImportResults(new[] { failed })
                }, files, disk);
                Assert.That(repository.GetFileWithPath(failed).MatchDetails, Is.EqualTo("NO_MATCH"));
                Stage(repository, new[] { failed, unmatched, otherMedia }, manual: false).GetAwaiter().GetResult();
                Assert.That(_queue.GetQueueCount(), Is.Zero, "An unmatched result must stop an earlier apply-failure retry");
            });
        }

        [Test]
        public void failed_main_handoff_keeps_scratch_for_recovery()
        {
            var path = AddItem("failed-save");
            var files = Stub<IMediaFileService>((method, args) => method.Name == nameof(IMediaFileService.GetFileWithPath) ? new List<BookFile>() : null);
            var disk = Stub<IDiskProvider>((method, args) => new FileSystem().FileInfo.FromFileName(path));
            var result = new OrchestratorImportResult { ScannedFilePaths = new List<string> { path }, StagingResults = _queue.GetImportResults() };
            Assert.Throws<TargetInvocationException>(() => VerifyHandoff(result, files, disk));
            Assert.That(_queue.GetImportResults(), Has.Count.EqualTo(1));
        }

        [Test]
        public void startup_recovery_keeps_work_when_main_database_cannot_accept_file()
        {
            var path = AddItem("cannot-save", null);
            var files = Stub<IMediaFileService>((method, args) => method.Name switch
            {
                nameof(IMediaFileService.GetFileWithPath) => args[0] is List<string> ? new List<BookFile>() : null,
                nameof(IMediaFileService.Add) => throw new InvalidOperationException("Main store unavailable"),
                _ => Default(method.ReturnType)
            });
            var disk = Stub<IDiskProvider>((method, args) => method.Name switch
            {
                nameof(IDiskProvider.GetFileInfo) => new FileSystem().FileInfo.FromFileName((string)args[0]),
                _ => Default(method.ReturnType)
            });
            var roots = Stub<IRootFolderService>((method, args) => new RootFolder { Path = _folder, FolderType = FolderType.Ebook });
            var sweeper = new StagingResidualQueueSweeper(_queue, files, disk, roots, _logger);
            Assert.That(sweeper.SweepAllResidualItems(), Is.Zero);
            Assert.That(_queue.GetQueuedItems().Single().Path, Is.EqualTo(path));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void startup_cleans_old_completed_work_only_after_main_database_represents_it(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var repository = new MediaFileRepository(database, Stub<IEventAggregator>((method, args) => null));
                var path = AddItem("old-completed");
                repository.InsertManyIgnoreDuplicatePaths(new List<BookFile> { new BookFile
                {
                    Path = path, Size = 4, Modified = DateTime.UtcNow, DateAdded = DateTime.UtcNow,
                    EditionId = 0, MediaType = "ebook", MatchDetails = "NO_MATCH",
                    Quality = new NzbDrone.Core.Qualities.QualityModel(),
                    MediaInfo = new NzbDrone.Core.Parser.Model.MediaInfoModel()
                } });
                var reads = 0;
                var files = Stub<IMediaFileService>((method, args) =>
                {
                    if (method.Name == nameof(IMediaFileService.GetFileWithPath) && args[0] is List<string> paths)
                    {
                        reads++;
                        return repository.GetFileWithPath(paths);
                    }

                    throw new InvalidOperationException("Unexpected per-file work: " + method.Name);
                });
                var sweeper = new StagingResidualQueueSweeper(_queue, files, null, null, _logger);
                sweeper.SweepAllResidualItems();
                Assert.That(reads, Is.EqualTo(1));
                Assert.That(repository.GetFileWithPath(path).MatchDetails, Is.EqualTo("NO_MATCH"));
                using var connection = _context.OpenConnection();
                Assert.That(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM ingest_queue"), Is.Zero);
                Assert.That(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM import_results"), Is.Zero);
            });
        }

        [Test]
        public void represented_inventory_survives_a_scratch_release_failure()
        {
            var path = AddItem("cleanup-failure");
            var results = _queue.GetImportResults();
            var files = Stub<IMediaFileService>((method, args) => method.Name == nameof(IMediaFileService.GetFileWithPath)
                ? new List<BookFile> { new BookFile { Id = 1, Path = path, EditionId = 42 } } : null);
            var disk = Stub<IDiskProvider>((method, args) => new FileSystem().FileInfo.FromFileName(path));
            var queue = Stub<IIngestQueueRepository>((method, args) => method.Name == nameof(IIngestQueueRepository.PurgeCompletedResults)
                ? throw new IOException("Scratch store unavailable") : Default(method.ReturnType));
            var scan = new DiskScanService(null, disk, null, files, null, queue, null, null, null, null, null, null, _logger);
            Assert.DoesNotThrow(() => typeof(DiskScanService).GetMethod("EnsureScannedFilesVisible", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(scan, new object[] { new OrchestratorImportResult { ScannedFilePaths = new List<string> { path }, StagingResults = results }, true }));
            Assert.That(_queue.GetImportResults().Single().Id, Is.EqualTo(results.Single().Id));
        }

        [Test]
        public void startup_keeps_unrepresented_completed_work_until_main_save_succeeds()
        {
            var path = AddItem("unrepresented-completed");
            var allowSave = false;
            BookFile saved = null;
            var files = Stub<IMediaFileService>((method, args) => method.Name switch
            {
                nameof(IMediaFileService.GetFileWithPath) => args[0] is List<string>
                    ? (saved == null ? new List<BookFile>() : new List<BookFile> { saved }) : saved,
                nameof(IMediaFileService.Add) => allowSave ? saved = (BookFile)args[0] : throw new IOException("Main store unavailable"),
                _ => Default(method.ReturnType)
            });
            var disk = Stub<IDiskProvider>((method, args) => new FileSystem().FileInfo.FromFileName((string)args[0]));
            var roots = Stub<IRootFolderService>((method, args) => new RootFolder { Path = _folder, FolderType = FolderType.Ebook });
            var sweeper = new StagingResidualQueueSweeper(_queue, files, disk, roots, _logger);
            sweeper.SweepAllResidualItems();
            Assert.That(_queue.GetImportResults().Single().Path, Is.EqualTo(path));
            Assert.That(saved, Is.Null);
            allowSave = true;
            sweeper.SweepAllResidualItems();
            Assert.That(saved?.Path, Is.EqualTo(path));
            Assert.That(_queue.GetImportResults(), Is.Empty);
        }

        [Test]
        public void startup_continues_after_a_failed_bulk_read_and_retains_that_batch()
        {
            var first = AddItem("first-batch");
            var second = AddItem("second-batch");
            var reads = 0;
            var files = Stub<IMediaFileService>((method, args) =>
            {
                Assert.That(method.Name, Is.EqualTo(nameof(IMediaFileService.GetFileWithPath)));
                reads++;
                if (reads == 1) { throw new IOException("Main store unavailable"); }
                return ((List<string>)args[0]).Select(path => new BookFile { Id = 1, Path = path }).ToList();
            });
            var sweeper = new StagingResidualQueueSweeper(_queue, files, null, null, _logger);
            Assert.DoesNotThrow(() => sweeper.SweepAllResidualItems(batchSize: 1));
            Assert.That(reads, Is.EqualTo(2));
            Assert.That(_queue.GetImportResults().Select(result => result.Path), Is.EqualTo(new[] { first }));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void summary_keeps_outcomes_when_an_imported_author_is_deleted(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var events = Stub<IEventAggregator>((method, args) => null);
                var authorRepository = new AuthorRepository(database, events, _logger);
                var authorService = (AuthorService)RuntimeHelpers.GetUninitializedObject(typeof(AuthorService));
                typeof(AuthorService).GetField("_authorRepository", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(authorService, authorRepository);
                var imported = AddItem("imported-deleted-author", ImportOutcome.Imported);
                var failed = AddItem("failed-write", ImportOutcome.Failed);
                var unmapped = AddItem("unmatched-summary", ImportOutcome.Unmapped);
                var paths = new[] { imported, failed, unmapped };
                using (var connection = _context.OpenConnection())
                {
                    connection.Execute("UPDATE import_results SET book_id=123, author_id=456 WHERE path=@imported", new { imported });
                }

                var disk = Stub<IDiskProvider>((method, args) => method.Name switch
                {
                    nameof(IDiskProvider.FolderExists) => true,
                    nameof(IDiskProvider.GetDirectories) => Array.Empty<string>(),
                    nameof(IDiskProvider.GetFiles) => paths,
                    nameof(IDiskProvider.GetFileInfo) => new FileSystem().FileInfo.FromFileName((string)args[0]),
                    _ => Default(method.ReturnType)
                });
                var repository = Stub<IMediaFileRepository>((method, args) => method.Name switch
                {
                    nameof(IMediaFileRepository.GetFilesWithBasePath) => paths.Select(path => new BookFile
                        { Path = path, EditionId = 1, Size = new FileInfo(path).Length, Modified = File.GetLastWriteTimeUtc(path) }).ToList(),
                    nameof(IMediaFileRepository.GetFileWithPath) => new List<BookFile> { new BookFile { Path = imported, EditionId = 1 } },
                    _ => Default(method.ReturnType)
                });
                var editions = Stub<IEditionService>((method, args) => method.Name == nameof(IEditionService.GetEditions)
                    ? new List<Edition> { new Edition { Id = 1, BookId = 123 } } : Default(method.ReturnType));
                var books = Stub<IBookService>((method, args) => method.Name == nameof(IBookService.GetExistingBooks)
                    ? new List<Book> { new Book { Id = 123, AuthorId = 456, Title = "Imported book" } } : Default(method.ReturnType));
                var discovery = Stub<IDiscoveryWorker>((method, args) => Task.FromResult(0));
                var orchestrator = new ImportOrchestratorV2(_queue, null, discovery, null, null, books, editions, authorService,
                    null, null, disk, null, null, repository, null, events, _logger);
                var result = orchestrator.ProcessFilesAsync(_folder, new RootFolder { Path = _folder, FolderType = FolderType.Ebook }).GetAwaiter().GetResult();
                Assert.Multiple(() =>
                {
                    Assert.That(result.StagingResults, Has.Count.EqualTo(3));
                    Assert.That(result.ImportedFiles.Single().AuthorName, Is.EqualTo("Unknown"));
                    Assert.That(result.FailedFiles.Single().FilePath, Is.EqualTo(failed));
                    Assert.That(result.UnmappedFiles.Single().FilePath, Is.EqualTo(unmapped));
                });

                var main = new MediaFileRepository(database, events);
                var files = Stub<IMediaFileService>((method, args) =>
                {
                    if (method.Name == nameof(IMediaFileService.GetFileWithPath)) { return main.GetFileWithPath((List<string>)args[0]); }
                    if (method.Name == nameof(IMediaFileService.AddMany)) { main.InsertManyIgnoreDuplicatePaths((List<BookFile>)args[0]); }
                    return Default(method.ReturnType);
                });
                VerifyHandoff(result, files, disk);
                Assert.That(main.GetFileWithPath(failed).MatchDetails, Does.StartWith("APPLY_FAILED:"));
                Assert.That(main.GetFileWithPath(unmapped).EditionId, Is.Zero);
                Assert.That(_queue.GetImportResults(), Is.Empty);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void tolerant_author_lookup_is_batched_and_preserves_strict_lookup(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using (var connection = database.OpenConnection())
                {
                    connection.Execute(@"INSERT INTO ""Authors"" (""Id"", ""Name"", ""Path"", ""Monitored"", ""Added"", ""CleanName"")" +
                        (postgres ? " OVERRIDING SYSTEM VALUE " : " ") + @"VALUES (1, 'Present author', @path, @monitored, @added, 'presentauthor'),
                               (1901, 'Later author', @path, @monitored, @added, 'laterauthor')",
                        new { path = _folder, monitored = false, added = DateTime.UtcNow });
                }

                var reads = 0;
                var measured = Stub<IMainDatabase>((method, args) => method.Name == nameof(IMainDatabase.OpenConnection)
                    ? CommandObserver.Wrap(database.OpenConnection(), command => reads++)
                    : method.Invoke(database, args));
                var authors = new AuthorRepository(measured, Stub<IEventAggregator>((method, args) => null), _logger);
                var ids = Enumerable.Range(1, 1901).Concat(new[] { 1, 1901 }).ToArray();
                Assert.That(authors.FindExisting(ids).Select(author => author.Id), Is.EquivalentTo(new[] { 1, 1901 }));
                Assert.That(reads, Is.EqualTo(postgres ? 1 : 3));
                Assert.Throws<ApplicationException>(() => authors.Get(ids));
            });
        }

        [TestCase(0)]
        [TestCase(2)]
        public void completed_scratch_is_released_when_file_is_gone_or_already_has_durable_rows(int rows)
        {
            var path = AddItem("residual");
            if (rows == 0) File.Delete(path);
            var files = Stub<IMediaFileService>((method, args) => Enumerable.Range(1, rows)
                .Select(id => new BookFile { Id = id, Path = path, EditionId = id }).ToList());
            var disk = Stub<IDiskProvider>((method, args) => new FileSystem().FileInfo.FromFileName((string)args[0]));
            new StagingResidualQueueSweeper(_queue, files, disk, null, _logger).SweepAllResidualItems(completedOnly: true);
            Assert.That(_queue.GetImportResults(), Is.Empty);
        }

        [Test]
        public void completed_scratch_is_retained_when_file_stat_throws()
        {
            var path = AddItem("unavailable");
            var files = Stub<IMediaFileService>((method, args) => new List<BookFile>());
            var disk = Stub<IDiskProvider>((method, args) => throw new IOException("share unavailable"));
            new StagingResidualQueueSweeper(_queue, files, disk, null, _logger).SweepAllResidualItems(completedOnly: true);
            Assert.That(_queue.GetImportResults().Single().Path, Is.EqualTo(path));
        }

        [Test]
        public void housekeeping_releases_only_represented_completed_work_and_preserves_active_work()
        {
            var done = AddItem("housekeeping-done");
            var unrepresented = AddItem("housekeeping-unrepresented");
            var queued = AddItem("housekeeping-queued", null);
            var claimed = AddItem("housekeeping-claimed", null);
            var claimedId = _queue.GetQueuedItems().Single(item => item.Path == claimed).Id;
            Assert.That(_queue.TryClaimItem(claimedId, out _), Is.True);
            var reads = 0;
            var activeReads = 0;
            var files = Stub<IMediaFileService>((method, args) =>
            {
                Assert.That(method.Name, Is.EqualTo(nameof(IMediaFileService.GetFileWithPath)));
                if (args[0] is string activePath)
                {
                    activeReads++;
                    return new BookFile { Id = 2, Path = activePath, EditionId = 42 };
                }

                reads++;
                return ((List<string>)args[0]).Where(path => path == done).Select(path => new BookFile { Id = 1, Path = path }).ToList();
            });
            var cleanedCache = false;
            var cache = Stub<IFileTagCacheRepository>((method, args) => { cleanedCache = true; return Default(method.ReturnType); });
            var disk = Stub<IDiskProvider>((method, args) => new FileSystem().FileInfo.FromFileName((string)args[0]));
            var roots = Stub<IRootFolderService>((method, args) => new RootFolder { Path = _folder, FolderType = FolderType.Ebook });
            var sweeper = new StagingResidualQueueSweeper(_queue, files, disk, roots, _logger);
            var cleanup = new CleanupImportData(cache, sweeper, _queue, _context);
            cleanup.Clean();
            Assert.That(cleanedCache, Is.True);
            Assert.That(reads, Is.EqualTo(1));
            Assert.That(activeReads, Is.Zero, "Housekeeping must not run abandoned-item recovery");
            Assert.That(_queue.GetImportResults().Single().Path, Is.EqualTo(unrepresented));
            Assert.That(_queue.GetActiveItems().Select(item => item.Path), Is.EquivalentTo(new[] { queued, claimed }));
            Assert.That(_queue.GetActiveItems().Single(item => item.Path == claimed).Status, Is.EqualTo("in_progress"));
        }

        [Test]
        public void housekeeping_compacts_only_an_empty_scratch_database()
        {
            using (var connection = _context.OpenConnection())
            {
                connection.Execute("CREATE TABLE vacuum_probe (bytes BLOB); INSERT INTO vacuum_probe VALUES (zeroblob(1048576)); DROP TABLE vacuum_probe;");
                Assert.That(connection.ExecuteScalar<int>("PRAGMA freelist_count"), Is.GreaterThan(0));
            }

            var cache = Stub<IFileTagCacheRepository>((method, args) => Default(method.ReturnType));
            var cleanup = new CleanupImportData(cache, new StagingResidualQueueSweeper(_queue, null, null, null, _logger), _queue, _context);
            var active = AddItem("active-vacuum", null);
            cleanup.Clean();
            using (var connection = _context.OpenConnection())
            {
                Assert.That(connection.ExecuteScalar<int>("PRAGMA freelist_count"), Is.GreaterThan(0));
            }

            _queue.PurgePaths(new[] { active });
            cleanup.Clean();
            using (var connection = _context.OpenConnection())
            {
                Assert.That(connection.ExecuteScalar<int>("PRAGMA freelist_count"), Is.Zero);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void housekeeping_and_scan_cannot_consume_each_others_results(bool scanFirst)
        {
            var queue = new CommandQueue();
            var scan = new CommandModel { Id = 1, Body = new RescanFoldersCommand(), Status = CommandStatus.Queued };
            var cleanup = new CommandModel { Id = 2, Body = new HousekeepingCommand(), Status = CommandStatus.Queued };
            var first = scanFirst ? scan : cleanup;
            var second = scanFirst ? cleanup : scan;
            queue.Add(first);
            Assert.That(queue.TryGet(out var started), Is.True);
            Assert.That(started, Is.SameAs(first));
            queue.Add(second);
            Assert.That(queue.TryGet(out _), Is.False);
            first.Status = CommandStatus.Completed;
            Assert.That(queue.TryGet(out started), Is.True);
            Assert.That(started, Is.SameAs(second));
        }

        [Test]
        public void scratch_cleanup_failure_does_not_abort_later_root_scans()
        {
            var first = AddItem("root-first");
            var second = AddItem("root-second");
            var folders = new List<string> { Path.Combine(_folder, "first"), Path.Combine(_folder, "second") };
            var visited = new List<string>();
            var orchestrator = Stub<IImportOrchestrator>((method, args) =>
            {
                var folder = (string)args[0];
                visited.Add(folder);
                var path = folder == folders[0] ? first : second;
                return Task.FromResult(new OrchestratorImportResult
                {
                    ScannedFilePaths = new List<string> { path },
                    StagingResults = _queue.GetImportResults(new[] { path })
                });
            });
            var files = Stub<IMediaFileService>((method, args) => ((List<string>)args[0])
                .Select(path => new BookFile { Id = 1, Path = path, EditionId = 42 }).ToList());
            var roots = Stub<IRootFolderService>((method, args) => new RootFolder { Path = (string)args[0], FolderType = FolderType.Ebook });
            var disk = Stub<IDiskProvider>((method, args) => method.Name switch
            {
                nameof(IDiskProvider.FolderExists) => true,
                nameof(IDiskProvider.GetFileInfo) => new FileSystem().FileInfo.FromFileName((string)args[0]),
                _ => Default(method.ReturnType)
            });
            var queue = Stub<IIngestQueueRepository>((method, args) => throw new IOException("Scratch store unavailable"));
            var authors = Stub<IAuthorService>((method, args) => method.Name == nameof(IAuthorService.GetExistingAuthors)
                ? new List<Author>() : throw new ApplicationException("Requested author no longer exists"));
            var scan = new DiskScanService(null, disk, null, files, null, queue, orchestrator, authors, roots, null, null,
                Stub<IEventAggregator>((method, args) => null), _logger);
            Assert.DoesNotThrow(() => scan.Scan(folders, authorIds: new List<int> { 456 }));
            Assert.That(visited, Is.EqualTo(folders));
            Assert.That(_queue.GetImportResults(), Has.Count.EqualTo(2));
        }

        [Test]
        public void staging_read_failure_is_not_reported_as_an_empty_queue()
        {
            using var factory = new LogFactory();
            var target = new NLog.Targets.MemoryTarget { Layout = "${level}|${message}" };
            var config = new NLog.Config.LoggingConfiguration();
            config.AddRule(LogLevel.Info, LogLevel.Fatal, target);
            factory.Configuration = config;
            var logger = factory.GetLogger("staging-read-failure");
            using (var connection = _context.OpenConnection())
            {
                connection.Execute("ALTER TABLE ingest_queue RENAME TO unavailable_queue");
            }

            Assert.Throws<SqliteException>(() => _queue.GetActiveItems());
            var sweeper = new StagingResidualQueueSweeper(_queue, null, null, null, logger);
            Assert.DoesNotThrow(() => sweeper.SweepAllResidualItems());
            Assert.That(target.Logs.Any(line => line.StartsWith("Warn|") && line.Contains("remaining work retained")), Is.True);
            Assert.That(target.Logs.Any(line => line.Contains("failed batches: 1")), Is.True);
        }

        [Test]
        public void completed_result_must_belong_to_the_observed_queue_item()
        {
            var path = AddItem("receipt-identity");
            var queue = Stub<IIngestQueueRepository>((method, args) =>
            {
                var value = method.Invoke(_queue, args);
                if (method.Name == nameof(IIngestQueueRepository.GetImportResults))
                {
                    foreach (var result in (List<NzbDrone.Core.Datastore.ImportResult>)value) { result.QueueItemId += 100; }
                }

                return value;
            });
            var files = Stub<IMediaFileService>((method, args) => new List<BookFile> { new BookFile { Id = 1, Path = path } });
            var sweeper = new StagingResidualQueueSweeper(queue, files, null, null, _logger);
            sweeper.SweepAllResidualItems(completedOnly: true);
            Assert.That(_queue.GetImportResults().Single().Path, Is.EqualTo(path));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void staging_logs_retry_counts_at_info(bool manual)
        {
            using var factory = new LogFactory();
            var target = new NLog.Targets.MemoryTarget { Layout = "${level}|${message}" };
            var config = new NLog.Config.LoggingConfiguration();
            config.AddRule(LogLevel.Info, LogLevel.Fatal, target);
            factory.Configuration = config;
            var ordinary = AddItem("ordinary-retry-log");
            var failed = AddItem("failed-retry-log");
            var paths = new[] { ordinary, failed };
            var files = Stub<IMediaFileRepository>((method, args) => paths.Select(path => new BookFile
            {
                Path = path, EditionId = 0, Modified = File.GetLastWriteTimeUtc(path), Size = new FileInfo(path).Length,
                MatchDetails = path == failed ? "APPLY_FAILED:write failed" : "NO_MATCH"
            }).ToList());
            Stage(files, paths, manual, logger: factory.GetLogger("retry-counts")).GetAwaiter().GetResult();
            Assert.That(target.Logs.Any(line => line.StartsWith("Info|") &&
                line.Contains(manual ? "manual unmatched retries: 2, scheduled apply-failure retries: 0"
                                     : "manual unmatched retries: 0, scheduled apply-failure retries: 1")), Is.True);
        }

        [Test]
        public void cleanup_preserves_unconsumed_other_active_and_newer_attempts()
        {
            var completed = AddItem("completed");
            var retry = AddItem("retry");
            var newer = AddItem("newer");
            var other = AddItem("other");
            AddItem("active", null);
            var results = _queue.GetImportResults(new[] { completed, retry, newer });
            var retryId = results.Single(result => result.Path == retry).QueueItemId;
            _queue.UpdateStatus(retryId, "in_progress");
            var newerId = results.Single(result => result.Path == newer).QueueItemId;
            _queue.CompleteItemWithResult(newerId, newer, ImportOutcome.Failed);
            Assert.That(_queue.PurgeCompletedResults(results.Select(result => result.Id)), Is.EqualTo(1));
            Assert.That(_queue.GetImportResults().Select(result => result.Path), Is.EquivalentTo(new[] { newer, other }));
            Assert.That(_queue.GetQueueCount(), Is.EqualTo(2));
        }

        [Test]
        public void results_are_complete_batched_and_latest_only_above_old_summary_cap()
        {
            using (var connection = _context.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                for (var id = 1; id <= 2501; id++)
                {
                    var path = Path.Combine(_folder, id + ".epub");
                    connection.Execute("INSERT INTO ingest_queue(id,path,mtime_ns,size_bytes,tags_json,status,created_at,updated_at) VALUES(@id,@path,1,1,'{}','done',1,1)", new { id, path }, transaction);
                    connection.Execute("INSERT INTO import_results(queue_item_id,path,outcome,created_at) VALUES(@id,@path,'failed',1),(@id,@path,'unmapped',1)", new { id, path }, transaction);
                }

                transaction.Commit();
            }

            var stopwatch = Stopwatch.StartNew();
            var results = _queue.GetImportResults(Enumerable.Range(1, 2501).Select(id => Path.Combine(_folder, id + ".epub")).ToArray());
            Assert.That(results, Has.Count.EqualTo(2501));
            Assert.That(results.All(result => result.Outcome == ImportOutcome.Unmapped), Is.True);
            Assert.That(_queue.PurgeCompletedResults(results.Select(result => result.Id)), Is.EqualTo(2501));
            TestContext.WriteLine("Read and released 2501 completed files in {0}ms", stopwatch.ElapsedMilliseconds);
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void old_cleanup_receipt_cannot_delete_later_work_after_queue_empties(bool legacySchema, bool samePath)
        {
            var path = AddItem("first");
            if (legacySchema)
            {
                using var connection = _context.OpenConnection();
                var schema = connection.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE name='import_results'");
                connection.Execute("ALTER TABLE import_results RENAME TO old_results");
                connection.Execute(schema.Replace(" AUTOINCREMENT", string.Empty));
                connection.Execute("INSERT INTO import_results SELECT * FROM old_results; DROP TABLE old_results;");
            }

            _context.InitializeDatabase();
            var receipt = _queue.GetImportResults(new[] { path }).Select(result => result.Id).ToArray();
            Assert.That(_queue.PurgeCompletedResults(receipt), Is.EqualTo(1));
            var later = AddItem(samePath ? "first" : "later");
            Assert.That(_queue.PurgeCompletedResults(receipt), Is.Zero, "A delayed second consumer must not release a different completed attempt");
            Assert.That(_queue.GetImportResults().Single().Path, Is.EqualTo(later));
        }
    }
}
