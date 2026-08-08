using FluentValidation.Validators;

namespace Chaptarr.Http.Validation
{
    public class RssSyncIntervalValidator : PropertyValidator
    {
        protected override string GetDefaultMessageTemplate() => "Must be 0 to disable or between 10 and 120 minutes";

        protected override bool IsValid(PropertyValidatorContext context)
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
