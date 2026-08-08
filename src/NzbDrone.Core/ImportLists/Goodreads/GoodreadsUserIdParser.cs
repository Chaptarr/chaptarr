using System.Text.RegularExpressions;

namespace NzbDrone.Core.ImportLists.Goodreads
{
    public static class GoodreadsUserIdParser
    {
        // Goodreads user IDs are numeric and appear in profile URLs like:
        // https://www.goodreads.com/user/show/12345678-example-user
        private static readonly Regex UserIdRegex = new(@"(?<!\d)(\d{3,})(?!\d)", RegexOptions.Compiled);

        public static bool TryParse(string input, out string userId)
        {
            userId = null;

            if (string.IsNullOrWhiteSpace(input))
            {
                return false;
            }

            var trimmed = input.Trim();

            if (IsAllDigits(trimmed))
            {
                userId = trimmed;
                return true;
            }

            var match = UserIdRegex.Match(trimmed);
            if (!match.Success)
            {
                return false;
            }

            userId = match.Groups[1].Value;
            return IsAllDigits(userId);
        }

        public static bool IsValidUserId(string input)
        {
            return TryParse(input, out _);
        }

        private static bool IsAllDigits(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            foreach (var c in value)
            {
                if (c < '0' || c > '9')
                {
                    return false;
                }
            }

            return true;
        }
    }
}
