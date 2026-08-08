using System;
using System.Collections.Generic;
using System.Linq;
using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Books;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.CustomFormats
{
    public class NarratorNamesSpecificationValidator : AbstractValidator<NarratorNamesSpecification>
    {
        public NarratorNamesSpecificationValidator()
        {
            RuleFor(c => c.Names)
                .Must(names => names?.Any() == true)
                .WithMessage("At least one narrator name is required");
        }
    }

    public class NarratorNamesSpecification : CustomFormatSpecificationBase
    {
        private static readonly NarratorNamesSpecificationValidator Validator = new NarratorNamesSpecificationValidator();
        private IEnumerable<string> _names = Array.Empty<string>();

        public NarratorNamesSpecification()
        {
            Name = ImplementationName;
        }

        public override int Order => 4;
        public override string ImplementationName => "Narrator Names";

        [FieldDefinition(1,
            Label = "CustomFormatsNarratorNamesLabel",
            HelpText = "CustomFormatsNarratorNamesHelpText",
            Placeholder = "Type narrators here, separated by commas",
            Type = FieldType.Tag)]
        public IEnumerable<string> Names
        {
            get => _names;
            set => _names = (value ?? Array.Empty<string>())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        protected override bool IsApplicable(CustomFormatInput input)
        {
            if (input?.MediaType == BookMediaType.Ebook)
            {
                return false;
            }

            if (input?.MediaType == BookMediaType.Audiobook)
            {
                return true;
            }

            return ReleaseNarratorEvidenceExtractor.Extract(input).Names.Any();
        }

        protected override bool IsSatisfiedByWithoutNegate(CustomFormatInput input)
        {
            var observedNames = ReleaseNarratorEvidenceExtractor.Extract(input).Names;
            return Names.Any(configuredName => observedNames.Any(observedName => NarratorNameMatcher.IsMatch(observedName, configuredName)));
        }

        public override NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
