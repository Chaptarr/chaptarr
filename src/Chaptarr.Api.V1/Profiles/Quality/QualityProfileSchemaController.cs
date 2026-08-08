using Chaptarr.Http;
using Chaptarr.Http.REST;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Profiles.Qualities;
using CoreQuality = NzbDrone.Core.Qualities.Quality;

namespace Chaptarr.Api.V1.Profiles.Quality
{
    [V1ApiController("qualityprofile/schema")]
    public class QualityProfileSchemaController : Controller
    {
        private readonly IQualityProfileService _qualityProfileService;

        public QualityProfileSchemaController(IQualityProfileService qualityProfileService)
        {
            _qualityProfileService = qualityProfileService;
        }

        [HttpGet]
        public QualityProfileResource GetSchema([FromQuery] string mediaType = null)
        {
            var requestedMediaType = QualityProfileMediaTypeParser.ParseOrNull(mediaType);
            var qualityProfile = requestedMediaType switch
            {
                QualityProfileMediaType.Audiobook => GetDefaultAudiobookProfile(),
                QualityProfileMediaType.Ebook => GetDefaultEbookProfile(),
                null => GetLegacyDefaultProfile(),
                _ => throw new BadRequestException("mediaType must be either 'audiobook' or 'ebook'")
            };

            return qualityProfile.ToResource(requestedMediaType.HasValue);
        }

        private QualityProfile GetLegacyDefaultProfile()
        {
            var qualityProfile = _qualityProfileService.GetDefaultProfile(
                string.Empty,
                CoreQuality.Unknown,
                CoreQuality.Unknown);

            qualityProfile.ProfileType = ProfileType.Audiobook;
            QualityProfileService.ApplyNewProfileCustomFormatDefaults(qualityProfile);

            return qualityProfile;
        }

        private QualityProfile GetDefaultAudiobookProfile()
        {
            var qualityProfile = _qualityProfileService.GetDefaultProfile(
                "New Audiobook Profile",
                CoreQuality.M4B,
                CoreQuality.UnknownAudio,
                CoreQuality.FLAC,
                CoreQuality.MP3,
                CoreQuality.M4B);

            qualityProfile.ProfileType = ProfileType.Audiobook;
            QualityProfileService.ApplyNewProfileCustomFormatDefaults(qualityProfile);

            return qualityProfile;
        }

        private QualityProfile GetDefaultEbookProfile()
        {
            var qualityProfile = _qualityProfileService.GetDefaultProfile(
                "New E-Book Profile",
                CoreQuality.MOBI,
                CoreQuality.MOBI,
                CoreQuality.EPUB,
                CoreQuality.AZW3);

            qualityProfile.ProfileType = ProfileType.Ebook;
            QualityProfileService.ApplyNewProfileCustomFormatDefaults(qualityProfile);

            return qualityProfile;
        }
    }
}
