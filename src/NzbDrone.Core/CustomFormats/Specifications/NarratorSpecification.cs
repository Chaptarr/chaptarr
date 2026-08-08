using System.Linq;
using NzbDrone.Core.Books;

namespace NzbDrone.Core.CustomFormats
{
    public class NarratorSpecification : RegexSpecificationBase
    {
        public override int Order => 9;
        public override string ImplementationName => "Narrator (Advanced)";

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
            var names = ReleaseNarratorEvidenceExtractor.Extract(input).Names;

            if (NarratorNameMatcher.LooksLikeLiteralName(Value))
            {
                return names.Any(name => NarratorNameMatcher.IsMatch(name, Value));
            }

            return names.Any(MatchString);
        }
    }
}
