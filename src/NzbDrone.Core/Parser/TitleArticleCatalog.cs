using System;
using System.Collections.Generic;
using NzbDrone.Core.Books.Calibre;

namespace NzbDrone.Core.Parser
{
    internal static class TitleArticleCatalog
    {
        // Vocabulary is not identity proof: callers must preserve boundaries, remaining words and sibling distinctions.
        private static readonly IReadOnlyDictionary<string, IReadOnlyCollection<string>> ArticlesByLanguage =
            new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["eng"] = Array.AsReadOnly(new[] { "the", "a", "an" })
            };

        public static IReadOnlyCollection<string> GetArticles(string editionLanguage)
        {
            var language = editionLanguage.CanonicalizeLanguage();
            return language != null && ArticlesByLanguage.TryGetValue(language, out var articles)
                ? articles
                : Array.Empty<string>();
        }
    }
}
