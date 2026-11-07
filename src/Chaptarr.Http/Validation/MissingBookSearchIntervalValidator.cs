using FluentValidation.Validators;

using NzbDrone.Core.Validation;

namespace Chaptarr.Http.Validation
{
    public class MissingBookSearchIntervalValidator : NzbDronePropertyValidator
    {
        protected override string GetDefaultMessageTemplate() => "Must be 0 to disable or at least 60 minutes";

        protected override bool IsValid(NzbDronePropertyValidatorContext context)
        {
            if (context.PropertyValue == null)
            {
                return true;
            }

            if (context.PropertyValue is not int value)
            {
                return false;
            }

            return value == 0 || value >= 60;
        }
    }
}
