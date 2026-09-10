using FluentValidation.Results;

namespace NzbDrone.Core.Organizer
{
    public interface IFilenameValidationService
    {
        ValidationFailure ValidateTrackFilename(SampleResult sampleResult);
    }

    public class FileNameValidationService : IFilenameValidationService
    {
        private const string ERROR_MESSAGE = "Produces invalid file names";

        public ValidationFailure ValidateTrackFilename(SampleResult sampleResult)
        {
            return string.IsNullOrWhiteSpace(sampleResult.FileName)
                ? new ValidationFailure("StandardBookFormat", ERROR_MESSAGE)
                : null;
        }
    }
}
