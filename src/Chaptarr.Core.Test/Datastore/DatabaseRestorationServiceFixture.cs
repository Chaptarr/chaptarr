using System;
using System.IO;
using System.IO.Abstractions;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Datastore;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    public class DatabaseRestorationServiceFixture
    {
        private sealed class TestDiskProvider : DiskProviderBase
        {
            public TestDiskProvider(IFileSystem fileSystem)
                : base(fileSystem)
            {
            }

            public override long? GetAvailableSpace(string path) => throw new NotImplementedException();
            public override void InheritFolderPermissions(string filename) => throw new NotImplementedException();
            public override void SetEveryonePermissions(string filename) => throw new NotImplementedException();
            public override void SetFilePermissions(string path, string mask, string group) => throw new NotImplementedException();
            public override void SetPermissions(string path, string mask, string group) => throw new NotImplementedException();
            public override void CopyPermissions(string sourcePath, string targetPath) => throw new NotImplementedException();
            public override long? GetTotalSize(string path) => throw new NotImplementedException();
            public override bool TryCreateHardLink(string source, string destination) => throw new NotImplementedException();
        }

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

        private string _tempFolder;
        private TestAppFolderInfo _appFolderInfo;
        private DatabaseRestorationService _subject;

        [SetUp]
        public void SetUp()
        {
            _tempFolder = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"database_restore_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempFolder);
            _appFolderInfo = new TestAppFolderInfo(_tempFolder);
            _subject = new DatabaseRestorationService(new TestDiskProvider(new FileSystem()), _appFolderInfo);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempFolder))
            {
                Directory.Delete(_tempFolder, true);
            }
        }

        [Test]
        public void should_reject_a_readarr_database_without_touching_the_live_database()
        {
            CreateChaptarrDatabase(_appFolderInfo.GetDatabase(), "live");
            CreateReadarrDatabase(_appFolderInfo.GetDatabaseRestore());

            Assert.Throws<InvalidDataException>(() => _subject.Restore());

            Assert.Multiple(() =>
            {
                Assert.That(ReadMarker(_appFolderInfo.GetDatabase()), Is.EqualTo("live"));
                Assert.That(File.Exists(_appFolderInfo.GetDatabaseRestore()), Is.False);
                Assert.That(File.Exists(_appFolderInfo.GetDatabaseRestore() + ".failed"), Is.True);
            });
        }

        [Test]
        public void should_reject_a_corrupt_restore_without_touching_the_live_database()
        {
            CreateChaptarrDatabase(_appFolderInfo.GetDatabase(), "live");
            File.WriteAllText(_appFolderInfo.GetDatabaseRestore(), "not a database");

            Assert.Throws<SqliteException>(() => _subject.Restore());

            Assert.Multiple(() =>
            {
                Assert.That(ReadMarker(_appFolderInfo.GetDatabase()), Is.EqualTo("live"));
                Assert.That(File.Exists(_appFolderInfo.GetDatabaseRestore()), Is.False);
                Assert.That(File.Exists(_appFolderInfo.GetDatabaseRestore() + ".failed"), Is.True);
            });
        }

        [Test]
        public void should_restore_the_original_database_when_candidate_migration_fails()
        {
            CreateChaptarrDatabase(_appFolderInfo.GetDatabase(), "live");
            CreateChaptarrDatabase(_appFolderInfo.GetDatabaseRestore(), "candidate");

            Assert.That(_subject.Restore(), Is.True);
            Assert.That(ReadMarker(_appFolderInfo.GetDatabase()), Is.EqualTo("candidate"));

            _subject.Rollback();

            Assert.Multiple(() =>
            {
                Assert.That(ReadMarker(_appFolderInfo.GetDatabase()), Is.EqualTo("live"));
                Assert.That(ReadMarker(_appFolderInfo.GetDatabase() + ".failed-restore"), Is.EqualTo("candidate"));
                Assert.That(File.Exists(_appFolderInfo.GetDatabase() + ".pre-restore"), Is.False);
            });
        }

        [Test]
        public void should_discard_the_saved_original_only_after_restore_is_committed()
        {
            CreateChaptarrDatabase(_appFolderInfo.GetDatabase(), "live");
            CreateChaptarrDatabase(_appFolderInfo.GetDatabaseRestore(), "candidate");

            Assert.That(_subject.Restore(), Is.True);
            Assert.That(ReadMarker(_appFolderInfo.GetDatabase() + ".pre-restore"), Is.EqualTo("live"));

            _subject.Commit();

            Assert.Multiple(() =>
            {
                Assert.That(ReadMarker(_appFolderInfo.GetDatabase()), Is.EqualTo("candidate"));
                Assert.That(File.Exists(_appFolderInfo.GetDatabase() + ".pre-restore"), Is.False);
            });
        }

        private static void CreateChaptarrDatabase(string path, string marker)
        {
            using var connection = Open(path);
            Execute(connection, @"
                CREATE TABLE Authors (
                    Id INTEGER PRIMARY KEY,
                    AudiobookRootFolderPath TEXT NULL,
                    EbookRootFolderPath TEXT NULL
                );
                CREATE TABLE RestoreMarker (Value TEXT NOT NULL);
                INSERT INTO RestoreMarker (Value) VALUES ($marker);",
                marker);
        }

        private static void CreateReadarrDatabase(string path)
        {
            using var connection = Open(path);
            Execute(connection, @"
                CREATE TABLE Authors (Id INTEGER PRIMARY KEY);
                CREATE TABLE Books (Id INTEGER PRIMARY KEY);");
        }

        private static SqliteConnection Open(string path)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString());
            connection.Open();
            return connection;
        }

        private static void Execute(SqliteConnection connection, string sql, string marker = null)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;

            if (marker != null)
            {
                command.Parameters.AddWithValue("$marker", marker);
            }

            command.ExecuteNonQuery();
        }

        private static string ReadMarker(string path)
        {
            using var connection = Open(path);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Value FROM RestoreMarker;";
            return command.ExecuteScalar()?.ToString();
        }
    }
}
