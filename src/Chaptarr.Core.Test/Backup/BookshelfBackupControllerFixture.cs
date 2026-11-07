using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Chaptarr.Api.V1.System.Backup;
using Chaptarr.Http.REST;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Backup;

namespace Chaptarr.Core.Test.Backup
{
    [TestFixture]
    public class BookshelfBackupControllerFixture
    {
        private const string Passphrase = "a-long-test-backup-passphrase";
        private string _tempFolder;
        private Mock<IBackupService> _backupService;
        private Mock<IDiskProvider> _diskProvider;
        private BookshelfEncryptedBackupService _encryptedBackupService;

        [SetUp]
        public void SetUp()
        {
            _tempFolder = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"bookshelf_backup_controller_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempFolder);

            _backupService = new Mock<IBackupService>();
            _diskProvider = new Mock<IDiskProvider>();
            _encryptedBackupService = new BookshelfEncryptedBackupService();

            _diskProvider.Setup(provider => provider.EnsureFolder(It.IsAny<string>()))
                .Callback<string>(path => Directory.CreateDirectory(path));
            _diskProvider.Setup(provider => provider.SaveStream(It.IsAny<Stream>(), It.IsAny<string>()))
                .Callback<Stream, string>((stream, path) =>
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    using var output = File.Create(path);
                    stream.CopyTo(output);
                });
            _diskProvider.Setup(provider => provider.FileExists(It.IsAny<string>()))
                .Returns<string>(File.Exists);
            _diskProvider.Setup(provider => provider.DeleteFile(It.IsAny<string>()))
                .Callback<string>(path =>
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                });
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
        public async Task should_export_a_bookshelf_compatible_encrypted_backup()
        {
            var backupPath = Path.Combine(_tempFolder, "source.zip");
            var original = RandomNumberGenerator.GetBytes(4096);
            File.WriteAllBytes(backupPath, original);
            _backupService.Setup(service => service.CreateTemporaryBackup()).Returns(backupPath);

            var responseCompletedCallbacks = new List<Func<Task>>();
            var controller = CreateController(responseCompletedCallbacks);
            controller.HttpContext.Request.Headers[BookshelfEncryptedBackupService.PassphraseHeader] = Passphrase;

            var result = (FileStreamResult)controller.CreateEncryptedBackup(Passphrase);
            var encryptedPath = Path.Combine(_tempFolder, "export.enc");
            using (var output = File.Create(encryptedPath))
            {
                await result.FileStream.CopyToAsync(output);
            }
            await result.FileStream.DisposeAsync();

            var restoredPath = Path.Combine(_tempFolder, "restored.zip");
            _encryptedBackupService.Decrypt(encryptedPath, restoredPath, Passphrase);

            Assert.That(File.ReadAllBytes(restoredPath), Is.EqualTo(original));
            Assert.That(result.FileDownloadName, Does.EndWith(".enc"));
            Assert.That(controller.Response.Headers.CacheControl.ToString(), Does.Contain("no-store"));

            foreach (var callback in responseCompletedCallbacks)
            {
                await callback();
            }

            Assert.That(File.Exists(backupPath), Is.False);
            Assert.That(Directory.GetFiles(Path.Combine(_tempFolder, "chaptarr_backup_transfer")), Is.Empty);
        }

        [Test]
        public void should_decrypt_bookshelf_uploads_and_reject_wrong_passphrases()
        {
            var sourcePath = Path.Combine(_tempFolder, "source.zip");
            var encryptedPath = Path.Combine(_tempFolder, "bookshelfng_backup.enc");
            var original = RandomNumberGenerator.GetBytes(4096);
            File.WriteAllBytes(sourcePath, original);
            _encryptedBackupService.Encrypt(sourcePath, encryptedPath, Passphrase);

            byte[] restoredBytes = null;
            string restoredPath = null;
            _backupService.Setup(service => service.Restore(It.IsAny<string>()))
                .Callback<string>(path =>
                {
                    restoredPath = path;
                    restoredBytes = File.ReadAllBytes(path);
                });

            var invalidController = CreateController();
            invalidController.HttpContext.Request.Headers[BookshelfEncryptedBackupService.PassphraseHeader] = "incorrect-backup-passphrase";
            SetUpload(invalidController, encryptedPath, "bookshelfng_backup.enc");

            Assert.Throws<BadRequestException>(() => invalidController.UploadAndRestore("incorrect-backup-passphrase", null));
            _backupService.Verify(service => service.Restore(It.IsAny<string>()), Times.Never);

            var controller = CreateController();
            controller.HttpContext.Request.Headers[BookshelfEncryptedBackupService.PassphraseHeader] = Passphrase;
            SetUpload(controller, encryptedPath, "bookshelfng_backup.enc");

            controller.UploadAndRestore(Passphrase, controller.Request.Form.Files[0]);

            Assert.That(restoredPath, Does.EndWith(".zip"));
            Assert.That(restoredBytes, Is.EqualTo(original));
            Assert.That(File.Exists(restoredPath), Is.False);
            _backupService.Verify(service => service.Restore(It.IsAny<string>()), Times.Once);
        }

        [Test]
        public void should_keep_chaptarr_full_backup_uploads_on_the_existing_restore_path()
        {
            var encryptedPath = Path.Combine(_tempFolder, "chaptarr_backup.zip.enc");
            File.WriteAllBytes(encryptedPath, RandomNumberGenerator.GetBytes(1024));

            var controller = CreateController();
            SetUpload(controller, encryptedPath, "chaptarr_backup.zip.enc");

            controller.UploadAndRestore(null, null);

            _backupService.Verify(service => service.Restore(It.Is<string>(path => path.EndsWith(".zip.enc", StringComparison.OrdinalIgnoreCase))), Times.Once);
        }

        [TestCase(".db")]
        [TestCase(".xml")]
        public void should_accept_legacy_raw_backup_upload_extensions(string extension)
        {
            var uploadPath = Path.Combine(_tempFolder, $"bookshelf_backup{extension}");
            var bytes = RandomNumberGenerator.GetBytes(1024);
            File.WriteAllBytes(uploadPath, bytes);

            string restoredPath = null;
            _backupService.Setup(service => service.Restore(It.IsAny<string>()))
                .Callback<string>(path =>
                {
                    restoredPath = path;
                    Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes));
                });

            var controller = CreateController();
            SetUpload(controller, uploadPath, Path.GetFileName(uploadPath));

            controller.UploadAndRestore(null, null);

            Assert.Multiple(() =>
            {
                Assert.That(restoredPath, Does.EndWith(extension));
                Assert.That(File.Exists(restoredPath), Is.False);
            });
            _backupService.Verify(service => service.Restore(It.IsAny<string>()), Times.Once);
        }

        [Test]
        public void should_reject_unsupported_restore_upload_extensions()
        {
            var uploadPath = Path.Combine(_tempFolder, "bookshelf_backup.7z");
            File.WriteAllBytes(uploadPath, RandomNumberGenerator.GetBytes(1024));

            var controller = CreateController();
            SetUpload(controller, uploadPath, Path.GetFileName(uploadPath));

            Assert.Throws<UnsupportedMediaTypeException>(() => controller.UploadAndRestore(null, null));
            _backupService.Verify(service => service.Restore(It.IsAny<string>()), Times.Never);
        }

        private BackupController CreateController(List<Func<Task>> responseCompletedCallbacks = null)
        {
            var appFolderInfo = new Mock<IAppFolderInfo>();
            appFolderInfo.Setup(info => info.TempFolder).Returns(_tempFolder);

            var controller = new BackupController(_backupService.Object, _encryptedBackupService, appFolderInfo.Object, _diskProvider.Object);
            var httpContext = new DefaultHttpContext();

            if (responseCompletedCallbacks != null)
            {
                var responseFeature = new Mock<IHttpResponseFeature>();
                responseFeature.SetupProperty(feature => feature.Headers, new HeaderDictionary());
                responseFeature.Setup(feature => feature.HasStarted).Returns(false);
                responseFeature.Setup(feature => feature.OnCompleted(It.IsAny<Func<object, Task>>(), It.IsAny<object>()))
                    .Callback<Func<object, Task>, object>((callback, state) => responseCompletedCallbacks.Add(() => callback(state)));
                httpContext.Features.Set(responseFeature.Object);
            }

            controller.ControllerContext = new ControllerContext
            {
                HttpContext = httpContext
            };

            return controller;
        }

        private static void SetUpload(BackupController controller, string path, string filename)
        {
            var stream = new MemoryStream(File.ReadAllBytes(path));
            var file = new FormFile(stream, 0, stream.Length, "restore", filename);
            var files = new FormFileCollection
            {
                file
            };

            controller.Request.Form = new FormCollection(new Dictionary<string, StringValues>(), files);
        }
    }
}
