using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Localization;
using NzbDrone.Core.MediaFiles;

namespace NzbDrone.Core.HealthCheck.Checks
{
    public class FfmpegCheck : HealthCheckBase
    {
        private readonly IExternalToolsService _externalToolsService;

        public FfmpegCheck(IExternalToolsService externalToolsService, ILocalizationService localizationService)
            : base(localizationService)
        {
            _externalToolsService = externalToolsService;
        }

        public override HealthCheck Check()
        {
            var missingTools = new List<string>();

            if (!_externalToolsService.IsFFmpegAvailable())
            {
                missingTools.Add("FFmpeg");
            }

            if (!_externalToolsService.IsFFprobeAvailable())
            {
                missingTools.Add("FFprobe");
            }

            if (missingTools.Any())
            {
                var message = string.Format(
                    _localizationService.GetLocalizedString("ExternalToolsHealthCheckMessage"),
                    string.Join(", ", missingTools));

                return new HealthCheck(
                    GetType(),
                    HealthCheckResult.Warning,
                    message,
                    "#ffmpeg-or-ffprobe-is-not-available");
            }

            return new HealthCheck(GetType());
        }
    }
}
