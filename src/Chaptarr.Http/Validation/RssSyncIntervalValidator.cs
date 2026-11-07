using FluentValidation.Validators;

using NzbDrone.Core.Validation;

namespace Chaptarr.Http.Validation
{
    public class RssSyncIntervalValidator : NzbDronePropertyValidator
    {
        protected override string GetDefaultMessageTemplate() => "Must be 0 to disable or between 10 and 120 minutes";

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

            if (value == 0)
            {
                return true;
            }

            return value is >= 10 and <= 120;
        }
    }
}
