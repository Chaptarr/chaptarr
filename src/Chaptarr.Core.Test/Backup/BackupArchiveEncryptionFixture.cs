using System;
using System.IO;
using System.Security.Cryptography;
using NUnit.Framework;
using NzbDrone.Core.Backup;

namespace Chaptarr.Core.Test.Backup
{
    [TestFixture]
    public class BackupArchiveEncryptionFixture
    {
        private string _tempFolder;

        [SetUp]
        public void SetUp()
        {
            _tempFolder = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"backup_encryption_{Guid.NewGuid():N}");
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
        public void should_encrypt_large_archives_and_reject_modified_or_truncated_data()
        {
            var passphrase = "a-long-test-backup-passphrase";
            var original = RandomNumberGenerator.GetBytes((2 * 1024 * 1024) + 137);
            var sourcePath = Path.Combine(_tempFolder, "source.zip");
            var encryptedPath = Path.Combine(_tempFolder, "backup.zip.enc");
            var restoredPath = Path.Combine(_tempFolder, "restored.zip");
            File.WriteAllBytes(sourcePath, original);

            BackupArchiveEncryption.Encrypt(sourcePath, encryptedPath, passphrase);
            BackupArchiveEncryption.Decrypt(encryptedPath, restoredPath, passphrase);

            Assert.That(File.ReadAllBytes(restoredPath), Is.EqualTo(original));

            var encrypted = File.ReadAllBytes(encryptedPath);
            encrypted[^1] ^= 0x01;
            var modifiedPath = Path.Combine(_tempFolder, "modified.zip.enc");
            File.WriteAllBytes(modifiedPath, encrypted);
            Assert.Throws<AuthenticationTagMismatchException>(() =>
                BackupArchiveEncryption.Decrypt(modifiedPath, Path.Combine(_tempFolder, "modified.zip"), passphrase));

            var truncatedPath = Path.Combine(_tempFolder, "truncated.zip.enc");
            File.WriteAllBytes(truncatedPath, File.ReadAllBytes(encryptedPath)[..^1]);
            Assert.Throws<EndOfStreamException>(() =>
                BackupArchiveEncryption.Decrypt(truncatedPath, Path.Combine(_tempFolder, "truncated.zip"), passphrase));
        }
    }
}
