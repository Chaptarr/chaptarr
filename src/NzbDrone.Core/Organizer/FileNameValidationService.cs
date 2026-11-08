using System.Collections.Generic;
using System.Linq;
using FluentValidation.Results;

namespace NzbDrone.Core.Organizer
{
    public interface IFilenameValidationService
    {
        ValidationFailure ValidateTrackFilename(SampleResult sampleResult);
        List<ValidationFailure> ValidateNamingConfig(NamingConfig namingConfig);
    }

    public class FileNameValidationService : IFilenameValidationService
    {
        private const string ERROR_MESSAGE = "Produces invalid file names";
        private readonly IFilenameSampleService _samples;

        public FileNameValidationService(IFilenameSampleService samples)
        {
            _samples = samples;
        }

        public List<ValidationFailure> ValidateNamingConfig(NamingConfig namingConfig)
        {
            var errors = Configuration.ConfigValueValidation.ValidateNaming(namingConfig);
            if (errors.Count > 0)
            {
                return errors;
            }

            foreach (var mediaType in new[] { "audiobook", "ebook" })
            {
                // Single-file samples omit part tokens; both shapes must produce usable names.
                foreach (var sample in new[] { _samples.GetStandardTrackSample(namingConfig, mediaType), _samples.GetMultiDiscTrackSample(namingConfig, mediaType) })
                {
                    var failure = ValidateTrackFilename(sample);
                    if (failure != null)
                    {
                        failure.PropertyName = mediaType == "ebook" ? nameof(NamingConfig.EbookStandardBookFormat) : nameof(NamingConfig.StandardBookFormat);
                        errors.Add(failure);
                    }
                }
            }

            return errors.DistinctBy(error => error.PropertyName).ToList();
        }

        public ValidationFailure ValidateTrackFilename(SampleResult sampleResult)
        {
            return string.IsNullOrWhiteSpace(sampleResult.FileName)
                ? new ValidationFailure("StandardBookFormat", ERROR_MESSAGE)
                : null;
        }
    }
}
