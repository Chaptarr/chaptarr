using System.Text.RegularExpressions;

namespace NzbDrone.Core.Profiles.Releases.TermMatchers
{
    public class RegexTermMatcher : ITermMatcher
    {
        private readonly Regex _regex;

        public RegexTermMatcher(Regex regex)
        {
            _regex = regex;
        }

        public bool IsMatch(string value)
        {
            try
            {
                return _regex.IsMatch(value);
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        public string MatchingTerm(string value)
        {
            try
            {
                return _regex.Match(value).Value;
            }
            catch (RegexMatchTimeoutException)
            {
                return string.Empty;
            }
        }
    }
}
