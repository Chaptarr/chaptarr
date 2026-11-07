using FluentValidation;
using FluentValidation.Validators;
using NzbDrone.Common.Extensions;

using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Validation
{
    public static class UrlValidation
    {
        public static IRuleBuilderOptions<T, string> IsValidUrl<T>(this IRuleBuilder<T, string> ruleBuilder)
        {
            return ruleBuilder.SetValidator(new UrlValidator());
        }
    }

    public class UrlValidator : NzbDronePropertyValidator
    {
        protected override string GetDefaultMessageTemplate() => "Invalid Url: '{url}'";

        protected override bool IsValid(NzbDronePropertyValidatorContext context)
        {
            if (context.PropertyValue == null)
            {
                return false;
            }

            context.MessageFormatter.AppendArgument("url", context.PropertyValue.ToString());

            return context.PropertyValue.ToString().IsValidHttpUrl();
        }
    }
}
