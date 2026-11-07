using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using NzbDrone.Core.Datastore;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    public class BookshelfMigrationJournalFixture
    {
        private string _directory;
        private string _dataDirectory;
        private string _journalDirectory;
        private BookshelfMigrationJournal _journal;

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"bookshelf-journal-{Guid.NewGuid():N}");
            _dataDirectory = Path.Combine(_directory, "data");
            Directory.CreateDirectory(_dataDirectory);
            _journalDirectory = Path.Combine(_dataDirectory, ".chaptarrng-bookshelfng-journal");
            _journal = new BookshelfMigrationJournal(_dataDirectory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }

        [Test]
        public void should_record_a_sha256_fingerprint_without_persisting_the_path_or_contents()
        {
            const string sourceContents = "private bookshelf database fixture";
            var sourcePath = Path.Combine(_directory, "source.db");
            File.WriteAllText(sourcePath, sourceContents);

            _journal.RecordFingerprint("source-database", sourcePath);

            var recorded = ReadFingerprint("source-database");
            var expected = "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceContents))).ToLowerInvariant();
            var journalContents = Directory.GetFiles(_journalDirectory, "*", SearchOption.AllDirectories)
                .Select(File.ReadAllText)
                .ToArray();

            Assert.Multiple(() =>
            {
                Assert.That(recorded, Is.EqualTo(expected));
                Assert.That(string.Join(Environment.NewLine, journalContents), Does.Not.Contain(sourcePath));
                Assert.That(string.Join(Environment.NewLine, journalContents), Does.Not.Contain(sourceContents));
            });
        }

        [Test]
        public void should_record_a_missing_artifact_and_reject_a_directory()
        {
            var missingPath = Path.Combine(_directory, "not-created.db");

            _journal.RecordFingerprint("source-database", missingPath);

            Assert.That(ReadFingerprint("source-database"), Is.EqualTo("missing"));
            Assert.Throws<IOException>(() => _journal.RecordFingerprint("source-database", _directory));
        }

        [Test]
        public void should_fingerprint_the_consistent_source_snapshot_and_converted_target()
        {
            var snapshotPath = Path.Combine(_directory, "source-snapshot.db");
            var targetPath = Path.Combine(_directory, "target-stage.db");
            File.WriteAllText(snapshotPath, "consistent source snapshot");
            File.WriteAllText(targetPath, "converted target database");

            _journal.RecordConverterStage("snapshotted", snapshotPath);
            _journal.RecordConverterStage("converted", targetPath);
            _journal.RecordConverterStage("promoted", targetPath);

            Assert.Multiple(() =>
            {
                Assert.That(ReadFingerprint("source-database"), Is.EqualTo(Fingerprint(snapshotPath)));
                Assert.That(ReadFingerprint("target-database"), Is.EqualTo(Fingerprint(targetPath)));
                Assert.That(ReadOutcome("source-backup"), Is.EqualTo("retained"));
                Assert.That(File.ReadAllText(Path.Combine(_journalDirectory, "state")).Trim(), Is.EqualTo("promoted"));
            });
        }

        [Test]
        public void should_restrict_unix_journal_files_and_directories_to_the_owner()
        {
            if (OperatingSystem.IsWindows())
            {
                Assert.Ignore("Unix journal permissions are validated on Linux builds.");
            }

            var sourcePath = Path.Combine(_directory, "source.db");
            File.WriteAllText(sourcePath, "fixture");
            _journal.RecordFingerprint("source-database", sourcePath);

            var ownerOnlyDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            var ownerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            var fingerprintsDirectory = Path.Combine(_journalDirectory, "fingerprints");

            Assert.Multiple(() =>
            {
                Assert.That(File.GetUnixFileMode(_journalDirectory), Is.EqualTo(ownerOnlyDirectory));
                Assert.That(File.GetUnixFileMode(fingerprintsDirectory), Is.EqualTo(ownerOnlyDirectory));
                Assert.That(File.GetUnixFileMode(Path.Combine(fingerprintsDirectory, "source-database")), Is.EqualTo(ownerOnlyFile));
            });
        }

        private string ReadFingerprint(string name)
        {
            return File.ReadAllText(Path.Combine(_journalDirectory, "fingerprints", name)).Trim();
        }

        private string ReadOutcome(string name)
        {
            return File.ReadAllText(Path.Combine(_journalDirectory, "outcomes", name)).Trim();
        }

        private static string Fingerprint(string path)
        {
            return "sha256:" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        }
    }
}
