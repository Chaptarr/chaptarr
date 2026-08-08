using NzbDrone.Core.Books;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.CustomFormats
{
    public class PreferredNarratorMajoritySpecification : CustomFormatSpecificationBase
    {
        public override int Order => 7;
        public override string ImplementationName => BuiltInCustomFormats.PreferredNarratorMajorityName;

        protected override bool IsApplicable(CustomFormatInput input)
        {
            return input?.MediaType == BookMediaType.Audiobook &&
                   PreferredNarratorMatcher.HasPreferredNarratorTarget(input);
        }

        protected override bool IsSatisfiedByWithoutNegate(CustomFormatInput input)
        {
            return PreferredNarratorMatcher.Evaluate(input).Majority;
        }

        public override NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult();
        }
    }
}
