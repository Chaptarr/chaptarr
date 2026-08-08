using System.Text.RegularExpressions;
using FluentValidation;
using FluentValidation.Validators;

namespace Chaptarr.Http.Validation
{
    public static class RuleBuilderExtensions
    {
        public static IRuleBuilderOptions<T, int> ValidId<T>(this IRuleBuilder<T, int> ruleBuilder)
        {
            return ruleBuilder.SetValidator(new GreaterThanValidator(0));
        }

        public static IRuleBuilderOptions<T, int> IsZero<T>(this IRuleBuilder<T, int> ruleBuilder)
        {
            return ruleBuilder.SetValidator(new EqualValidator(0));
        }

        public static IRuleBuilderOptions<T, string> HaveHttpProtocol<T>(this IRuleBuilder<T, string> ruleBuilder)
        {
            return ruleBuilder.SetValidator(new RegularExpressionValidator("^https?://", RegexOptions.IgnoreCase)).WithMessage("must start with http:// or https://");
        }

        public static IRuleBuilderOptions<T, string> NotBlank<T>(this IRuleBuilder<T, string> ruleBuilder)
        {
            return ruleBuilder.NotEmpty();
        }

        public static IRuleBuilderOptions<T, int> IsValidRssSyncInterval<T>(this IRuleBuilder<T, int> ruleBuilder)
        {
            return ruleBuilder.SetValidator(new RssSyncIntervalValidator());
        }
    }
}
