using System.Collections.Generic;
using System.IO;
using System.Linq;
using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using NzbDrone.Core.Lifecycle;
using Chaptarr.Http;
using Chaptarr.Http.REST;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Common.Crypto;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Backup;
using NzbDrone.Http.REST.Attributes;

namespace Chaptarr.Api.V1.System.Backup
{
    [V1ApiController("system/backup")]
    public class BackupController : Controller
    {
        private readonly IBackupService _backupService;
        private readonly IAppFolderInfo _appFolderInfo;
        private readonly IDiskProvider _diskProvider;
        private readonly ILifecycleService _lifecycleService;

        private static readonly HashSet<string> ValidExtensions = new(StringComparer.OrdinalIgnoreCase) { ".zip" };

        public BackupController(IBackupService backupService,
                            IAppFolderInfo appFolderInfo,
                            IDiskProvider diskProvider,
                            ILifecycleService lifecycleService)
        {
            _backupService = backupService;
            _appFolderInfo = appFolderInfo;
            _diskProvider = diskProvider;
            _lifecycleService = lifecycleService;
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
                Time = b.Time,
                IncludedComponents = b.IncludedComponents,
                OmittedComponents = b.OmittedComponents
            })
                .OrderByDescending(b => b.Time)
                .ToList();
        }

        [HttpGet("status")]
        public BackupStatus GetStatus() => _backupService.GetStatus();

        [HttpDelete("restore")]
        public object CancelRestore()
        {
            _backupService.CancelRestore();
            return new { };
        }

        [HttpDelete("restore/failed")]
        public object DismissFailedRestores()
        {
            _backupService.DismissFailedRestores();
            return new { };
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
            _lifecycleService.Restart();

            return new
            {
                RestartRequired = true
            };
        }

        [HttpPost("restore/upload")]
        [RequestSizeLimit(BackupService.RestoreUploadLimitBytes)]
        [RequestFormLimits(MultipartBodyLengthLimit = BackupService.RestoreUploadLimitBytes)]
        [StreamBackupUpload]
        public async Task<object> UploadAndRestore()
        {
            const string sizeMessage = "Backup uploads are limited to 5 GB. Copy this ZIP into the manual subfolder of your configured Backups folder, keep its original filename, and restore it from the list.";
            if (Request.ContentLength > BackupService.RestoreUploadLimitBytes)
            {
                throw new RestoreBackupFailedException(global::System.Net.HttpStatusCode.RequestEntityTooLarge, sizeMessage);
            }

            if (!MediaTypeHeaderValue.TryParse(Request.ContentType, out var contentType) ||
                !contentType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase) ||
                !contentType.Boundary.HasValue)
            {
                throw new BadRequestException("A multipart backup file must be provided.");
            }

            var folder = BackupService.CreateWorkingFolder(_appFolderInfo);
            var path = Path.Combine(folder, "backup.zip");
            var staged = false;
            try
            {
                var available = _diskProvider.GetAvailableSpace(folder)
                    ?? throw new RestoreBackupFailedException(global::System.Net.HttpStatusCode.BadRequest, "Unable to determine free disk space for the backup upload.");
                if (Request.ContentLength > available)
                {
                    throw new RestoreBackupFailedException(global::System.Net.HttpStatusCode.BadRequest, $"Insufficient space for backup upload: need {Request.ContentLength} bytes, have {available} bytes.");
                }
                var reader = new MultipartReader(HeaderUtilities.RemoveQuotes(contentType.Boundary).Value, Request.Body)
                {
                    BodyLengthLimit = BackupService.RestoreUploadLimitBytes
                };
                var uploaded = false;
                MultipartSection section;
                while ((section = await reader.ReadNextSectionAsync(Request.HttpContext.RequestAborted)) != null)
                {
                    if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) ||
                        (!disposition.FileName.HasValue && !disposition.FileNameStar.HasValue))
                    {
                        continue;
                    }
                    if (uploaded)
                    {
                        throw new BadRequestException("Provide one backup file.");
                    }
                    var fileName = HeaderUtilities.RemoveQuotes(disposition.FileNameStar.HasValue ? disposition.FileNameStar : disposition.FileName).Value;
                    if (!ValidExtensions.Contains(Path.GetExtension(fileName)))
                    {
                        throw new UnsupportedMediaTypeException("Invalid extension, must be .zip");
                    }
                    await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                    {
                        var buffer = new byte[81920];
                        long received = 0;
                        int read;
                        while ((read = await section.Body.ReadAsync(buffer, Request.HttpContext.RequestAborted)) > 0)
                        {
                            received += read;
                            if (received > available)
                            {
                                throw new RestoreBackupFailedException(global::System.Net.HttpStatusCode.BadRequest, $"Insufficient space for backup upload: need at least {received} bytes, have {available} bytes.");
                            }
                            await output.WriteAsync(buffer.AsMemory(0, read), Request.HttpContext.RequestAborted);
                        }
                    }
                    uploaded = true;
                }
                if (!uploaded)
                {
                    throw new BadRequestException("file must be provided");
                }

                // Once staged, the restore must finish independently of the requesting browser.
                _backupService.Restore(path);
                staged = true;
            }
            catch (BadHttpRequestException ex) when (ex.StatusCode == 413)
            {
                throw new RestoreBackupFailedException(global::System.Net.HttpStatusCode.RequestEntityTooLarge, sizeMessage);
            }
            catch (InvalidDataException ex)
            {
                throw new BadRequestException($"Invalid backup upload: {ex.Message}");
            }
            finally
            {
                try
                {
                    _diskProvider.DeleteFolder(folder, true);
                }
                finally
                {
                    if (staged)
                    {
                        _lifecycleService.Restart();
                    }
                }
            }

            return new { RestartRequired = true };
        }

        // Avoid MVC's disk buffering: the upload belongs in this instance's private AppData directory.
        private sealed class StreamBackupUploadAttribute : Attribute, IResourceFilter
        {
            public void OnResourceExecuting(ResourceExecutingContext context)
            {
                context.ValueProviderFactories.RemoveType<FormValueProviderFactory>();
                context.ValueProviderFactories.RemoveType<FormFileValueProviderFactory>();
                context.ValueProviderFactories.RemoveType<JQueryFormValueProviderFactory>();
            }

            public void OnResourceExecuted(ResourceExecutedContext context)
            {
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
