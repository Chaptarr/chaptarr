using NzbDrone.Core.Books;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.CustomFormats
{
    public class PreferredNarratorCompleteSpecification : CustomFormatSpecificationBase
    {
        public override int Order => 8;
        public override string ImplementationName => BuiltInCustomFormats.CompletePreferredCastName;

        protected override bool IsApplicable(CustomFormatInput input)
        {
            return input?.MediaType == BookMediaType.Audiobook &&
                   PreferredNarratorMatcher.HasPreferredNarratorTarget(input);
        }

        protected override bool IsSatisfiedByWithoutNegate(CustomFormatInput input)
        {
            return PreferredNarratorMatcher.Evaluate(input).Complete;
        }

        public override NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult();
        }
    }
}
