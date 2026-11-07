using FluentValidation.Validators;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;

namespace NzbDrone.Core.Validation
{
    public class FolderValidator : NzbDronePropertyValidator
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
