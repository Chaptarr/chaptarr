using System.Collections.Generic;
using System.Linq;
using FluentValidation;
using FluentValidation.Validators;

namespace Chaptarr.Api.V1.Profiles.Quality
{
    public static class QualityCutoffValidator
    {
        public static IRuleBuilderOptions<T, int> ValidCutoff<T>(this IRuleBuilder<T, int> ruleBuilder)
        {
            return ruleBuilder.SetValidator(new ValidCutoffValidator<T>());
        }
    }

    public class ValidCutoffValidator<T> : PropertyValidator
    {
        protected override string GetDefaultMessageTemplate() => "Cutoff must be an allowed quality or group";

        protected override bool IsValid(PropertyValidatorContext context)
        {
            var cutoff = (int)context.PropertyValue;
            dynamic instance = context.ParentContext.InstanceToValidate;
            var items = instance.Items as IList<QualityProfileQualityItemResource>;

            // Check if cutoff matches a top-level quality or group
            var cutoffItem = items?.SingleOrDefault(i => (i.Quality == null && i.Id == cutoff) || i.Quality?.Id == cutoff);

            if (cutoffItem is { Allowed: true })
            {
                return true;
            }

            // Also check qualities within groups
            if (items != null)
            {
                foreach (var item in items.Where(i => i.Quality == null && i.Items != null))
                {
                    // This is a group, check its sub-items
                    var nestedCutoffItem = item.Items.SingleOrDefault(subItem => subItem.Quality?.Id == cutoff);
                    if (nestedCutoffItem is { Allowed: true } && item.Allowed)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
