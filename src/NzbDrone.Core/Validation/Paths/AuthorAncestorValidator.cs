using System.Linq;
using FluentValidation.Validators;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;

using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Validation.Paths
{
    public class AuthorAncestorValidator : NzbDronePropertyValidator
    {
        private readonly IAuthorService _authorService;

        public AuthorAncestorValidator(IAuthorService authorService)
        {
            _authorService = authorService;
        }

        protected override string GetDefaultMessageTemplate() => "Path '{path}' is an ancestor of an existing author";

        protected override bool IsValid(NzbDronePropertyValidatorContext context)
        {
            if (context.PropertyValue == null)
            {
                return true;
            }

            context.MessageFormatter.AppendArgument("path", context.PropertyValue.ToString());

            return !_authorService.AllAuthorPaths().Any(s => context.PropertyValue.ToString().IsParentPath(s.Value));
        }
    }
}
