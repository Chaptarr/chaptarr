using FluentValidation;
using FluentValidation.Validators;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;

using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Validation.Paths
{
    public static class PathValidation
    {
        public static IRuleBuilderOptions<T, string> IsValidPath<T>(this IRuleBuilder<T, string> ruleBuilder)
        {
            return ruleBuilder.SetValidator(new PathValidator());
        }
    }

    public class PathValidator : NzbDronePropertyValidator
    {
        protected override string GetDefaultMessageTemplate() => "Invalid Path: '{path}'";

        protected override bool IsValid(NzbDronePropertyValidatorContext context)
        {
            context.MessageFormatter.AppendArgument("path", context.PropertyValue?.ToString() ?? string.Empty);

            if (context.PropertyValue == null)
            {
                return false;
            }

            return context.PropertyValue.ToString().IsPathValid(PathValidationType.CurrentOs);
        }
    }
}
