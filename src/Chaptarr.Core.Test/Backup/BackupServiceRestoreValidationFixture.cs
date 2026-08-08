using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Backup;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore;

namespace Chaptarr.Core.Test.Backup
{
    [TestFixture]
    public class BackupServiceRestoreValidationFixture
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

        private sealed class TestArchiveService : IArchiveService
        {
            public void Extract(string compressedFile, string destination)
            {
                File.WriteAllText(Path.Combine(destination, "Config.xml"), "replacement");
                File.WriteAllText(Path.Combine(destination, "readarr.db"), "invalid");
            }

            public void CreateZip(string path, IEnumerable<string> files) => throw new NotImplementedException();
        }

        private sealed class RejectingRestoreDatabase : IRestoreDatabase
        {
            public List<string> ValidatedPaths { get; } = new();

            public void Validate(string path)
            {
                ValidatedPaths.Add(path);
                throw new InvalidDataException("invalid restore");
            }

            public bool Restore() => throw new NotImplementedException();
            public void Commit() => throw new NotImplementedException();
            public void Rollback() => throw new NotImplementedException();
        }

        private class ThrowingProxy<T> : DispatchProxy where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"Test proxy does not implement {typeof(T).Name}.{targetMethod?.Name}");
            }
        }

        private string _tempFolder;
        private TestAppFolderInfo _appFolderInfo;
        private RejectingRestoreDatabase _restoreDatabase;
        private BackupService _subject;

        [SetUp]
        public void SetUp()
        {
            _tempFolder = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"backup_restore_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempFolder);
            _appFolderInfo = new TestAppFolderInfo(_tempFolder);
            _restoreDatabase = new RejectingRestoreDatabase();
            _subject = new BackupService(
                new MainDatabase(null),
                DispatchProxy.Create<IMakeDatabaseBackup, ThrowingProxy<IMakeDatabaseBackup>>(),
                DispatchProxy.Create<IDiskTransferService, ThrowingProxy<IDiskTransferService>>(),
                new TestDiskProvider(new FileSystem()),
                _appFolderInfo,
                new TestArchiveService(),
                DispatchProxy.Create<IConfigService, ThrowingProxy<IConfigService>>(),
                _restoreDatabase,
                LogManager.GetCurrentClassLogger());
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
        public void should_validate_extracted_database_before_replacing_config()
        {
            var configPath = _appFolderInfo.GetConfigPath();
            File.WriteAllText(configPath, "original");

            Assert.Throws<RestoreBackupFailedException>(() => _subject.Restore(Path.Combine(_tempFolder, "backup.zip")));

            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(configPath), Is.EqualTo("original"));
                Assert.That(File.Exists(_appFolderInfo.GetDatabaseRestore()), Is.False);
                Assert.That(_restoreDatabase.ValidatedPaths, Has.Exactly(1).Items);
            });
        }

        [Test]
        public void should_validate_raw_database_before_staging_restore()
        {
            var uploadPath = Path.Combine(_tempFolder, "uploaded.db");
            File.WriteAllText(uploadPath, "invalid");

            Assert.Throws<RestoreBackupFailedException>(() => _subject.Restore(uploadPath));

            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(uploadPath), Is.EqualTo("invalid"));
                Assert.That(File.Exists(_appFolderInfo.GetDatabaseRestore()), Is.False);
                Assert.That(_restoreDatabase.ValidatedPaths, Is.EqualTo(new[] { uploadPath }));
            });
        }
    }
}
