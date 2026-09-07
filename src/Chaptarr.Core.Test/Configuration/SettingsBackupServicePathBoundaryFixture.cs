using System;
using System.Net;
using System.Reflection;
using NUnit.Framework;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Configuration.SettingsBackups;
using NzbDrone.Core.Exceptions;

namespace Chaptarr.Core.Test.Configuration
{
    [TestFixture]
    public class SettingsBackupServicePathBoundaryFixture
    {
        private string _originalAllowedRoots;

        [SetUp]
        public void SetUp()
        {
            _originalAllowedRoots = Environment.GetEnvironmentVariable("CHAPTARR_SETTINGS_BACKUP_ALLOWED_ROOTS");
            Environment.SetEnvironmentVariable("CHAPTARR_SETTINGS_BACKUP_ALLOWED_ROOTS", null);
        }

        [TearDown]
        public void TearDown()
        {
            Environment.SetEnvironmentVariable("CHAPTARR_SETTINGS_BACKUP_ALLOWED_ROOTS", _originalAllowedRoots);
        }

        [Test]
        public void should_accept_directdownload_backup_root_within_allowlisted_mount()
        {
            var service = CreateService("/appdata/chaptarr");

            var normalizedRoot = InvokeNormalizeAndValidateRoot(service, "/downloads/direct-download-backups");

            Assert.That(normalizedRoot, Is.EqualTo("/downloads/direct-download-backups"));
        }

        [Test]
        public void should_reject_directdownload_backup_root_that_only_shares_an_allowlisted_prefix()
        {
            var service = CreateService("/appdata/chaptarr");

            var exception = Assert.Throws<TargetInvocationException>(() => InvokeNormalizeAndValidateRoot(service, "/downloads-direct-download-backups"));

            AssertBoundaryException(exception, HttpStatusCode.BadRequest, "not an allowed backup location");
        }

        [Test]
        public void should_accept_directdownload_restore_file_within_env_allowlisted_boundary()
        {
            Environment.SetEnvironmentVariable("CHAPTARR_SETTINGS_BACKUP_ALLOWED_ROOTS", "/srv/direct-download-restore");
            var service = CreateService("/appdata/chaptarr");

            var normalizedFilePath = InvokeNormalizeAndValidateFilePath(
                service,
                "/srv/direct-download-restore/session/direct.chaptarr-settings-backup.json");

            Assert.That(normalizedFilePath, Is.EqualTo("/srv/direct-download-restore/session/direct.chaptarr-settings-backup.json"));
        }

        [Test]
        public void should_reject_directdownload_restore_file_outside_allowlisted_boundary_even_when_extension_matches()
        {
            Environment.SetEnvironmentVariable("CHAPTARR_SETTINGS_BACKUP_ALLOWED_ROOTS", "/srv/direct-download-restore");
            var service = CreateService("/appdata/chaptarr");

            var exception = Assert.Throws<TargetInvocationException>(() => InvokeNormalizeAndValidateFilePath(
                service,
                "/srv/direct-download-restore-archive/direct.chaptarr-settings-backup.json"));

            AssertBoundaryException(exception, HttpStatusCode.BadRequest, "not an allowed backup location");
        }

        [Test]
        public void should_keep_backup_file_extension_gate_when_directdownload_restore_path_is_allowlisted()
        {
            Environment.SetEnvironmentVariable("CHAPTARR_SETTINGS_BACKUP_ALLOWED_ROOTS", "/srv/direct-download-restore");
            var service = CreateService("/appdata/chaptarr");

            var exception = Assert.Throws<TargetInvocationException>(() => InvokeNormalizeAndValidateFilePath(
                service,
                "/srv/direct-download-restore/session/direct.json"));

            AssertBoundaryException(exception, HttpStatusCode.BadRequest, "Backup file must end with '.chaptarr-settings-backup.json'");
        }

        private static SettingsBackupService CreateService(string appDataPath)
        {
            return new SettingsBackupService(
                new TestAppFolderInfo(appDataPath),
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null);
        }

        private static string InvokeNormalizeAndValidateRoot(SettingsBackupService service, string rootFolder)
        {
            var method = typeof(SettingsBackupService).GetMethod("NormalizeAndValidateRoot", BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.That(method, Is.Not.Null);

            return (string)method.Invoke(service, new object[] { rootFolder });
        }

        private static string InvokeNormalizeAndValidateFilePath(SettingsBackupService service, string filePath)
        {
            var method = typeof(SettingsBackupService).GetMethod("NormalizeAndValidateFilePath", BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.That(method, Is.Not.Null);

            return (string)method.Invoke(service, new object[] { filePath });
        }

        private static void AssertBoundaryException(TargetInvocationException exception, HttpStatusCode statusCode, string expectedMessageFragment)
        {
            Assert.That(exception?.InnerException, Is.InstanceOf<NzbDroneClientException>());

            var clientException = (NzbDroneClientException)exception.InnerException;
            Assert.That(clientException.StatusCode, Is.EqualTo(statusCode));
            Assert.That(clientException.Message, Does.Contain(expectedMessageFragment));
        }

        private sealed class TestAppFolderInfo : IAppFolderInfo
        {
            private readonly string _appDataPath;

            public TestAppFolderInfo(string appDataPath)
            {
                _appDataPath = appDataPath;
            }

            public string StartUpFolder => "/startup";
            public string AppDataFolder => _appDataPath;
            public string UserHomeFolder => "/home/test";
            public string TempFolder => "/tmp";

            public string GetLogPath() => "/logs";
            public string GetUpdatePackageFolder() => "/updates";
            public string GetUpdateClientFolder() => "/updates/client";
            public string GetConfigPath() => "/config/config.xml";
            public string GetMediaCoverPath() => "/config/MediaCover";
            public string GetInternalTempPath() => "/tmp/internal";
            public string GetCachePath() => "/cache";
            public string GetLocalAppDataPath() => "/localappdata";
            public string GetMetadataRootFolder() => "/metadata";
            public string GetAppDataPath() => _appDataPath;
        }
    }
}
