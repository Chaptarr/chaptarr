using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.MediaFiles.TagExtraction;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Utilities;
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
            bool requiresAudiobookDurationCorroboration = false,
            bool isNumberEquivalent = false,
            string editionLanguage = null)
        {
            FieldName = fieldName;
            FieldValue = fieldValue;
            MatchedTitle = matchedTitle;
            IsNearExact = isNearExact;
            RequiresAudiobookDurationCorroboration = requiresAudiobookDurationCorroboration;
            IsNumberEquivalent = isNumberEquivalent;
            EditionLanguage = editionLanguage;
        }

        public string FieldName { get; }
        public string FieldValue { get; }
        public string MatchedTitle { get; }
        public bool IsNearExact { get; }
        public bool RequiresAudiobookDurationCorroboration { get; }
        public bool IsNumberEquivalent { get; }
        public string EditionLanguage { get; }
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
        bool ValidateAuthorInTags(string authorName, IDictionary<string, List<string>> allTags, IEnumerable<string> knownTitles);

        /// <summary>
        /// Validates that an edition title returned from V5 is contained in the tags
        /// Checks each field individually, not a concatenated blob
        /// </summary>
        bool ValidateEditionInTags(string editionTitle, IDictionary<string, List<string>> allTags, IEnumerable<string> knownNarrators = null);

        /// <summary>
        /// Returns the tag field(s) that caused an edition title to be considered contained.
        /// Used for higher-level validation (e.g., leftover-token checks) against the same evidence field.
	        /// </summary>
	        IReadOnlyList<EditionTitleEvidence> GetEditionTitleEvidence(string editionTitle, IDictionary<string, List<string>> allTags, bool includeDurationGatedNearExact = false, IEnumerable<string> knownNarrators = null);
	    }

	    public class ContainmentValidator : IContainmentValidator
	    {
	        private readonly ITagNormalizer _tagNormalizer;
	        private readonly Logger _logger;

	        // Edition titles are never plot summaries. If a tag value is longer than this, treat it as
	        // ineligible for edition-title evidence (defense-in-depth against non-standard comment fields).
	        internal const int MaxTitleEvidenceValueLength = 400;
	        
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
            return ValidateAuthorInTags(authorName, allTags, null);
        }

        public bool ValidateAuthorInTags(string authorName, IDictionary<string, List<string>> allTags, IEnumerable<string> knownTitles)
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
            var expectedName = AuthorNameMatcher.PrepareName(authorName);
            var titleTokens = (knownTitles ?? Enumerable.Empty<string>())
                .Where(title => !string.IsNullOrWhiteSpace(title))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(ReleaseTitleMatchScorer.Tokenize)
                .Where(tokens => tokens.Count > 0)
                .ToList();
            
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

                    var fieldTokens = ReleaseTitleMatchScorer.TokenizeWithSpans(fieldValue);
                    var exactTitleSpans = new List<(int Start, int End)>();
                    if (titleTokens.Count > 0)
                    {
                        var fieldWords = fieldTokens.Select(token => token.Value).ToList();
                        exactTitleSpans.AddRange(titleTokens.SelectMany(title =>
                            ReleaseTitleMatchScorer.FindExactTitleSpans(fieldWords, title)).Distinct());
                    }
                    if (AuthorNameMatcher.ContainsAuthorName(expectedName, fieldValue, fieldTokens, exactTitleSpans))
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

        public bool ValidateEditionInTags(string editionTitle, IDictionary<string, List<string>> allTags, IEnumerable<string> knownNarrators = null)
        {
            editionTitle = ReleaseNarratorEvidenceExtractor.RemoveKnownNarratorNotes(editionTitle, knownNarrators);
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

            if (GetSplitTitleEvidence(editionTitle, allTags).Count > 0)
            {
                return true;
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

        public IReadOnlyList<EditionTitleEvidence> GetEditionTitleEvidence(string editionTitle, IDictionary<string, List<string>> allTags, bool includeDurationGatedNearExact = false, IEnumerable<string> knownNarrators = null)
        {
            editionTitle = ReleaseNarratorEvidenceExtractor.RemoveKnownNarratorNotes(editionTitle, knownNarrators);
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

            evidence.AddRange(GetSplitTitleEvidence(editionTitle, allTags));
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
            var remaining = TitleBracketCatalog.BaseTitle(editionTitle);

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

        private IReadOnlyList<EditionTitleEvidence> GetSplitTitleEvidence(string title, IDictionary<string, List<string>> tags)
        {
            // ASCII and full-width colons both mark a title/subtitle split.
            var separator = title.IndexOfAny(NzbDrone.Core.Parser.Parser.SubtitleSeparators);
            if (separator <= 0 || separator == title.Length - 1)
            {
                return Array.Empty<EditionTitleEvidence>();
            }

            var head = title.Substring(0, separator).Trim();
            var tail = title.Substring(separator + 1).Trim();
            var headTokens = GetEditionTokensForValidation(head);
            var tailTokens = GetEditionTokensForValidation(tail);
            if (headTokens.Count == 0 || tailTokens.Count == 0)
            {
                return Array.Empty<EditionTitleEvidence>();
            }

            var heads = new List<EditionTitleEvidence>();
            var tails = new List<EditionTitleEvidence>();
            foreach (var field in tags)
            {
                if (IsExcludedFromEditionTitleEvidenceKey(field.Key) || field.Value == null)
                {
                    continue;
                }

                foreach (var value in field.Value)
                {
                    if (string.IsNullOrWhiteSpace(value) || value.Length > MaxTitleEvidenceValueLength ||
                        TagCanonicalizer.IsCanonicalAliasBackedByRawSource(field.Key, value, tags))
                    {
                        continue;
                    }

                    var tokens = ExtractTokensAsList(NormalizeForWordExtraction(value).ToLowerInvariant());
                    if (CanConsumeAllTokens(headTokens, tokens))
                    {
                        heads.Add(new EditionTitleEvidence(field.Key, value, head));
                    }

                    if (CanConsumeAllTokens(tailTokens, tokens))
                    {
                        tails.Add(new EditionTitleEvidence(field.Key, value, tail));
                    }
                }
            }

            // Each complete part needs its own physical field. Preserve those source spans for
            // token consumption; a generated alias cannot supply the same occurrence twice.
            return heads.Where(headEvidence => tails.Any(tailEvidence =>
                    !string.Equals(headEvidence.FieldName, tailEvidence.FieldName, StringComparison.OrdinalIgnoreCase)))
                .Concat(tails.Where(tailEvidence => heads.Any(headEvidence =>
                    !string.Equals(headEvidence.FieldName, tailEvidence.FieldName, StringComparison.OrdinalIgnoreCase))))
                .ToList();
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
            // Peel trailing bracket groups from the end.
            var trailingGroups = new List<string>();
            var remaining = TitleBracketCatalog.BaseTitle(editionTitle, trailingGroups);
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

	            // Catalog parenthetical content needs observed evidence; vocabulary alone cannot establish it.
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


	                // A parenthetical must have ordered evidence in one field, preserving credit and position boundaries.
	                if (!GroupTokensContainedInAnySingleField(groupTokens))
	                {
	                    return false;
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
            // Keep numeric evidence after the words so an inserted series position does not interrupt title alignment.
            return UnicodeComparisonNormalizer.WordSpans(normalized)
                .OrderBy(word => word.Value.All(char.IsDigit))
                .Select(word => word.Value)
                .ToList();
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

        private string NormalizeForWordExtraction(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            text = UnicodeComparisonNormalizer.NormalizeText(text).Trim();

            // Normalize apostrophes and quotes to standard forms
            // Apostrophes: ' ` ´ ′ ʼ ’ ‘ ‛ ＇
            text = Regex.Replace(text, @"[`\u00B4\u02BC\u2018\u2019\u201B\u2032\uFF07]", "'");
            // Quotes: “ ” „ ‟ « » ＂
            text = Regex.Replace(text, @"[\u00AB\u00BB\u201C\u201D\u201E\u201F\uFF02]", "\"");

            // CRITICAL: Possessive normalization - convert 's to s (merge with word)
            // Keep tag tokens aligned with the shared comparison tokenizer.
            // "Housemaid's" → "Housemaids", "Philosopher's" → "Philosophers"
            // Does NOT affect: Can't, O'Brien, ma'am (not word-final 's)
            text = Regex.Replace(text, @"'s\b", "s");

            // Normalize dashes to spaces for word separation
            text = Regex.Replace(text, @"[–—-]", " ");

            // Remove periods after single letters (initials)
            text = Regex.Replace(text, @"\b(\p{L})\.(?=\s|$)", "$1");

            // Collapse whitespace
            text = Regex.Replace(text, @"\s+", " ");

            return text;
        }

    }
}
