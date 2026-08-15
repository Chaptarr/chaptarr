using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NzbDrone.Core.Parser;
using NLog;

namespace NzbDrone.Core.MediaFiles.BookImport.Services
{
    public sealed class EditionTitleEvidence
    {
        public EditionTitleEvidence(
            string fieldName,
            string fieldValue,
            string matchedTitle,
            bool isNearExact = false,
            bool requiresAudiobookDurationCorroboration = false)
        {
            FieldName = fieldName;
            FieldValue = fieldValue;
            MatchedTitle = matchedTitle;
            IsNearExact = isNearExact;
            RequiresAudiobookDurationCorroboration = requiresAudiobookDurationCorroboration;
        }

        public string FieldName { get; }
        public string FieldValue { get; }
        public string MatchedTitle { get; }
        public bool IsNearExact { get; }
        public bool RequiresAudiobookDurationCorroboration { get; }
    }

	    public interface IContainmentValidator
	    {
        /// <summary>
        /// Validates that a needle string is contained within a haystack string
        /// Used to ensure V5 results are actually present in the original tags
        /// </summary>
        bool Contains(string haystack, string needle);

        /// <summary>
        /// Validates that an author name returned from V5 is contained in the tags
        /// Checks each field individually, not a concatenated blob
        /// </summary>
        bool ValidateAuthorInTags(string authorName, IDictionary<string, List<string>> allTags);

        /// <summary>
        /// Validates that an edition title returned from V5 is contained in the tags
        /// Checks each field individually, not a concatenated blob
        /// </summary>
        bool ValidateEditionInTags(string editionTitle, IDictionary<string, List<string>> allTags);

        /// <summary>
        /// Returns the tag field(s) that caused an edition title to be considered contained.
        /// Used for higher-level validation (e.g., leftover-token checks) against the same evidence field.
	        /// </summary>
	        IReadOnlyList<EditionTitleEvidence> GetEditionTitleEvidence(string editionTitle, IDictionary<string, List<string>> allTags, bool includeDurationGatedNearExact = false);
	    }

	    public class ContainmentValidator : IContainmentValidator
	    {
	        private readonly ITagNormalizer _tagNormalizer;
	        private readonly Logger _logger;

	        // Edition titles are never plot summaries. If a tag value is longer than this, treat it as
	        // ineligible for edition-title evidence (defense-in-depth against non-standard comment fields).
	        private const int MaxTitleEvidenceValueLength = 400;
	        
	        // Unicode-aware word boundary pattern
	        private static readonly Regex WordPattern = new Regex(@"\p{L}+", RegexOptions.Compiled);
        // Numeric tokens (e.g., 1984)
        private static readonly Regex NumberPattern = new Regex(@"\b\d+\b", RegexOptions.Compiled);
        // Roman numerals (conservative)
        private static readonly Regex RomanRegex = new Regex(@"^(M{0,4}(CM|CD|D?C{0,3})(XC|XL|L?X{0,3})(IX|IV|V?I{0,3}))$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        
        // Common surname particles that should be preserved as part of the last name
        private static readonly HashSet<string> SurnameParticles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "de", "del", "della", "di", "da", "von", "van", "der", "den", "ten", "ter",
            "la", "le", "les", "du", "des", "dos", "das", "do", "zu", "zur", "zum",
            "mac", "mc", "o", "al", "el", "ibn", "bin", "bint", "abu", "um"
        };
        
        // Common short words that should NOT be treated as initial clusters
        private static readonly HashSet<string> CommonShortWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Articles and prepositions
            "a", "an", "as", "at", "by", "do", "go", "he", "i", "if", "in", "is", "it", 
            "me", "my", "no", "of", "on", "or", "so", "to", "up", "us", "we",
            // Common abbreviations that aren't initials
            "jr", "sr", "dr", "mr", "ms", "st", "co", "vs", "etc", "inc", "ltd"
        };

	        // Tokens that commonly appear in trailing parenthetical metadata but are not expected to exist verbatim in tags.
	        // Example: "(Narrated by Stephen Fry)" should match if "Stephen Fry" is present in ANY tag field, even if
	        // the literal words "narrated"/"by" are not.
	        private static readonly HashSet<string> TrailingParentheticalNoiseTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	        {
	            "narrated", "narrator", "narrators", "by", "read", "performed", "performer", "with"
	        };
	        
	        // Parenthetical phrases that are common "metadata noise" and are allowed to be absent from embedded tags.
	        // Example: "(Unabridged)" or "(A Novel)".
	        private static readonly HashSet<string> TrailingParentheticalGenericNoiseTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	        {
	            "a", "an", "the",
	            "novel", "audiobook", "audio",
	            "unabridged", "abridged",
	            "complete", "special", "deluxe", "expanded", "illustrated", "anniversary",
	            "collector", "collectors", "collection",
	            "edition"
	        };
	        
	        // Structural tokens that often appear in series/volume parentheticals, and should not be required
	        // unless the group is otherwise evidenced in tags.
	        private static readonly HashSet<string> TrailingParentheticalStructuralTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	        {
	            "book", "books",
	            "vol", "volume", "volumes",
	            "part", "parts", "pt",
	            "season", "seasons",
	            "episode", "episodes"
	        };

	        private static bool IsExcludedFromEditionTitleEvidenceKey(string key)
	        {
	            // Single source of truth — same exclusion as FTS and V5 query.
	            // All non-trash tags are treated uniformly. No label assumptions.
	            return FileMatchingService.IsExcludedFromMatching(key);
	        }

        // Structural words that may appear in metadata server titles but are sometimes absent from embedded tags.
        // We only treat these as optional when the title also contains a numeric token (e.g., "Season 3").
        private static readonly HashSet<string> EditionStructuralTokensOptionalWhenNumberPresent = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "season", "seasons"
        };

        public ContainmentValidator(ITagNormalizer tagNormalizer, Logger logger)
        {
            _tagNormalizer = tagNormalizer;
            _logger = logger;
        }

        public bool Contains(string haystack, string needle)
        {
            if (string.IsNullOrWhiteSpace(haystack) || string.IsNullOrWhiteSpace(needle))
                return false;

            // Both strings are already normalized by the caller
            var isContained = haystack.Contains(needle, StringComparison.Ordinal);
            
            if (!isContained)
            {
                _logger.Trace("[CONTAINMENT] '{0}' NOT found in '{1}'", needle, haystack);
            }

            return isContained;
        }

        public bool ValidateAuthorInTags(string authorName, IDictionary<string, List<string>> allTags)
        {
            if (string.IsNullOrWhiteSpace(authorName))
            {
                _logger.Warn("[CONTAINMENT] Cannot validate empty author name");
                return false;
            }

            if (allTags == null || !allTags.Any())
            {
                _logger.Warn("[CONTAINMENT] Cannot validate against empty tags");
                return false;
            }

            _logger.Trace("[CONTAINMENT] === Validating author '{0}' ===", authorName);
            
            // Parse author name into words
            var authorWords = ParseIntoWords(authorName);
            if (_logger.IsTraceEnabled)
            {
                _logger.Trace("[CONTAINMENT] Author parsed into words: [{0}]",
                    string.Join(", ", authorWords.Select(w => $"'{w.Word}'({w.Type})")));
            }

            // Check each match-eligible field individually. Callers do not all receive the
            // same pre-filtered dictionary, so the shared exclusion policy must be enforced
            // at this boundary rather than relying on every route to remember it.
            foreach (var tagField in allTags)
            {
                if (TagExclusionPolicy.IsExcludedFromMatching(tagField.Key))
                {
                    _logger.Trace("[CONTAINMENT] Skipping excluded author-evidence field '{0}'", tagField.Key);
                    continue;
                }

                foreach (var fieldValue in tagField.Value)
                {
                    if (string.IsNullOrWhiteSpace(fieldValue))
                        continue;

                    if (CheckWordContainment(authorWords, fieldValue, tagField.Key))
                    {
                            _logger.Trace("[CONTAINMENT] Author '{0}' MATCHED in field '{1}' = '{2}'",
                                authorName, tagField.Key,
                                fieldValue.Length > 100 ? fieldValue.Substring(0, 100) + "..." : fieldValue);
                        return true;
                    }
                }
            }

            _logger.Trace("[CONTAINMENT] Author '{0}' NOT FOUND in any single field", authorName);

            if (_logger.IsTraceEnabled)
            {
                _logger.Trace("[CONTAINMENT] Searched {0} fields with {1} total values",
                    allTags.Count, allTags.Sum(kv => kv.Value.Count));
            }
            
            return false;
        }

        public bool ValidateEditionInTags(string editionTitle, IDictionary<string, List<string>> allTags)
        {
            if (string.IsNullOrWhiteSpace(editionTitle))
            {
                _logger.Warn("[CONTAINMENT] Cannot validate empty edition title");
                return false;
            }

            if (allTags == null || !allTags.Any())
            {
                _logger.Warn("[CONTAINMENT] Cannot validate against empty tags");
                return false;
            }

            _logger.Trace("[CONTAINMENT] === Validating edition '{0}' ===", editionTitle);

            // Tokenize the FULL edition title as a LIST (preserving duplicates)
            // This is critical for consumption-based matching:
            // "The Housemaid / The Housemaid's Secret" has "the" twice, "housemaid" twice
            // A file with just "The Housemaid's Secret" cannot satisfy this demand.
            var editionTokens = GetEditionTokensForValidation(editionTitle);

            if (_logger.IsTraceEnabled)
            {
                _logger.Trace("[CONTAINMENT] Edition tokens ({0}): [{1}]",
                    editionTokens.Count,
                    string.Join(", ", editionTokens.Take(15)) + (editionTokens.Count > 15 ? "..." : ""));
            }

            // Check each tag field using CONSUMPTION-based matching:
            // Each edition token must find AND consume a matching field token.
            // If the edition has "the" twice but the field only has "the" once, it fails.
            // IMPORTANT: Skip comment/description fields - they contain book summaries that
            // mention character names, causing false positives (e.g., "Paul of Dune" matching
            // because Dune's description mentions "Paul Atreides").
            foreach (var tagField in allTags)
            {
                // Skip comment/description, genre/language, and identity fields for edition title validation.
                if (IsExcludedFromEditionTitleEvidenceKey(tagField.Key))
                {
	                    continue;
	                }

	                var fieldValues = (tagField.Value ?? new List<string>())
	                    .Where(v => !string.IsNullOrWhiteSpace(v) && v.Length <= MaxTitleEvidenceValueLength)
	                    .ToList();

                foreach (var fieldValue in fieldValues)
                {
                    var normField = NormalizeForWordExtraction(fieldValue).ToLowerInvariant();
                    var fieldTokens = ExtractTokensAsList(normField);

                    if (fieldTokens.Count == 0) continue;

                    // Try to consume all edition tokens from this field
                    if (CanConsumeAllTokens(editionTokens, fieldTokens))
                    {
                        _logger.Trace("[CONTAINMENT] Edition '{0}' MATCHED in field '{1}' = '{2}'",
                            TrimTitleForLog(editionTitle),
                            tagField.Key,
                            fieldValue.Length > 60 ? fieldValue.Substring(0, 60) + "..." : fieldValue);
                        return true;
                    }
                }

	                // Multi-value field support: treat multiple values for the SAME field key as one logical field.
	                // This helps for MP4 freeform/vendored tags where title tokens and numeric positions can be split
	                // across multiple values within the same atom/box.
	                if (fieldValues.Count > 1)
	                {
                    var mergedTokens = new List<string>();
	                    foreach (var v in fieldValues)
	                    {
	                        var normField = NormalizeForWordExtraction(v).ToLowerInvariant();
	                        mergedTokens.AddRange(ExtractTokensAsList(normField));
	                    }

                    if (mergedTokens.Count > 0 && CanConsumeAllTokens(editionTokens, mergedTokens))
                    {
                        _logger.Trace("[CONTAINMENT] Edition '{0}' MATCHED in multi-value field '{1}'",
                            TrimTitleForLog(editionTitle),
                            tagField.Key);
                        return true;
                    }
                }
            }

            if (TryGetNearExactTitleEvidence(editionTitle, allTags, includeDurationGatedNearExact: false).Count > 0)
            {
                _logger.Trace("[CONTAINMENT] Edition '{0}' MATCHED via ordered near-exact title evidence",
                    TrimTitleForLog(editionTitle));
                return true;
            }

            // Special-case: tolerate trailing parentheticals when the base title matches in a single field AND
            // the parenthetical's meaningful tokens appear anywhere in the tags.
            // This prevents "good evidence" (e.g., narrator in another field) from blocking an otherwise-correct match.
            if (TryValidateTrailingParentheticals(editionTitle, allTags))
            {
                _logger.Trace("[CONTAINMENT] Edition '{0}' MATCHED via trailing-parenthetical evidence",
                    TrimTitleForLog(editionTitle));
                return true;
            }

            if (_logger.IsTraceEnabled)
            {
                _logger.Trace("[CONTAINMENT] Edition '{0}' NOT FOUND in any single field",
                    TrimTitleForLog(editionTitle));
            }
            return false;
        }

        public IReadOnlyList<EditionTitleEvidence> GetEditionTitleEvidence(string editionTitle, IDictionary<string, List<string>> allTags, bool includeDurationGatedNearExact = false)
        {
            var evidence = new List<EditionTitleEvidence>();

            if (string.IsNullOrWhiteSpace(editionTitle) || allTags == null || !allTags.Any())
            {
                return evidence;
            }

            // 1) Try strict "full title in one field" (same strictness as ValidateEditionInTags).
            var editionTokens = GetEditionTokensForValidation(editionTitle);

	            if (editionTokens.Count > 0)
	            {
	                foreach (var tagField in allTags)
	                {
	                    if (IsExcludedFromEditionTitleEvidenceKey(tagField.Key))
	                    {
	                        continue;
	                    }

	                    var fieldValues = (tagField.Value ?? new List<string>())
	                        .Where(v => !string.IsNullOrWhiteSpace(v) && v.Length <= MaxTitleEvidenceValueLength)
	                        .ToList();

                    foreach (var fieldValue in fieldValues)
                    {
                        var normField = NormalizeForWordExtraction(fieldValue).ToLowerInvariant();
                        var fieldTokens = ExtractTokensAsList(normField);
                        if (fieldTokens.Count == 0) continue;

                        if (CanConsumeAllTokens(editionTokens, fieldTokens))
                        {
                            evidence.Add(new EditionTitleEvidence(tagField.Key, fieldValue, editionTitle));
                        }
                    }

                    // Multi-value evidence: allow consuming title tokens from multiple values of the SAME field key.
                    // Return a minimal "evidence" string built from only the selected values to avoid polluting
                    // leftover-token validation with unrelated metadata from the same key.
                    if (fieldValues.Count > 1)
                    {
                        if (TryBuildMultiValueEvidence(editionTokens, fieldValues, out var selectedValues))
                        {
                            evidence.Add(new EditionTitleEvidence(tagField.Key, string.Join(" ", selectedValues), editionTitle));
                        }
                    }
                }
            }

            if (evidence.Count > 0)
            {
                return evidence;
            }

            evidence.AddRange(TryGetNearExactTitleEvidence(editionTitle, allTags, includeDurationGatedNearExact));
            if (evidence.Count > 0)
            {
                return evidence;
            }

            // 2) Trailing-parenthetical tolerant match: base title must match in one field and the
            // meaningful parenthetical tokens must exist somewhere in the tags.
            if (!TryValidateTrailingParentheticals(editionTitle, allTags))
            {
                return evidence;
            }

            // Recompute the base title so the caller can remove the same evidence tokens from the matching field.
            var remaining = editionTitle.Trim();
            while (remaining.EndsWith(")", StringComparison.Ordinal))
            {
                var open = remaining.LastIndexOf('(');
                if (open <= 0)
                {
                    break;
                }

                var inside = remaining.Substring(open + 1, remaining.Length - open - 2).Trim();
                if (string.IsNullOrWhiteSpace(inside))
                {
                    break;
                }

                remaining = remaining.Substring(0, open).TrimEnd();
            }

            if (string.IsNullOrWhiteSpace(remaining))
            {
                return evidence;
            }

            var normBase = NormalizeForWordExtraction(remaining).ToLowerInvariant();
            var baseTokens = ApplyOptionalStructuralTokenRules(ExtractTokensAsList(normBase));
            if (baseTokens.Count == 0)
            {
                return evidence;
            }

	            foreach (var tagField in allTags)
	            {
	                if (IsExcludedFromEditionTitleEvidenceKey(tagField.Key))
	                {
	                    continue;
	                }

	                foreach (var fieldValue in tagField.Value)
	                {
	                    if (string.IsNullOrWhiteSpace(fieldValue) || fieldValue.Length > MaxTitleEvidenceValueLength) continue;

                    var normField = NormalizeForWordExtraction(fieldValue).ToLowerInvariant();
                    var fieldTokens = ExtractTokensAsList(normField);
                    if (fieldTokens.Count == 0) continue;

                    if (CanConsumeAllTokens(baseTokens, fieldTokens))
                    {
                        evidence.Add(new EditionTitleEvidence(tagField.Key, fieldValue, remaining));
                    }
                }
            }

            return evidence;
        }

        private IReadOnlyList<EditionTitleEvidence> TryGetNearExactTitleEvidence(string editionTitle, IDictionary<string, List<string>> allTags, bool includeDurationGatedNearExact)
        {
            var evidence = new List<EditionTitleEvidence>();
            var editionTokens = GetEditionTokensForValidation(editionTitle);

            if (editionTokens.Count == 0 || allTags == null || allTags.Count == 0)
            {
                return evidence;
            }

            foreach (var tagField in allTags)
            {
                if (IsExcludedFromEditionTitleEvidenceKey(tagField.Key))
                {
                    continue;
                }

                var fieldValues = (tagField.Value ?? new List<string>())
                    .Where(v => !string.IsNullOrWhiteSpace(v) && v.Length <= MaxTitleEvidenceValueLength)
                    .ToList();

                foreach (var fieldValue in fieldValues)
                {
                    var normField = NormalizeForWordExtraction(fieldValue).ToLowerInvariant();
                    var fieldTokens = ExtractTokensAsList(normField);
                    if (fieldTokens.Count == 0)
                    {
                        continue;
                    }

                    if (TitleTokenAlignment.TryAlignStructural(editionTokens, fieldTokens, allowNearExact: true, allowTransposition: includeDurationGatedNearExact, out var alignment) &&
                        alignment.UsedNearExact)
                    {
                        evidence.Add(new EditionTitleEvidence(
                            tagField.Key,
                            fieldValue,
                            editionTitle,
                            isNearExact: true,
                            requiresAudiobookDurationCorroboration: alignment.UsedTransposition));
                    }
                }
            }

            return evidence;
        }

	        private List<string> GetEditionTokensForValidation(string editionTitle)
	        {
	            var normEdition = NormalizeForWordExtraction(editionTitle).ToLowerInvariant();
	            return ApplyOptionalStructuralTokenRules(ExtractTokensAsList(normEdition));
	        }

        private List<string> ApplyOptionalStructuralTokenRules(List<string> tokens)
        {
            if (tokens == null || tokens.Count == 0)
            {
                return tokens ?? new List<string>();
            }

            // Only ignore structural tokens when a numeric token is also present (e.g., "Season 3").
            // This prevents ignoring legitimate content words like "Season of Storms".
            var hasNumber = tokens.Any(t => t.All(char.IsDigit));
            if (!hasNumber)
            {
                return tokens;
            }

            var filtered = new List<string>(tokens.Count);
            foreach (var t in tokens)
            {
                if (EditionStructuralTokensOptionalWhenNumberPresent.Contains(t))
                {
                    continue;
                }
                filtered.Add(t);
            }

            return filtered;
        }

        private bool TryBuildMultiValueEvidence(List<string> editionTokens, List<string> fieldValues, out List<string> selectedValues)
        {
            selectedValues = new List<string>();
            if (editionTokens == null || editionTokens.Count == 0) return false;
            if (fieldValues == null || fieldValues.Count < 2) return false;

            var remaining = new List<string>(editionTokens);
            var tokenized = new List<List<string>>(fieldValues.Count);
            foreach (var v in fieldValues)
            {
                var norm = NormalizeForWordExtraction(v).ToLowerInvariant();
                tokenized.Add(ExtractTokensAsList(norm));
            }

            var used = new bool[fieldValues.Count];
            while (remaining.Count > 0)
            {
                var bestIdx = -1;
                var bestCount = 0;

                for (var i = 0; i < tokenized.Count; i++)
                {
                    if (used[i]) continue;
                    var count = CountConsumableTokens(remaining, tokenized[i]);
                    if (count > bestCount)
                    {
                        bestCount = count;
                        bestIdx = i;
                    }
                }

                if (bestIdx < 0 || bestCount == 0)
                {
                    break;
                }

                used[bestIdx] = true;
                selectedValues.Add(fieldValues[bestIdx]);
                ConsumeTokensFromRemaining(remaining, tokenized[bestIdx]);
            }

            if (remaining.Count != 0)
            {
                return false;
            }

            // If a tag field stores multiple values, treat pure numeric siblings as part of the same logical
            // field for downstream validation (e.g., ["Impact Winter", "3"]). These numbers often encode
            // series/season/work identity and must be visible to the leftover-token gate.
            for (var i = 0; i < fieldValues.Count; i++)
            {
                if (used[i]) continue;

                var v = fieldValues[i]?.Trim();
                if (string.IsNullOrWhiteSpace(v)) continue;

                if (v.All(char.IsDigit))
                {
                    selectedValues.Add(v);
                }
            }

            return true;
        }

        private static int CountConsumableTokens(List<string> remaining, List<string> availableTokens)
        {
            if (remaining == null || remaining.Count == 0) return 0;
            if (availableTokens == null || availableTokens.Count == 0) return 0;

            var available = new List<string>(availableTokens);
            var count = 0;
            foreach (var t in remaining)
            {
                var idx = available.IndexOf(t);
                if (idx >= 0)
                {
                    count++;
                    available.RemoveAt(idx);
                }
            }

            return count;
        }

        private static void ConsumeTokensFromRemaining(List<string> remaining, List<string> availableTokens)
        {
            if (remaining == null || remaining.Count == 0) return;
            if (availableTokens == null || availableTokens.Count == 0) return;

            foreach (var t in availableTokens)
            {
                var idx = remaining.IndexOf(t);
                if (idx >= 0)
                {
                    remaining.RemoveAt(idx);
                    if (remaining.Count == 0) return;
                }
            }
        }

	        private bool TryValidateTrailingParentheticals(string editionTitle, IDictionary<string, List<string>> allTags)
	        {
	            var remaining = (editionTitle ?? string.Empty).Trim();
	            if (!remaining.EndsWith(")", StringComparison.Ordinal))
	            {
	                return false;
	            }

            // Peel trailing "(...)" groups from the end.
            var trailingGroups = new List<string>();
            while (remaining.EndsWith(")", StringComparison.Ordinal))
            {
                var open = remaining.LastIndexOf('(');
                if (open <= 0)
                {
                    break;
                }

                var inside = remaining.Substring(open + 1, remaining.Length - open - 2).Trim();
                if (string.IsNullOrWhiteSpace(inside))
                {
                    break;
                }

                trailingGroups.Add(inside);
                remaining = remaining.Substring(0, open).TrimEnd();
            }

            if (trailingGroups.Count == 0 || string.IsNullOrWhiteSpace(remaining))
            {
                return false;
            }

	            // 1) Base title must match in ONE field (same strictness as normal smoke test).
	            var normBase = NormalizeForWordExtraction(remaining).ToLowerInvariant();
	            var baseTokens = ApplyOptionalStructuralTokenRules(ExtractTokensAsList(normBase));
	            if (baseTokens.Count == 0)
	            {
	                return false;
	            }

	            var baseMatched = false;
	            foreach (var tagField in allTags)
	            {
	                if (IsExcludedFromEditionTitleEvidenceKey(tagField.Key))
	                {
	                    continue;
	                }

	                foreach (var fieldValue in tagField.Value)
	                {
	                    if (string.IsNullOrWhiteSpace(fieldValue) || fieldValue.Length > MaxTitleEvidenceValueLength) continue;

	                    var normField = NormalizeForWordExtraction(fieldValue).ToLowerInvariant();
	                    var fieldTokens = ExtractTokensAsList(normField);
	                    if (fieldTokens.Count == 0) continue;

	                    if (CanConsumeAllTokens(baseTokens, fieldTokens))
	                    {
	                        baseMatched = true;
	                        break;
	                    }
	                }

	                if (baseMatched) break;
	            }

	            if (!baseMatched)
	            {
	                return false;
	            }

	            // 2) Parenthetical groups must be either ignorable metadata noise OR evidenced elsewhere in tags.
	            // This prevents matching titles like "Never Come Back (Nora McTavish, Book 2)" when tags contain only
	            // "Never Come Back" and provide no series/narrator evidence.
	            var evidenceTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
	            foreach (var kv in allTags)
	            {
	                if (string.IsNullOrWhiteSpace(kv.Key))
	                {
	                    continue;
	                }

	                // Skip excluded keys — single source of truth.
	                if (FileMatchingService.IsExcludedFromMatching(kv.Key))
	                {
	                    continue;
	                }

	                var values = kv.Value?
	                    .Where(v => !string.IsNullOrWhiteSpace(v) && v.Length <= MaxTitleEvidenceValueLength)
	                    .ToList();

	                if (values == null || values.Count == 0)
	                {
	                    continue;
	                }

	                evidenceTags[kv.Key] = values;
	            }

	            var evidenceTokenSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
	            foreach (var kv in evidenceTags)
	            {
	                foreach (var v in kv.Value)
	                {
	                    var norm = NormalizeForWordExtraction(v).ToLowerInvariant();
	                    foreach (var tok in ExtractTokensAsList(norm))
	                    {
	                        evidenceTokenSet.Add(tok);
	                    }
	                }
	            }

	            bool GroupIsIgnorable(List<string> groupTokens)
	            {
	                if (groupTokens == null || groupTokens.Count == 0)
	                {
	                    return true;
	                }

	                foreach (var t in groupTokens)
	                {
	                    if (!TrailingParentheticalGenericNoiseTokens.Contains(t) &&
	                        !TrailingParentheticalNoiseTokens.Contains(t))
	                    {
	                        return false;
	                    }
	                }

	                return true;
	            }

	            bool GroupTokensContainedInAnySingleField(List<string> groupTokens)
	            {
	                if (groupTokens == null || groupTokens.Count == 0)
	                {
	                    return true;
	                }

	                foreach (var kv in evidenceTags)
	                {
	                    foreach (var v in kv.Value)
	                    {
	                        var norm = NormalizeForWordExtraction(v).ToLowerInvariant();
	                        var tokens = ExtractTokensAsList(norm);
	                        if (tokens.Count == 0) continue;

	                        if (CanConsumeAllTokens(groupTokens, tokens))
	                        {
	                            return true;
	                        }
	                    }
	                }

	                return false;
	            }

	            foreach (var group in trailingGroups)
	            {
	                var normGroup = NormalizeForWordExtraction(group).ToLowerInvariant();
	                var groupTokens = ExtractTokensAsList(normGroup);
	                if (groupTokens.Count == 0)
	                {
	                    continue;
	                }

	                if (GroupIsIgnorable(groupTokens))
	                {
	                    continue;
	                }

	                var isNarratorGroup = groupTokens.Any(t => TrailingParentheticalNoiseTokens.Contains(t));
	                if (isNarratorGroup)
	                {
	                    var narratorTokens = groupTokens
	                        .Where(t => !TrailingParentheticalNoiseTokens.Contains(t) &&
	                                    !TrailingParentheticalGenericNoiseTokens.Contains(t))
	                        .ToList();

	                    if (narratorTokens.Count == 0)
	                    {
	                        continue;
	                    }

	                    var narratorName = string.Join(" ", narratorTokens);
	                    if (!ValidateAuthorInTags(narratorName, evidenceTags))
	                    {
	                        return false;
	                    }

	                    continue;
	                }

	                // Non-narrator parenthetical: require meaningful tokens to exist in tags somewhere.
	                // Structural tokens like "Book"/"Volume" are treated as optional.
	                var requiredTokens = groupTokens
	                    .Where(t => !TrailingParentheticalGenericNoiseTokens.Contains(t) &&
	                                !TrailingParentheticalStructuralTokens.Contains(t))
	                    .ToList();

	                if (requiredTokens.Count == 0)
	                {
	                    continue;
	                }

	                // If the only required tokens are numeric, demand the original group (including structural words)
	                // to be contained within a single tag field, otherwise it is too easy to "prove" using TRACKNUMBER.
	                var requiredAllNumeric = requiredTokens.All(t => t.All(char.IsDigit));
	                if (requiredAllNumeric)
	                {
	                    var strictGroupTokens = groupTokens
	                        .Where(t => !TrailingParentheticalGenericNoiseTokens.Contains(t))
	                        .ToList();

	                    if (!GroupTokensContainedInAnySingleField(strictGroupTokens))
	                    {
	                        return false;
	                    }

	                    continue;
	                }

	                foreach (var t in requiredTokens)
	                {
	                    if (!evidenceTokenSet.Contains(t))
	                    {
	                        return false;
	                    }
	                }
	            }

	            return true;
	        }

        private static string TrimTitleForLog(string title)
        {
            return title.Length > 60 ? title.Substring(0, 60) + "..." : title;
        }

        /// <summary>
        /// Extract tokens as a LIST preserving duplicates for consumption-based matching.
        /// "The Housemaid / The Housemaid's Secret" → [the, housemaid, the, housemaids, secret]
        /// </summary>
        private static List<string> ExtractTokensAsList(string normalized)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(normalized)) return list;

            foreach (Match m in WordPattern.Matches(normalized))
            {
                var v = m.Value.ToLowerInvariant();
                if (!string.IsNullOrEmpty(v)) list.Add(v);
            }
            foreach (Match m in NumberPattern.Matches(normalized))
            {
                var v = m.Value.ToLowerInvariant();
                if (!string.IsNullOrEmpty(v)) list.Add(v);
            }
            return list;
        }

        /// <summary>
        /// Match edition identity in order. Internal glue may be absent, but identity tokens,
        /// duplicates, and numeric disambiguators must all survive the alignment.
        /// </summary>
        private static bool CanConsumeAllTokens(List<string> editionTokens, List<string> fieldTokens)
        {
            return TitleTokenAlignment.TryAlignStructural(
                editionTokens,
                fieldTokens,
                allowNearExact: false,
                allowTransposition: false,
                out _);
        }

        private List<AuthorWord> ParseIntoWords(string authorName)
        {
            var normalized = NormalizeForWordExtraction(authorName);
            var words = WordPattern.Matches(normalized);
            var result = new List<AuthorWord>();

            for (int i = 0; i < words.Count; i++)
            {
                var word = words[i].Value.ToLowerInvariant();
                var type = word.Length == 1 ? WordType.Initial : WordType.Full;
                
                // Check if this is a surname particle
                if (SurnameParticles.Contains(word))
                {
                    type = WordType.Particle;
                }
                
                result.Add(new AuthorWord 
                { 
                    Word = word, 
                    Type = type,
                    Position = i
                });
            }

            // Create initial clusters for consecutive initials (not including particles)
            for (int i = 0; i < result.Count - 1; i++)
            {
                if (result[i].Type == WordType.Initial && result[i + 1].Type == WordType.Initial)
                {
                    var cluster = result[i].Word + result[i + 1].Word;
                    result.Add(new AuthorWord 
                    { 
                        Word = cluster, 
                        Type = WordType.InitialCluster,
                        Position = i
                    });
                }
            }

            return result;
        }

        private bool CheckWordContainment(List<AuthorWord> authorWords, string fieldValue, string fieldName)
        {
            var fieldWords = ExtractFieldWords(fieldValue);
            var fieldWordSet = new HashSet<string>(fieldWords, StringComparer.OrdinalIgnoreCase);
            
            // Separate word types, excluding particles
            var authorInitials = authorWords.Where(w => w.Type == WordType.Initial).ToList();
            var authorFullWords = authorWords.Where(w => w.Type == WordType.Full).ToList();
            var authorParticles = authorWords.Where(w => w.Type == WordType.Particle).ToList();

            // Identify first and last name
            string firstName = null;
            string lastName = null;
            
            if (authorFullWords.Any())
            {
                // Get first full word (skipping particles at the beginning)
                firstName = authorFullWords.First().Word;
                
                // Get last full word, potentially with particle prefix
                if (authorFullWords.Count > 1)
                {
                    var lastFullWord = authorFullWords.Last();
                    lastName = lastFullWord.Word;
                    
                    // Check if there's a particle before the last name
                    var particleBeforeLastName = authorWords
                        .Where(w => w.Type == WordType.Particle && w.Position == lastFullWord.Position - 1)
                        .FirstOrDefault();
                        
                    if (particleBeforeLastName != null)
                    {
                        // Include the particle as part of the last name
                        lastName = particleBeforeLastName.Word + " " + lastName;
                    }
                }
            }

            _logger.Trace("[CONTAINMENT] Field '{0}': Looking for first='{1}', last='{2}'", 
                fieldName, firstName ?? "(none)", lastName ?? "(none)");

            // Check first name requirement
            if (!string.IsNullOrEmpty(firstName) && !fieldWordSet.Contains(firstName))
            {
                _logger.Trace("[CONTAINMENT] Field '{0}' missing first name '{1}'", fieldName, firstName);
                return false;
            }

            // Check last name requirement (with potential particle)
            if (!string.IsNullOrEmpty(lastName))
            {
                // Try exact match first (for compound last names with particles)
                var lastNameFound = false;
                
                if (lastName.Contains(" "))
                {
                    // Check if the compound last name exists as consecutive words in the field
                    var parts = lastName.Split(' ');
                    if (parts.All(part => fieldWordSet.Contains(part)))
                    {
                        lastNameFound = true;
                        _logger.Trace("[CONTAINMENT] Found compound last name parts: {0}", lastName);
                    }
                }
                else if (fieldWordSet.Contains(lastName))
                {
                    lastNameFound = true;
                }
                
                if (!lastNameFound)
                {
                    _logger.Trace("[CONTAINMENT] Field '{0}' missing last name '{1}'", fieldName, lastName);
                    return false;
                }
            }

            // Handle initials based on position
            if (authorInitials.Any())
            {
                var initialGroups = GroupConsecutiveInitials(authorWords);
                
                for (int groupIndex = 0; groupIndex < initialGroups.Count; groupIndex++)
                {
                    var group = initialGroups[groupIndex];
                    var groupPosition = group.First().Position;
                    
                    // Determine if this initial group is required based on position
                    bool isRequired = false;
                    
                    // First initial group is required if it's at the start
                    if (groupIndex == 0 && groupPosition == 0)
                    {
                        isRequired = true;
                    }
                    // Last initial group is required if it's at the end
                    else if (groupIndex == initialGroups.Count - 1 && 
                             group.Last().Position == authorWords.Count - 1)
                    {
                        isRequired = true;
                    }
                    // Middle initials are optional
                    
                    if (!isRequired)
                    {
                        _logger.Trace("[CONTAINMENT] Initial group [{0}] at position {1} is optional", 
                            string.Join("", group.Select(w => w.Word)), groupPosition);
                        continue;
                    }
                    
                    // Check if required initials are present
                    bool groupMatched = false;
                    
                    // Check if the cluster exists
                    var clusterForm = string.Join("", group.Select(w => w.Word));
                    if (fieldWordSet.Contains(clusterForm))
                    {
                        _logger.Trace("[CONTAINMENT] Found initial cluster '{0}' in field", clusterForm);
                        groupMatched = true;
                    }
                    // Check if all initials in group exist separately
                    else if (group.All(w => fieldWordSet.Contains(w.Word)))
                    {
                        _logger.Trace("[CONTAINMENT] Found initials [{0}] separately in field", 
                            string.Join(", ", group.Select(w => w.Word)));
                        groupMatched = true;
                    }
                    
                    if (!groupMatched)
                    {
                        _logger.Trace("[CONTAINMENT] Required initial group [{0}] at position {1} not found", 
                            string.Join("", group.Select(w => w.Word)), groupPosition);
                        return false;
                    }
                }
            }

            _logger.Trace("[CONTAINMENT] Field '{0}' contains required author components", fieldName);
            return true;
        }

        private List<string> ExtractFieldWords(string text)
        {
            var normalized = NormalizeForWordExtraction(text);
            var words = WordPattern.Matches(normalized);
            var result = new List<string>();

            foreach (Match match in words)
            {
                var word = match.Value.ToLowerInvariant();
                result.Add(word);
            }

            // Look for actual initial clusters (consecutive single letters)
            // This helps match "J.K." written as "JK" in tags
            var singleLetters = new List<(char letter, int index)>();
            var cleanText = normalized.ToLowerInvariant();
            
            for (int i = 0; i < cleanText.Length; i++)
            {
                if (char.IsLetter(cleanText[i]))
                {
                    // Check if this is a single letter (not part of a larger word)
                    bool isStart = i == 0 || !char.IsLetter(cleanText[i - 1]);
                    bool isEnd = i == cleanText.Length - 1 || !char.IsLetter(cleanText[i + 1]);
                    
                    if (isStart && isEnd)
                    {
                        singleLetters.Add((cleanText[i], i));
                    }
                }
            }
            
            // Create clusters from consecutive single letters
            for (int i = 0; i < singleLetters.Count - 1; i++)
            {
                // Check if the next letter is close enough to be a cluster
                if (singleLetters[i + 1].index - singleLetters[i].index <= 3)
                {
                    var cluster = new string(new[] { singleLetters[i].letter, singleLetters[i + 1].letter });
                    result.Add(cluster);
                    
                    // Also try three-letter clusters
                    if (i < singleLetters.Count - 2 && singleLetters[i + 2].index - singleLetters[i + 1].index <= 3)
                    {
                        var tripleCluster = new string(new[] { singleLetters[i].letter, singleLetters[i + 1].letter, singleLetters[i + 2].letter });
                        result.Add(tripleCluster);
                    }
                }
            }

            return result.Distinct().ToList();
        }

        private List<List<AuthorWord>> GroupConsecutiveInitials(List<AuthorWord> words)
        {
            var groups = new List<List<AuthorWord>>();
            List<AuthorWord> currentGroup = null;

            for (int i = 0; i < words.Count; i++)
            {
                if (words[i].Type == WordType.Initial)
                {
                    if (currentGroup == null)
                    {
                        currentGroup = new List<AuthorWord>();
                        groups.Add(currentGroup);
                    }
                    currentGroup.Add(words[i]);
                }
                else if (words[i].Type == WordType.Full)
                {
                    currentGroup = null;
                }
                // Skip cluster types in grouping
            }

            return groups;
        }

        private string NormalizeForWordExtraction(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            // Unicode normalization to decomposed form
            text = text.Normalize(NormalizationForm.FormD);

            // Remove combining diacritical marks
            var sb = new StringBuilder();
            foreach (var ch in text)
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(ch);
                if (category != UnicodeCategory.NonSpacingMark)
                {
                    sb.Append(ch);
                }
            }
            text = sb.ToString();

            // Normalize back to composed form
            text = text.Normalize(NormalizationForm.FormC);

            // Basic normalization while preserving word structure
            text = text.Trim();

            // Normalize apostrophes and quotes to standard forms
            // Apostrophes: ' ` ´ ′ ʼ ’ ‘ ‛ ＇
            text = Regex.Replace(text, @"[`\u00B4\u02BC\u2018\u2019\u201B\u2032\uFF07]", "'");
            // Quotes: “ ” „ ‟ « » ＂
            text = Regex.Replace(text, @"[\u00AB\u00BB\u201C\u201D\u201E\u201F\uFF02]", "\"");

            // CRITICAL: Possessive normalization - convert 's to s (merge with word)
            // This matches FTS tokenizer behavior in FileMatchingService.TokenizeText
            // "Housemaid's" → "Housemaids", "Philosopher's" → "Philosophers"
            // Does NOT affect: Can't, O'Brien, ma'am (not word-final 's)
            text = Regex.Replace(text, @"(\p{L})'s\b", "$1s");

            // Normalize dashes to spaces for word separation
            text = Regex.Replace(text, @"[–—-]", " ");

            // Remove periods after single letters (initials)
            text = Regex.Replace(text, @"\b(\p{L})\.(?=\s|$)", "$1");

            // Collapse whitespace
            text = Regex.Replace(text, @"\s+", " ");

            return text;
        }

        private enum WordType
        {
            Initial,        // Single letter
            Full,           // Multi-letter word  
            InitialCluster, // Combined initials like "jk"
            Particle        // Surname particles like "de", "van", etc.
        }

        private class AuthorWord
        {
            public string Word { get; set; }
            public WordType Type { get; set; }
            public int Position { get; set; } // Position in the original word sequence
        }
    }
}
