using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentValidation;
using FluentValidation.Validators;
using NzbDrone.Common.Extensions;

namespace NzbDrone.Core.Organizer
{
    public static class FileNameValidation
    {
        internal static readonly Regex OriginalTokenRegex = new Regex(@"(\{original[- ._](?:title|filename)\})",
                                                                            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static IRuleBuilderOptions<T, string> ValidBookFormat<T>(this IRuleBuilder<T, string> ruleBuilder)
        {
            ruleBuilder.SetValidator(new NotEmptyValidator(null));
            ruleBuilder.SetValidator(new IllegalCharactersValidator());
            ruleBuilder.SetValidator(new BalancedNamingBracesValidator());

            return ruleBuilder.SetValidator(new ValidStandardTrackFormatValidator());
        }

        public static IRuleBuilderOptions<T, string> ValidAuthorFolderFormat<T>(this IRuleBuilder<T, string> ruleBuilder)
        {
            ruleBuilder.SetValidator(new NotEmptyValidator(null));
            ruleBuilder.SetValidator(new IllegalCharactersValidator());
            ruleBuilder.SetValidator(new BalancedNamingBracesValidator());

            return ruleBuilder.SetValidator(new RegularExpressionValidator(FileNameBuilder.AuthorNameRegex)).WithMessage("Must contain Author name");
        }

        public static string GetBraceError(string pattern)
        {
            var depth = 0;
            var opening = 0;
            for (var index = 0; index < (pattern?.Length ?? 0); index++)
            {
                if (pattern[index] == '{')
                {
                    if (depth++ == 0) opening = index;
                }
                else if (pattern[index] == '}' && --depth < 0)
                {
                    return $"Unmatched closing brace at position {index + 1}.";
                }
            }

            return depth > 0 ? $"Unmatched opening brace at position {opening + 1}." : null;
        }
    }

    public class BalancedNamingBracesValidator : PropertyValidator
    {
        protected override string GetDefaultMessageTemplate() => "{BraceError}";

        protected override bool IsValid(PropertyValidatorContext context)
        {
            var error = FileNameValidation.GetBraceError(context.PropertyValue as string);
            context.MessageFormatter.AppendArgument("BraceError", error);
            return error == null;
        }
    }

    public class ValidStandardTrackFormatValidator : PropertyValidator
    {
        protected override string GetDefaultMessageTemplate() => "Must contain Book Title AND PartNumber, OR Original Title";

        protected override bool IsValid(PropertyValidatorContext context)
        {
            if (context.PropertyValue is not string value)
            {
                return false;
            }

            return (FileNameBuilder.BookTitleRegex.IsMatch(value) && FileNameBuilder.PartRegex.IsMatch(value)) ||
                   FileNameValidation.OriginalTokenRegex.IsMatch(value);
        }
    }

    public class IllegalCharactersValidator : PropertyValidator
    {
        private readonly char[] _invalidPathChars = Path.GetInvalidPathChars();

        protected override string GetDefaultMessageTemplate() => "Contains illegal characters: {InvalidCharacters}";

        protected override bool IsValid(PropertyValidatorContext context)
        {
            var value = context.PropertyValue as string;

            if (value.IsNullOrWhiteSpace())
            {
                return true;
            }

            var invalidCharacters = _invalidPathChars.Where(i => value!.IndexOf(i) >= 0).ToList();
            if (invalidCharacters.Any())
            {
                context.MessageFormatter.AppendArgument("InvalidCharacters", string.Join("", invalidCharacters));
                return false;
            }

            return true;
        }
    }
}
