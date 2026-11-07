using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.CustomFormats
{
    public class SizeSpecificationValidator : AbstractValidator<SizeSpecification>
    {
        public SizeSpecificationValidator()
        {
            RuleFor(c => c.Min).GreaterThanOrEqualTo(0);
            RuleFor(c => c.Max).GreaterThan(c => c.Min);
        }
    }

    public class SizeSpecification : CustomFormatSpecificationBase
    {
        private static readonly SizeSpecificationValidator Validator = new SizeSpecificationValidator();

        public override int Order => 8;
        public override string ImplementationName => "Size";

        [FieldDefinition(1, Label = "provider.field.fa65c5057eb7e", HelpText = "provider.field.fe9b5e70cc374", Unit = "GB", Type = FieldType.Number)]
        public double Min { get; set; }

        [FieldDefinition(1, Label = "MaximumSize", HelpText = "provider.field.f2aa9bf2bc039", Unit = "GB", Type = FieldType.Number)]
        public double Max { get; set; }

        protected override bool IsSatisfiedByWithoutNegate(CustomFormatInput input)
        {
            var size = input.Size;

            return size > Min.Gigabytes() && size <= Max.Gigabytes();
        }

        public override NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
