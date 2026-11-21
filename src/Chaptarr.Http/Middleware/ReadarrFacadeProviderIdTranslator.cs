using System;
using System.Linq;

namespace Chaptarr.Http.Middleware
{
    public static class ReadarrFacadeProviderIdTranslator
    {
        public static bool IsBareNumericProviderId(string providerId)
        {
            return !string.IsNullOrWhiteSpace(providerId) &&
                   providerId.Trim().All(char.IsDigit);
        }

        public static string NormalizeBareProviderId(string providerId, ReadarrFacadeContext facadeContext)
        {
            if (!IsBareNumericProviderId(providerId) || facadeContext == null)
            {
                return providerId;
            }

            return facadeContext.IsGoodreads
                ? "gr:" + providerId.Trim()
                : "hc:" + providerId.Trim();
        }

        public static bool RequiresProviderPrefix(string providerId, ReadarrFacadeContext facadeContext)
        {
            if (facadeContext != null || string.IsNullOrWhiteSpace(providerId))
            {
                return false;
            }

            return !providerId.Trim().Contains(':');
        }

        public static bool IsBareWorkTerm(string term)
        {
            return IsBareNumericTerm(term, "work");
        }

        private static bool IsBareNumericTerm(string term, string kind)
        {
            if (string.IsNullOrWhiteSpace(term) ||
                !term.Trim().StartsWith(kind + ":", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var id = term.Trim().Substring(kind.Length + 1).Trim();
            return id.Length > 0 && id.All(char.IsDigit);
        }

        public static string NormalizeLookupTerm(string term, ReadarrFacadeContext facadeContext)
        {
            if (facadeContext == null)
            {
                return term;
            }

            var provider = facadeContext.IsGoodreads ? "gr" : "hc";
            foreach (var kind in new[] { "work", "edition" })
            {
                if (IsBareNumericTerm(term, kind))
                {
                    var id = term.Trim().Substring(kind.Length + 1).Trim();
                    return provider + (kind == "edition" ? "-ed:" : ":") + id;
                }
            }

            return term;
        }

        public static string ProviderPrefixRequiredMessage(string field)
        {
            return $"{field} must include a provider prefix on native /api/v1 (for example hc:123 or gr:123). Use a /readarr/{{hc|gr}}/{{ebook|audiobook}} facade for Readarr-compatible bare numeric IDs.";
        }
    }
}
