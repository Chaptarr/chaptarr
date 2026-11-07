using System.Collections.Generic;
using System.IO;
using System.Linq;
using System;
using System.Threading.Tasks;
using Chaptarr.Http;
using Chaptarr.Http.REST;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Common.Crypto;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Backup;
using NzbDrone.Http.REST.Attributes;
using Swashbuckle.AspNetCore.Annotations;

namespace Chaptarr.Api.V1.System.Backup
{
    [V1ApiController("system/backup")]
    public class BackupController : Controller
    {
        // Large enough for realistic backups (DB + config), small enough to limit resource exhaustion if the endpoint is reachable.
        private const long RestoreUploadLimitBytes = 1L * 1024 * 1024 * 1024; // 1 GiB

        private readonly IBackupService _backupService;
        private readonly IBookshelfEncryptedBackupService _bookshelfEncryptedBackupService;
        private readonly IAppFolderInfo _appFolderInfo;
        private readonly IDiskProvider _diskProvider;

        private static readonly HashSet<string> ValidExtensions = new(StringComparer.OrdinalIgnoreCase) { ".zip", ".db", ".xml", ".enc" };

        public BackupController(IBackupService backupService,
                            IBookshelfEncryptedBackupService bookshelfEncryptedBackupService,
                            IAppFolderInfo appFolderInfo,
                            IDiskProvider diskProvider)
        {
            _backupService = backupService;
            _bookshelfEncryptedBackupService = bookshelfEncryptedBackupService;
            _appFolderInfo = appFolderInfo;
            _diskProvider = diskProvider;
        }

        [HttpGet]
        public List<BackupResource> GetBackupFiles()
        {
            var backups = _backupService.GetBackups();

            return backups.Select(b => new BackupResource
            {
                Id = GetBackupId(b),
                Name = b.Name,
                Path = $"/backup/{b.Type.ToString().ToLower()}/{b.Name}",
                Type = b.Type,
                Size = b.Size,
                Time = b.Time
            })
                .OrderByDescending(b => b.Time)
                .ToList();
        }

        [RestDeleteById]
        public object DeleteBackup(int id)
        {
            var backup = GetBackup(id);

            if (backup == null)
            {
                throw new NotFoundException();
            }

            var path = GetBackupPath(backup);

            if (!_diskProvider.FileExists(path))
            {
                throw new NotFoundException();
            }

            _diskProvider.DeleteFile(path);

            return new { };
        }

        [HttpPost("restore/{id:int}")]
        public object Restore(int id)
        {
            var backup = GetBackup(id);

            if (backup == null)
            {
                throw new NotFoundException();
            }

            var path = GetBackupPath(backup);

            _backupService.Restore(path);

            return new
            {
                RestartRequired = true
            };
        }

        [HttpPost("encrypted")]
        [Produces("application/octet-stream")]
        [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK)]
        [SwaggerResponse(StatusCodes.Status400BadRequest, "Invalid passphrase")]
        public IActionResult CreateEncryptedBackup(
            [FromHeader(Name = BookshelfEncryptedBackupService.PassphraseHeader)]
            [SwaggerParameter("Passphrase used to encrypt the portable backup. It must contain at least 16 characters.")]
            string passphrase)
        {
            passphrase = GetPassphrase(passphrase);
            var encryptedPath = Path.Combine(GetPrivateTempFolder(), $"chaptarr_backup_{Guid.NewGuid():N}.enc");
            string backupPath = null;

            try
            {
                backupPath = _backupService.CreateTemporaryBackup();
                _bookshelfEncryptedBackupService.Encrypt(backupPath, encryptedPath, passphrase);

                var stream = global::System.IO.File.OpenRead(encryptedPath);
                Response.Headers.CacheControl = "no-store, no-cache";
                Response.Headers.Pragma = "no-cache";
                Response.OnCompleted(() =>
                {
                    DeleteTemporaryFile(backupPath);
                    DeleteTemporaryFile(encryptedPath);
                    return Task.CompletedTask;
                });

                return File(stream, "application/octet-stream", $"chaptarrng_backup_{DateTime.UtcNow:yyyyMMdd_HHmmss}.enc");
            }
            catch
            {
                DeleteTemporaryFile(backupPath);
                DeleteTemporaryFile(encryptedPath);
                throw;
            }
        }

        [HttpPost("restore/upload")]
        [Consumes("multipart/form-data")]
        [RequestSizeLimit(RestoreUploadLimitBytes)]
        [RequestFormLimits(MultipartBodyLengthLimit = RestoreUploadLimitBytes)]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public object UploadAndRestore(
            [FromHeader(Name = BookshelfEncryptedBackupService.PassphraseHeader)]
            [SwaggerParameter("Required when the uploaded restore file has the .enc extension.")]
            string passphrase,
            IFormFile restore)
        {
            var file = restore ?? Request.Form.Files.FirstOrDefault();

            if (file == null)
            {
                throw new BadRequestException("file must be provided");
            }

            var filename = file.FileName ?? string.Empty;
            var extension = Path.GetExtension(filename);
            var isEncryptedFullBackup = filename.EndsWith(".zip.enc", StringComparison.OrdinalIgnoreCase);
            var isBookshelfEncryptedBackup = extension.Equals(".enc", StringComparison.OrdinalIgnoreCase) && !isEncryptedFullBackup;

            if (!ValidExtensions.Contains(extension) && !isEncryptedFullBackup)
            {
                throw new UnsupportedMediaTypeException($"Invalid extension, must be .zip, .db, .xml, .enc or .zip.enc");
            }

            var restoreExtension = isEncryptedFullBackup ? ".zip.enc" : extension;
            var restoreFolder = GetPrivateTempFolder();
            var path = Path.Combine(restoreFolder, $"restore_upload_{Guid.NewGuid():N}{restoreExtension}");
            var decryptedPath = Path.Combine(restoreFolder, $"restore_decrypted_{Guid.NewGuid():N}.zip");
            passphrase = isBookshelfEncryptedBackup ? GetPassphrase(passphrase) : null;

            try
            {
                _diskProvider.SaveStream(file.OpenReadStream(), path);

                if (isBookshelfEncryptedBackup)
                {
                    try
                    {
                        _bookshelfEncryptedBackupService.Decrypt(path, decryptedPath, passphrase);
                    }
                    catch (InvalidDataException exception)
                    {
                        throw new BadRequestException(exception.Message);
                    }

                    _backupService.Restore(decryptedPath);
                }
                else
                {
                    _backupService.Restore(path);
                }
            }
            finally
            {
                // Cleanup restored file (it may have been moved by restore)
                DeleteTemporaryFile(path);
                DeleteTemporaryFile(decryptedPath);
            }

            return new
            {
                RestartRequired = true
            };
        }

        private static string GetPassphrase(string passphrase)
        {
            if (!BookshelfEncryptedBackupService.IsValidPassphrase(passphrase))
            {
                throw new BadRequestException($"A passphrase with at least {BookshelfEncryptedBackupService.MinimumPassphraseLength} characters and no more than {BookshelfEncryptedBackupService.MaximumPassphraseBytes} UTF-8 bytes is required.");
            }

            return passphrase;
        }

        private string GetPrivateTempFolder()
        {
            var path = Path.Combine(_appFolderInfo.TempFolder, "chaptarr_backup_transfer");

            if (OperatingSystem.IsWindows())
            {
                _diskProvider.EnsureFolder(path);
            }
            else
            {
                var ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
                Directory.CreateDirectory(path, ownerOnly);
                global::System.IO.File.SetUnixFileMode(path, ownerOnly);
            }

            return path;
        }

        private void DeleteTemporaryFile(string path)
        {
            if (!string.IsNullOrEmpty(path) && _diskProvider.FileExists(path))
            {
                _diskProvider.DeleteFile(path);
            }
        }

        private string GetBackupPath(NzbDrone.Core.Backup.Backup backup)
        {
            return Path.Combine(_backupService.GetBackupFolder(backup.Type), backup.Name);
        }

        private int GetBackupId(NzbDrone.Core.Backup.Backup backup)
        {
            return HashConverter.GetHashInt31($"backup-{backup.Type}-{backup.Name}");
        }

        private NzbDrone.Core.Backup.Backup GetBackup(int id)
        {
            return _backupService.GetBackups().SingleOrDefault(b => GetBackupId(b) == id);
        }
    }
}
