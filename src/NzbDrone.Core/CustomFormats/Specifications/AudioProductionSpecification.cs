using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.CustomFormats
{
    public class AudioProductionSpecification : CustomFormatSpecificationBase
    {
        public override int Order => 5;
        public override string ImplementationName => "Audio Production";

        protected override bool IsApplicable(CustomFormatInput input)
        {
            var mediaType = input?.MediaType;

            if (mediaType == BookMediaType.Ebook)
            {
                return false;
            }

            if (mediaType == BookMediaType.Audiobook)
            {
                return true;
            }

            // Unknown media type: only applicable when the input carries an explicit
            // audio-production signal, so ambiguous releases match neither audio format.
            return AudioProductionDetector.IsDramatizedOrFullCast(
                input?.IsGraphicAudio == true,
                input?.AudioProductionType,
                input?.AudioProductionFields);
        }

        protected override bool IsSatisfiedByWithoutNegate(CustomFormatInput input)
        {
            return AudioProductionDetector.IsDramatizedOrFullCast(
                input?.IsGraphicAudio == true,
                input?.AudioProductionType,
                input?.AudioProductionFields);
        }

        public override NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult();
        }
    }
}
