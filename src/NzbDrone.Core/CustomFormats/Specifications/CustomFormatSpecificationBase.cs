using NzbDrone.Core.Validation;

namespace NzbDrone.Core.CustomFormats
{
    public abstract class CustomFormatSpecificationBase : ICustomFormatSpecification
    {
        public abstract int Order { get; }
        public abstract string ImplementationName { get; }

        public virtual string InfoLink => "https://discord.gg/nqFGsGUug2";

        public string Name { get; set; }
        public bool Negate { get; set; }
        public bool Required { get; set; }

        public ICustomFormatSpecification Clone()
        {
            return (ICustomFormatSpecification)MemberwiseClone();
        }

        public abstract NzbDroneValidationResult Validate();

        public bool IsSatisfiedBy(CustomFormatInput input)
        {
            // Applicability is checked before negation: a spec that does not apply to the
            // input at all must not match even when negated, otherwise a negated spec
            // matches everything outside its domain (e.g. audio specs matching ebooks).
            if (!IsApplicable(input))
            {
                return false;
            }

            var match = IsSatisfiedByWithoutNegate(input);
            if (Negate)
            {
                match = !match;
            }

            return match;
        }

        protected virtual bool IsApplicable(CustomFormatInput input) => true;

        protected abstract bool IsSatisfiedByWithoutNegate(CustomFormatInput input);
    }
}
