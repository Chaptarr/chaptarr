using System.Collections.Generic;
using Chaptarr.Http;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Configuration.SettingsBackups;

namespace Chaptarr.Api.V1.System.SettingsBackup
{
    [V1ApiController("system/settingsbackup")]
    public class SettingsBackupController : Controller
    {
        private readonly ISettingsBackupService _settingsBackupService;

        public SettingsBackupController(ISettingsBackupService settingsBackupService)
        {
            _settingsBackupService = settingsBackupService;
        }

        [HttpGet("locations")]
        public List<SettingsBackupLocation> GetLocations()
        {
            return _settingsBackupService.GetLocations();
        }

        [HttpGet("files")]
        public List<SettingsBackupFileInfo> GetFiles([FromQuery] string rootFolder)
        {
            return _settingsBackupService.GetFiles(rootFolder);
        }

        [HttpPost("create")]
        public ActionResult<SettingsBackupResult> Create([FromBody] SettingsBackupCreateRequest request)
        {
            return Ok(_settingsBackupService.CreateBackup(request));
        }

        [HttpPost("restore")]
        public ActionResult<SettingsBackupRestoreResult> Restore([FromBody] SettingsBackupRestoreRequest request)
        {
            return Ok(_settingsBackupService.RestoreBackup(request));
        }
    }
}

