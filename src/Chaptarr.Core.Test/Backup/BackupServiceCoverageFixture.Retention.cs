using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using NzbDrone.Core.Backup;
using NzbDrone.Core.Datastore;

namespace Chaptarr.Core.Test.Backup
{
    public partial class BackupServiceCoverageFixture
    {
        [Test]
        public void retention_removes_only_generated_names_and_keeps_renamed_backups_listed()
        {
            var folders = new AppFolders(Path.Combine(_root, "retention"));
            var service = CreateService(new MainDatabase(null), folders);
            var folder = service.GetBackupFolder(BackupType.Scheduled);
            Directory.CreateDirectory(folder);
            var generated = Path.Combine(folder, "chaptarr_backup_v0.9.965_2026.01.01_00.00.00.zip");
            var renamed = Path.Combine(folder, "chaptarr_backup_before-upgrade.zip");
            foreach (var path in new[] { generated, renamed })
            {
                File.WriteAllText(path, "saved backup");
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddYears(-10));
            }

            typeof(BackupService).GetMethod("CleanupOldBackups", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(service, new object[] { BackupType.Scheduled });

            Assert.That(File.Exists(generated), Is.False);
            Assert.That(File.ReadAllText(renamed), Is.EqualTo("saved backup"));
            Assert.That(service.GetBackups().Single().Name, Is.EqualTo(Path.GetFileName(renamed)));
        }

    }
}
