using System;
using System.Linq;

namespace NzbDrone.Core.MetadataSource.Hardcover
{
    internal static class HardcoverAuthorIdentity
    {
        public static bool IsLikelyMultiPerson(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            if (name.Contains(";"))
            {
                return true;
            }

            if (name.Contains("&"))
            {
                return true;
            }

            if (name.Count(c => c == ',') >= 2)
            {
                return true;
            }

            if (name.Count(c => c == ',') == 1)
            {
                var parts = name.Split(new[] { ',' }, 2);
                var leftTokens = parts[0].Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                var rightTokens = parts[1].Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (leftTokens.Length >= 2 && rightTokens.Length >= 2)
                {
                    var lastLeft = CleanToken(leftTokens[leftTokens.Length - 1]);
                    var firstRight = CleanToken(rightTokens[0]).ToLowerInvariant();
                    if (!IsRomanNumeral(lastLeft) && !IsTitleWord(firstRight))
                    {
                        return true;
                    }
                }
            }

            var lower = name.ToLowerInvariant();
            foreach (var delimiter in new[] { " and ", " with " })
            {
                var idx = lower.IndexOf(delimiter, StringComparison.Ordinal);
                if (idx > 0 && idx < lower.Length - delimiter.Length)
                {
                    var parts = lower.Split(new[] { delimiter }, 2, StringSplitOptions.None);
                    var leftTokens = parts[0].Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    var rightTokens = parts[1].Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    if (NamePartsLookMultiPerson(leftTokens, rightTokens))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool NamePartsLookMultiPerson(string[] leftTokens, string[] rightTokens)
        {
            if (leftTokens.Length >= 2 && rightTokens.Length >= 2)
            {
                return true;
            }

            if (leftTokens.Length == 1 && rightTokens.Length >= 2)
            {
                var left = CleanToken(leftTokens[0]).ToLowerInvariant();
                var firstRight = CleanToken(rightTokens[0]).ToLowerInvariant();
                var lastRight = CleanToken(rightTokens[rightTokens.Length - 1]);
                return !string.IsNullOrWhiteSpace(left) &&
                       !string.IsNullOrWhiteSpace(firstRight) &&
                       !string.IsNullOrWhiteSpace(lastRight) &&
                       !IsTitleWord(left) &&
                       !IsTitleWord(firstRight) &&
                       !IsTitleWord(lastRight) &&
                       !IsRomanNumeral(lastRight);
            }

            return false;
        }

        private static string CleanToken(string token)
        {
            return (token ?? string.Empty).Trim(" \t\n\r.,;:!?()[]{}\"'".ToCharArray());
        }

        private static bool IsRomanNumeral(string value)
        {
            switch ((value ?? string.Empty).Trim().ToUpperInvariant())
            {
                case "I":
                case "II":
                case "III":
                case "IV":
                case "V":
                case "VI":
                case "VII":
                case "VIII":
                case "IX":
                case "X":
                case "XI":
                case "XII":
                case "XIII":
                case "XIV":
                case "XV":
                case "XVI":
                case "XVII":
                case "XVIII":
                case "XIX":
                case "XX":
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsTitleWord(string value)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "empress":
                case "emperor":
                case "king":
                case "queen":
                case "prince":
                case "princess":
                case "duke":
                case "duchess":
                case "count":
                case "countess":
                case "baron":
                case "baroness":
                case "lord":
                case "lady":
                case "bishop":
                case "pope":
                case "saint":
                case "sir":
                case "dame":
                case "of":
                case "the":
                    return true;
                default:
                    return false;
            }
        }
    }
}
