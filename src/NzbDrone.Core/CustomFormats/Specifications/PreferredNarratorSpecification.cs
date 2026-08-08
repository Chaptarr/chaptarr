using NzbDrone.Core.Books;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.CustomFormats
{
    public class PreferredNarratorSpecification : CustomFormatSpecificationBase
    {
        public override int Order => 6;
        public override string ImplementationName => BuiltInCustomFormats.PreferredNarratorName;

        protected override bool IsApplicable(CustomFormatInput input)
        {
            if (input?.MediaType == BookMediaType.Ebook)
            {
                return false;
            }

            return input?.MediaType == BookMediaType.Audiobook &&
                   PreferredNarratorMatcher.HasPreferredNarratorTarget(input);
        }

        protected override bool IsSatisfiedByWithoutNegate(CustomFormatInput input)
        {
            return PreferredNarratorMatcher.IsMatch(input);
        }

        public override NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult();
        }
    }
}
