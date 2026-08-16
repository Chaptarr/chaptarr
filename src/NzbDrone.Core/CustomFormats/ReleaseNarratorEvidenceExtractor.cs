using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using NzbDrone.Core.Books.Services;

namespace NzbDrone.Core.CustomFormats
{
    internal static class ReleaseNarratorEvidenceExtractor
    {
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
        private static readonly Regex FullCastRegex = new Regex(@"\b(full\s*cast|cast\s*recording|dramati[sz]ed|graphic\s*audio)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout);
        private static readonly Regex AdditionalNarratorCountRegex = new Regex(@"\+\s*(\d+)\s+(?:more|additional|other)\s+(?:narrators?|voices?|performers?)", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout);
        private static readonly Regex TrailingYearRegex = new Regex(@"[\s,]+(?:19|20)\d{2}\s*$", RegexOptions.Compiled, RegexTimeout);
        private static readonly Regex[] ExplicitNarratorPatterns =
        {
            new Regex(@"(?:read by|narrated by|narrator[:\s]+)([^,\[\]\(\)]+?)(?=\s+-\s+|[,\[\]\(\)]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout)
        };

        // RequireNameShape marks patterns whose delimiters are also used for release metadata.
        // Parentheses hold a narrator credit ("(Roy Dotrice)") about as often as they hold a
        // year, format or bitrate ("(2011)", "(M4B-64)", "(Retail MP3)"), so a candidate found
        // there must additionally look like a person's name before it counts as evidence.
        private static readonly (Regex Pattern, bool RequireNameShape)[] UnlabelledNarratorPatterns =
        {
            (new Regex(@"\[([^,\[\]]+)\]$", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout), false),
            (new Regex(@"\(([^,\(\)]+)\)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout), true),
            (new Regex(@"-\s*([^,\-\[\(\)]+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout), false)
        };

        public static ReleaseNarratorEvidence Extract(CustomFormatInput input, Func<string, bool> includeUnlabelledCandidate = null)
        {
            var names = new List<string>();
            var hasUnresolvedNames = false;

            AddExtraction(ExtractNames(null, input?.Narrator), names, ref hasUnresolvedNames);

            foreach (var field in input?.AudioProductionFields?.Where(field => !string.IsNullOrWhiteSpace(field)) ?? Enumerable.Empty<string>())
            {
                foreach (var narratorText in ExtractExplicitNarratorTexts(field))
                {
                    AddExtraction(ExtractNames(null, narratorText), names, ref hasUnresolvedNames);
                }

                foreach (var (candidate, requireNameShape) in ExtractUnlabelledNarratorCandidates(field))
                {
                    // A trailing person-shaped token is only weak narrator evidence. When it is
                    // the known author, it proves authorship rather than narration. Keep explicit
                    // narrator fields and labels eligible so genuine self-narration still matches.
                    if (IsKnownAuthor(candidate, input))
                    {
                        continue;
                    }

                    // Matching a name the book already asks for is authoritative on its own, so
                    // it overrides the name-shape requirement.
                    var matchesRequestedNarrator = includeUnlabelledCandidate?.Invoke(candidate) == true;

                    if (requireNameShape && !matchesRequestedNarrator && !LooksLikePersonName(candidate))
                    {
                        continue;
                    }

                    if (IsPlausibleUnlabelledNarrator(candidate) || matchesRequestedNarrator)
                    {
                        AddExtraction(ExtractNames(null, candidate), names, ref hasUnresolvedNames);
                    }
                }
            }

            return new ReleaseNarratorEvidence
            {
                Names = names.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                HasUnresolvedNames = hasUnresolvedNames
            };
        }

        public static NarratorNameExtraction ExtractNames(IEnumerable<string> narratorNames, string narratorText)
        {
            var names = new List<string>();
            var textNames = new List<string>();
            var explicitFullCast = false;
            var additionalCount = 0;

            if (narratorNames != null)
            {
                foreach (var name in narratorNames.Where(name => !string.IsNullOrWhiteSpace(name)))
                {
                    if (IsFullCastLabel(name))
                    {
                        explicitFullCast = true;
                    }
                    else
                    {
                        names.Add(name.Trim());
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(narratorText))
            {
                explicitFullCast = explicitFullCast || IsFullCastLabel(narratorText);
                additionalCount = ParseAdditionalNarratorCount(narratorText);

                var cleaned = AdditionalNarratorCountRegex.Replace(narratorText, string.Empty);
                var parts = cleaned
                    .Split(new[] { ",", ";", "&", " + ", " and ", " with ", "/", "|" }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(part => part.Trim())
                    .Where(part => !string.IsNullOrWhiteSpace(part));

                foreach (var part in parts)
                {
                    if (IsFullCastLabel(part))
                    {
                        explicitFullCast = true;
                    }
                    else if (part.Length >= 2)
                    {
                        textNames.Add(part);
                    }
                }
            }

            names.AddRange(textNames);
            names = names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var textCountHint = additionalCount > 0 ? textNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() + additionalCount : textNames.Count;

            return new NarratorNameExtraction
            {
                Names = names,
                ExplicitFullCast = explicitFullCast,
                TotalCountHint = Math.Max(names.Count, textCountHint)
            };
        }

        public static string ExtractExplicitNarratorFromFields(IEnumerable<string> fields)
        {
            if (fields == null)
            {
                return null;
            }

            return fields
                .Where(field => !string.IsNullOrWhiteSpace(field))
                .SelectMany(ExtractExplicitNarratorTexts)
                .FirstOrDefault(narrator => !string.IsNullOrWhiteSpace(narrator));
        }

        private static void AddExtraction(NarratorNameExtraction extraction, List<string> names, ref bool hasUnresolvedNames)
        {
            if (extraction == null)
            {
                return;
            }

            names.AddRange(extraction.Names);
            hasUnresolvedNames = hasUnresolvedNames || extraction.TotalCountHint > extraction.Names.Count;
        }

        private static IEnumerable<string> ExtractExplicitNarratorTexts(string field)
        {
            foreach (var pattern in ExplicitNarratorPatterns)
            {
                var match = pattern.Match(field);
                if (match.Success && match.Groups.Count > 1)
                {
                    // Release names routinely append a publication year to the narrator credit
                    // ("... (Narrator Julia Whelan 2019)"); it is not part of the name.
                    var narrator = TrailingYearRegex.Replace(match.Groups[1].Value.Trim(), string.Empty).Trim();
                    if (!string.IsNullOrWhiteSpace(narrator))
                    {
                        yield return narrator;
                    }
                }
            }
        }

        private static IEnumerable<(string Candidate, bool RequireNameShape)> ExtractUnlabelledNarratorCandidates(string field)
        {
            foreach (var (pattern, requireNameShape) in UnlabelledNarratorPatterns)
            {
                var match = pattern.Match(field);
                if (match.Success && match.Groups.Count > 1)
                {
                    var candidate = match.Groups[1].Value.Trim();
                    if (!string.IsNullOrWhiteSpace(candidate))
                    {
                        yield return (candidate, requireNameShape);
                    }
                }
            }
        }

        private static bool LooksLikePersonName(string candidate)
        {
            var tokens = TextNormalizer.NormalizeAndTokenize(candidate);

            return tokens.Count is >= 2 and <= 6 && tokens.All(token => token.All(char.IsLetter));
        }

        private static bool IsPlausibleUnlabelledNarrator(string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate) || IsFullCastLabel(candidate))
            {
                return false;
            }

            var tokens = TextNormalizer.NormalizeAndTokenize(candidate);
            return tokens.Count is >= 2 and <= 6 && candidate.Any(char.IsLetter);
        }

        private static bool IsKnownAuthor(string candidate, CustomFormatInput input)
        {
            if (string.IsNullOrWhiteSpace(candidate) || input == null)
            {
                return false;
            }

            return new[] { input.Author?.Name, input.BookInfo?.AuthorName }
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Any(name => NarratorNameMatcher.IsMatch(candidate, name));
        }

        private static int ParseAdditionalNarratorCount(string narratorText)
        {
            var match = AdditionalNarratorCountRegex.Match(narratorText ?? string.Empty);
            return match.Success && match.Groups.Count > 1 &&
                   int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
                ? count
                : 0;
        }

        private static bool IsFullCastLabel(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            if (FullCastRegex.IsMatch(value))
            {
                return true;
            }

            var collapsed = Regex.Replace(value.Trim().ToLowerInvariant(), @"[^\p{L}\p{N}]+", string.Empty);
            return collapsed.Contains("fullcast");
        }
    }

    internal sealed class ReleaseNarratorEvidence
    {
        public List<string> Names { get; init; } = new List<string>();
        public bool HasUnresolvedNames { get; init; }
    }

    internal sealed class NarratorNameExtraction
    {
        public List<string> Names { get; init; } = new List<string>();
        public bool ExplicitFullCast { get; init; }
        public int TotalCountHint { get; init; }
    }
}
