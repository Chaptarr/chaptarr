using System;
using NzbDrone.Core.Profiles.Releases;

namespace Chaptarr.Core.Test.Books
{
    internal sealed class TestTermMatcherService : ITermMatcherService
    {
        public bool IsMatch(string term, string value)
        {
            return MatchingTerm(term, value) != null;
        }

        public string MatchingTerm(string term, string value)
        {
            if (string.IsNullOrWhiteSpace(term) || string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return value.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0 ? term : null;
        }
    }
}
