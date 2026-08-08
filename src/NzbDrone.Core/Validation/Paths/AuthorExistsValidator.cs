using FluentValidation.Validators;
using NzbDrone.Core.Books;
using NzbDrone.Core.MetadataSource;

namespace NzbDrone.Core.Validation.Paths
{
    public class AuthorExistsValidator : PropertyValidator
    {
        private readonly IAuthorService _authorService;

        public AuthorExistsValidator(IAuthorService authorService)
        {
            _authorService = authorService;
        }

        protected override string GetDefaultMessageTemplate() => "This author has already been added";

        protected override bool IsValid(PropertyValidatorContext context)
        {
            if (context.PropertyValue == null)
            {
                return true;
            }

            var raw = context.PropertyValue.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return true;
            }

            // Canonical provider-prefixed ID (hc:/gr:/ol:/gb:/az:) => check only the matching provider.
            if (raw.Contains(":"))
            {
                try
                {
                    var normalized = ProviderIdHelper.Normalize(raw, defaultPrefix: null);
                    var idx = normalized.IndexOf(':');
                    var provider = idx > 0 ? normalized.Substring(0, idx) : null;

                    if (!string.IsNullOrWhiteSpace(provider))
                    {
                        return _authorService.FindByProviderId(provider, normalized) == null;
                    }
                }
                catch
                {
                    // Unknown/invalid provider ID format; let other validators handle it.
                    return true;
                }
            }

            // Bare numeric IDs are dialect-scoped at the API facade boundary. This core validator
            // has no request context, so it must not guess a provider.
            return true;
        }
    }
}
