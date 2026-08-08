using System;
using System.Collections.Generic;
using System.Linq;

namespace NzbDrone.Core.Parser
{
    public sealed class AuthorNameMatchExplanation
    {
        public bool IsMatch { get; set; }
        public string Mode { get; set; }
    }

    public static class AuthorNameMatcher
    {
        public static AuthorNameMatchExplanation ExplainAuthorNamesMatch(string expected, string candidate)
        {
            var expectedTokens = NormalizeNameTokens(expected);
            var candidateTokens = NormalizeNameTokens(candidate);

            if (expectedTokens.Count == 0 || candidateTokens.Count == 0)
            {
                return NoMatch();
            }

            if (TokensEqual(expectedTokens, candidateTokens))
            {
                return Match("exact");
            }

            expectedTokens = ExpandGivenInitialCluster(expectedTokens);
            candidateTokens = ExpandGivenInitialCluster(candidateTokens);

            if (expectedTokens.Count < 2 || candidateTokens.Count < 2)
            {
                return NoMatch();
            }

            if (!string.Equals(expectedTokens.Last(), candidateTokens.Last(), StringComparison.OrdinalIgnoreCase))
            {
                return NoMatch();
            }

            var expectedGiven = expectedTokens.Take(expectedTokens.Count - 1).ToList();
            var candidateGiven = candidateTokens.Take(candidateTokens.Count - 1).ToList();

            if (expectedGiven.Count != candidateGiven.Count)
            {
                return NoMatch();
            }

            var usedInitialExpansion = false;
            for (var i = 0; i < expectedGiven.Count; i++)
            {
                if (string.Equals(expectedGiven[i], candidateGiven[i], StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (InitialCompatible(expectedGiven[i], candidateGiven[i]))
                {
                    usedInitialExpansion = true;
                    continue;
                }

                return NoMatch();
            }

            return Match(usedInitialExpansion ? "given_initials" : "first_last");
        }

        public static bool AuthorNamesMatch(string expected, string candidate)
        {
            return ExplainAuthorNamesMatch(expected, candidate).IsMatch;
        }

        private static AuthorNameMatchExplanation Match(string mode)
        {
            return new AuthorNameMatchExplanation { IsMatch = true, Mode = mode };
        }

        private static AuthorNameMatchExplanation NoMatch()
        {
            return new AuthorNameMatchExplanation { IsMatch = false, Mode = "none" };
        }

        private static List<string> NormalizeNameTokens(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return new List<string>();
            }

            if (name.Contains(','))
            {
                var parts = name.Split(',', 2);
                if (parts.Length == 2)
                {
                    name = $"{parts[1]} {parts[0]}";
                }
            }

            return ReleaseTitleMatchScorer.Tokenize(name)
                .Where(token => token.Length > 0)
                .ToList();
        }

        private static List<string> ExpandGivenInitialCluster(List<string> tokens)
        {
            if (tokens.Count < 2)
            {
                return tokens;
            }

            var expanded = new List<string>();
            for (var i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (i < tokens.Count - 1 && token.Length == 2 && token.All(char.IsLetter))
                {
                    expanded.Add(token.Substring(0, 1));
                    expanded.Add(token.Substring(1, 1));
                    continue;
                }

                expanded.Add(token);
            }

            return expanded;
        }

        private static bool TokensEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            for (var i = 0; i < left.Count; i++)
            {
                if (!string.Equals(left[i], right[i], StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool InitialCompatible(string left, string right)
        {
            return (left.Length == 1 && right.StartsWith(left, StringComparison.OrdinalIgnoreCase)) ||
                   (right.Length == 1 && left.StartsWith(right, StringComparison.OrdinalIgnoreCase));
        }
    }
}
