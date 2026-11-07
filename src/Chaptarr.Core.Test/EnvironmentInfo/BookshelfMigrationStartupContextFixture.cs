using System;
using NUnit.Framework;
using NzbDrone.Common;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Host;

namespace Chaptarr.Core.Test.EnvironmentInfo
{
    [TestFixture]
    public class BookshelfMigrationStartupContextFixture
    {
        [TestCase("-migrate-bookshelf", true, false, false, false, false, false, false, false)]
        [TestCase("--preview-bookshelf-migration", false, true, false, false, false, false, false, false)]
        [TestCase("--restore-bookshelf", false, false, true, false, false, false, false, false)]
        [TestCase("--preview-bookshelf-restore", false, false, false, true, false, false, false, false)]
        [TestCase("--confirm-bookshelf-service-stopped", false, false, false, false, true, false, false, false)]
        [TestCase("--check-bookshelf-postgres-backup", false, false, false, false, false, true, false, false)]
        [TestCase("--backup-bookshelf-postgres", false, false, false, false, false, false, true, false)]
        [TestCase("--restore-bookshelf-postgres", false, false, false, false, false, false, false, true)]
        public void should_parse_a_single_bookshelf_maintenance_flag(
            string argument,
            bool migrate,
            bool previewMigration,
            bool restore,
            bool previewRestore,
            bool confirmServiceStopped,
            bool checkPostgresBackup,
            bool backupPostgres,
            bool restorePostgres)
        {
            var context = new StartupContext(argument);

            Assert.That(context.MigrateBookshelf, Is.EqualTo(migrate));
            Assert.That(context.PreviewBookshelfMigration, Is.EqualTo(previewMigration));
            Assert.That(context.RestoreBookshelf, Is.EqualTo(restore));
            Assert.That(context.PreviewBookshelfRestore, Is.EqualTo(previewRestore));
            Assert.That(context.ConfirmBookshelfServiceStopped, Is.EqualTo(confirmServiceStopped));
            Assert.That(context.CheckBookshelfPostgresBackup, Is.EqualTo(checkPostgresBackup));
            Assert.That(context.BackupBookshelfPostgres, Is.EqualTo(backupPostgres));
            Assert.That(context.RestoreBookshelfPostgres, Is.EqualTo(restorePostgres));
        }

        [TestCase("--check-bookshelf-postgres-backup", ApplicationModes.CheckBookshelfPostgresBackup)]
        [TestCase("--backup-bookshelf-postgres", ApplicationModes.BackupBookshelfPostgres)]
        [TestCase("--restore-bookshelf-postgres", ApplicationModes.RestoreBookshelfPostgres)]
        public void should_route_a_postgres_backup_utility_flag(string argument, ApplicationModes expectedMode)
        {
            Assert.That(Bootstrap.GetApplicationMode(new StartupContext(argument)), Is.EqualTo(expectedMode));
        }

        [Test]
        public void should_reject_multiple_postgres_backup_utility_commands()
        {
            var context = new StartupContext("--check-bookshelf-postgres-backup", "--backup-bookshelf-postgres");

            Assert.Throws<InvalidOperationException>(() => Bootstrap.GetApplicationMode(context));
        }

        [Test]
        public void should_preserve_a_data_path_with_spaces_for_service_installation()
        {
            var context = new StartupContext("-data=\"C:\\ProgramData\\Readarr Libraries\"", "-nobrowser");

            Assert.That(context.PreservedArguments, Is.EqualTo("/data=\"C:\\ProgramData\\Readarr Libraries\" /nobrowser"));
        }

        [Test]
        public void should_quote_the_service_image_path_as_a_single_windows_argument()
        {
            Assert.That(
                ServiceProvider.BuildServiceImagePathArgument(
                    "C:\\Program Files\\Chaptarr\\Chaptarr.Console.exe",
                    "/data=\"C:\\ProgramData\\Readarr Libraries\" /nobrowser"),
                Is.EqualTo("\"\\\"C:\\Program Files\\Chaptarr\\Chaptarr.Console.exe\\\" /data=\\\"C:\\ProgramData\\Readarr Libraries\\\" /nobrowser\""));
        }
    }
}
