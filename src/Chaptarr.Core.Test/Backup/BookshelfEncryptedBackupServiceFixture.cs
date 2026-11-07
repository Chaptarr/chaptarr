using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using NzbDrone.Core.Backup;

namespace Chaptarr.Core.Test.Backup
{
    [TestFixture]
    public class BookshelfEncryptedBackupServiceFixture
    {
        private string _tempFolder;
        private readonly BookshelfEncryptedBackupService _service = new();

        [SetUp]
        public void SetUp()
        {
            _tempFolder = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"bookshelf_backup_encryption_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempFolder);
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
        public void should_round_trip_bookshelf_encrypted_backup_envelope()
        {
            const string passphrase = "a-long-test-backup-passphrase";
            var original = RandomNumberGenerator.GetBytes((2 * 1024 * 1024) + 137);
            var sourcePath = Path.Combine(_tempFolder, "source.zip");
            var encryptedPath = Path.Combine(_tempFolder, "backup.enc");
            var restoredPath = Path.Combine(_tempFolder, "restored.zip");
            File.WriteAllBytes(sourcePath, original);

            _service.Encrypt(sourcePath, encryptedPath, passphrase);

            var existingEncryptedPath = Path.Combine(_tempFolder, "existing.enc");
            File.WriteAllText(existingEncryptedPath, "preserve existing file");
            Assert.Throws<IOException>(() => _service.Encrypt(sourcePath, existingEncryptedPath, passphrase));
            Assert.That(File.ReadAllText(existingEncryptedPath), Is.EqualTo("preserve existing file"));

            var existingDecryptedPath = Path.Combine(_tempFolder, "existing.zip");
            File.WriteAllText(existingDecryptedPath, "preserve existing file");
            Assert.Throws<IOException>(() => _service.Decrypt(encryptedPath, existingDecryptedPath, passphrase));
            Assert.That(File.ReadAllText(existingDecryptedPath), Is.EqualTo("preserve existing file"));

            var encrypted = File.ReadAllBytes(encryptedPath);
            Assert.That(Encoding.ASCII.GetString(encrypted, 0, 8), Is.EqualTo("BKSENC01"));
            Assert.That(BookshelfEncryptedBackupService.IsValidPassphrase(passphrase), Is.True);

            _service.Decrypt(encryptedPath, restoredPath, passphrase);

            Assert.That(File.ReadAllBytes(restoredPath), Is.EqualTo(original));
        }

        [Test]
        public void should_decrypt_an_archive_created_by_bookshelfng()
        {
            const string passphrase = "bookshelfng-fixture-passphrase";
            const string sourceEncryptedBackup = "QktTRU5DMDEAEAAAwfeaKtzy1LQwtZejNNxyRRAdUNCtT0mKAAAALItfj+66m7KD45asikd0UHWEHnp6zCGkEw380xVoT9yONpOFJf93iXlkoFxnJYluygQOj6S2CK3eICcbpAAAAAB0Whc5vmS1Nr3oWdE/vi3r";
            const string expectedContents = "portable backup cross-compatibility fixture\n";
            var encryptedPath = Path.Combine(_tempFolder, "bookshelfng.enc");
            var restoredPath = Path.Combine(_tempFolder, "bookshelfng.zip");
            File.WriteAllBytes(encryptedPath, Convert.FromBase64String(sourceEncryptedBackup));

            _service.Decrypt(encryptedPath, restoredPath, passphrase);

            Assert.That(File.ReadAllText(restoredPath), Is.EqualTo(expectedContents));
        }

        [Test]
        public void should_reject_wrong_passphrase_and_modified_or_truncated_records()
        {
            const string passphrase = "a-long-test-backup-passphrase";
            var sourcePath = Path.Combine(_tempFolder, "source.zip");
            var encryptedPath = Path.Combine(_tempFolder, "backup.enc");
            var original = RandomNumberGenerator.GetBytes(1024);
            File.WriteAllBytes(sourcePath, original);
            _service.Encrypt(sourcePath, encryptedPath, passphrase);

            Assert.Throws<InvalidDataException>(() =>
                _service.Decrypt(encryptedPath, Path.Combine(_tempFolder, "wrong.zip"), "incorrect-backup-passphrase"));

            var encrypted = File.ReadAllBytes(encryptedPath);
            encrypted[^1] ^= 0x01;
            var modifiedPath = Path.Combine(_tempFolder, "modified.enc");
            File.WriteAllBytes(modifiedPath, encrypted);
            var modifiedOutput = Path.Combine(_tempFolder, "modified.zip");
            Assert.Throws<InvalidDataException>(() => _service.Decrypt(modifiedPath, modifiedOutput, passphrase));
            Assert.That(File.Exists(modifiedOutput), Is.False);

            var truncatedPath = Path.Combine(_tempFolder, "truncated.enc");
            File.WriteAllBytes(truncatedPath, File.ReadAllBytes(encryptedPath)[..^1]);
            var truncatedOutput = Path.Combine(_tempFolder, "truncated.zip");
            Assert.Throws<InvalidDataException>(() => _service.Decrypt(truncatedPath, truncatedOutput, passphrase));
            Assert.That(File.Exists(truncatedOutput), Is.False);
        }

        [Test]
        public void should_reject_invalid_headers_trailing_bytes_and_oversized_plaintext()
        {
            const string passphrase = "a-long-test-backup-passphrase";
            var sourcePath = Path.Combine(_tempFolder, "source.zip");
            var encryptedPath = Path.Combine(_tempFolder, "backup.enc");
            File.WriteAllBytes(sourcePath, new byte[] { 1, 2, 3 });
            _service.Encrypt(sourcePath, encryptedPath, passphrase);

            var encrypted = File.ReadAllBytes(encryptedPath);
            encrypted[0] ^= 0x01;
            var malformedPath = Path.Combine(_tempFolder, "malformed.enc");
            File.WriteAllBytes(malformedPath, encrypted);
            Assert.Throws<InvalidDataException>(() =>
                _service.Decrypt(malformedPath, Path.Combine(_tempFolder, "malformed.zip"), passphrase));

            var withTrailingBytes = new byte[File.ReadAllBytes(encryptedPath).Length + 1];
            File.ReadAllBytes(encryptedPath).CopyTo(withTrailingBytes, 0);
            var trailingPath = Path.Combine(_tempFolder, "trailing.enc");
            File.WriteAllBytes(trailingPath, withTrailingBytes);
            Assert.Throws<InvalidDataException>(() =>
                _service.Decrypt(trailingPath, Path.Combine(_tempFolder, "trailing.zip"), passphrase));

            var oversizedPath = Path.Combine(_tempFolder, "oversized.zip");
            using (var oversized = new FileStream(oversizedPath, FileMode.CreateNew, FileAccess.Write))
            {
                oversized.SetLength(900_000_001);
            }

            Assert.Throws<InvalidDataException>(() =>
                _service.Encrypt(oversizedPath, Path.Combine(_tempFolder, "oversized.enc"), passphrase));

            Assert.That(BookshelfEncryptedBackupService.IsValidPassphrase("short"), Is.False);
            Assert.That(BookshelfEncryptedBackupService.IsValidPassphrase(new string('x', 4097)), Is.False);
        }
    }
}
