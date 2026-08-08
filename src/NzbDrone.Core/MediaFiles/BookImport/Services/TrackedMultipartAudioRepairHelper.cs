using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles.BookImport.Services
{
    internal static class TrackedMultipartAudioRepairHelper
    {
        private static readonly Regex ProofTokenRegex = new Regex(@"[\p{L}\p{Nd}]+", RegexOptions.Compiled);
        private static readonly Regex RomanNumeralRegex = new Regex(@"^(m{0,4}(cm|cd|d?c{0,3})(xc|xl|l?x{0,3})(ix|iv|v?i{0,3}))$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly HashSet<string> PackagingTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "part", "disc", "disk", "cd", "track"
        };

        internal static List<ImportDecision<LocalBook>> RepairTrackedSingleBookAudioDecisions(
            IEnumerable<ImportDecision<LocalBook>> sourceDecisions,
            Book targetBook,
            Author targetAuthor,
            Edition preferredEdition,
            IEditionService editionService,
            Logger logger,
            string contextLabel,
            IContainmentValidator containmentValidator = null)
        {
            var decisions = sourceDecisions?.ToList() ?? new List<ImportDecision<LocalBook>>();
            if (decisions.Count < 2 || targetBook?.Id <= 0)
            {
                return decisions;
            }

            targetAuthor ??= targetBook.Author;
            if (targetAuthor == null || targetAuthor.Id <= 0)
            {
                return decisions;
            }

            var audioIndexes = decisions
                .Select((decision, index) => new { decision, index })
                .Where(x => IsAudioDecision(x.decision))
                .Select(x => x.index)
                .ToList();

            if (audioIndexes.Count < 2)
            {
                return decisions;
            }

            containmentValidator ??= new ContainmentValidator(new TagNormalizer(), logger);

            var probes = decisions
                .Select((decision, index) => BuildProbe(decision, index, containmentValidator))
                .Where(probe => probe != null)
                .ToList();

            if (probes.Count < 2)
            {
                return decisions;
            }

            var contract = SelectBestContract(probes, targetBook.Id);
            if (contract == null)
            {
                return decisions;
            }

            var repairIndexes = decisions
                .Select((decision, index) => new { decision, index })
                .Where(x => audioIndexes.Contains(x.index))
                .Where(x => TagsSatisfyContract(ToCaseInsensitiveTags(x.decision?.Item?.RawTags?.AllTags), contract))
                .Select(x => x.index)
                .Distinct()
                .ToList();

            if (repairIndexes.Count < 2)
            {
                return decisions;
            }

            var clusterTags = ExactMatchEvidenceBuilder.MergeTagSets(
                repairIndexes
                    .Select(index => (IDictionary<string, List<string>>)ToCaseInsensitiveTags(decisions[index]?.Item?.RawTags?.AllTags))
                    .ToArray());

            var selectedEdition = SelectTrackedBookEdition(
                targetBook,
                targetAuthor,
                preferredEdition,
                clusterTags,
                editionService,
                containmentValidator);

            if (selectedEdition == null)
            {
                logger?.Debug("[TRACKED-MULTIPART] Skipping repair for {0}: no validated edition for target book {1} ('{2}')",
                    contextLabel ?? "<unknown>", targetBook.Id, targetBook.Title ?? "<unknown>");
                return decisions;
            }

            logger?.Info("[TRACKED-MULTIPART] Repairing {0} audio file(s) for {1}: target book {2} ('{3}'), edition {4} ('{5}')",
                repairIndexes.Count,
                contextLabel ?? "<unknown>",
                targetBook.Id,
                targetBook.Title ?? "<unknown>",
                selectedEdition.Id,
                selectedEdition.Title ?? "<unknown>");

            var repaired = new List<ImportDecision<LocalBook>>(decisions.Count);
            for (var index = 0; index < decisions.Count; index++)
            {
                var decision = decisions[index];
                if (!repairIndexes.Contains(index))
                {
                    repaired.Add(decision);
                    continue;
                }

                var localBook = decision?.Item ?? new LocalBook();
                localBook.Author = targetAuthor;
                localBook.Book = targetBook;
                localBook.Edition = selectedEdition;

                repaired.Add(new ImportDecision<LocalBook>(localBook));
            }

            return repaired;
        }

        internal static Edition ResolveExpectedTrackedEdition(Book book, string downloadId, IHistoryService historyService, IEditionService editionService)
        {
            if (book == null || book.Id <= 0 || editionService == null)
            {
                return null;
            }

            var editionsForBook = (editionService.GetEditionsByBook(book.Id) ?? new List<Edition>())
                .Where(edition => edition != null)
                .ToList();

            if (!string.IsNullOrWhiteSpace(downloadId) && historyService != null)
            {
                var historicalEditionId = (historyService.FindByDownloadId(downloadId) ?? new List<EntityHistory>())
                    .Where(history => history.EventType == EntityHistoryEventType.Grabbed &&
                                      history.BookId == book.Id &&
                                      history.EditionId > 0)
                    .OrderByDescending(history => history.Date)
                    .Select(history => history.EditionId)
                    .FirstOrDefault();

                if (historicalEditionId > 0)
                {
                    var historicalEdition = editionsForBook.FirstOrDefault(edition => edition.Id == historicalEditionId)
                        ?? editionService.GetEdition(historicalEditionId);

                    if (historicalEdition?.BookId == book.Id)
                    {
                        return historicalEdition;
                    }
                }
            }

            return editionsForBook.FirstOrDefault(edition => edition.Monitored);
        }

        private static RepairContract SelectBestContract(IReadOnlyList<ProbeSnapshot> probes, int targetBookId)
        {
            RepairContract best = null;
            var bestScore = (Members: 0, TargetMembers: 0, ExactFields: 0, Fields: 0, Values: 0);

            for (var i = 0; i < probes.Count; i++)
            {
                for (var j = i + 1; j < probes.Count; j++)
                {
                    var authorContract = BuildContractTagSet(probes[i].AuthorTags, probes[j].AuthorTags);
                    var bookContract = BuildContractTagSet(probes[i].BookTags, probes[j].BookTags);
                    if (authorContract.FieldCount == 0 || bookContract.FieldCount == 0)
                    {
                        continue;
                    }

                    var narratorContract = BuildContractTagSet(probes[i].NarratorTags, probes[j].NarratorTags);
                    var contract = MergeRepairContracts(authorContract, bookContract, narratorContract);
                    if (contract.FieldCount == 0)
                    {
                        continue;
                    }

                    var members = probes
                        .Where(probe => TagsSatisfyContract(probe.RawTags, contract))
                        .Select(probe => probe.DecisionIndex)
                        .Distinct()
                        .ToList();

                    if (members.Count < 2)
                    {
                        continue;
                    }

                    var targetMembers = probes
                        .Where(probe => members.Contains(probe.DecisionIndex) && probe.BookId == targetBookId)
                        .Select(probe => probe.DecisionIndex)
                        .Distinct()
                        .Count();

                    var score = (
                        Members: members.Count,
                        TargetMembers: targetMembers,
                        ExactFields: contract.ExactFieldCount,
                        Fields: contract.FieldCount,
                        Values: contract.ValueCount);

                    if (score.Members > bestScore.Members ||
                        (score.Members == bestScore.Members && score.TargetMembers > bestScore.TargetMembers) ||
                        (score.Members == bestScore.Members && score.TargetMembers == bestScore.TargetMembers && score.ExactFields > bestScore.ExactFields) ||
                        (score.Members == bestScore.Members && score.TargetMembers == bestScore.TargetMembers && score.ExactFields == bestScore.ExactFields && score.Fields > bestScore.Fields) ||
                        (score.Members == bestScore.Members && score.TargetMembers == bestScore.TargetMembers && score.ExactFields == bestScore.ExactFields && score.Fields == bestScore.Fields && score.Values > bestScore.Values))
                    {
                        contract.MatchedMemberIndexes = members;
                        best = contract;
                        bestScore = score;
                    }
                }
            }

            return best;
        }

        private static ProbeSnapshot BuildProbe(ImportDecision<LocalBook> decision, int decisionIndex, IContainmentValidator containmentValidator)
        {
            var localBook = decision?.Item;
            var tags = ToCaseInsensitiveTags(localBook?.RawTags?.AllTags);
            var author = localBook?.Author;
            var book = localBook?.Book;
            var edition = localBook?.Edition;

            if (decision == null || !decision.Approved || tags.Count == 0 || author == null || book == null || edition == null)
            {
                return null;
            }

            var evidence = ExactMatchEvidenceBuilder.Build(author.Name, book.Title, edition, tags, containmentValidator);
            if (evidence.AuthorTags.Count == 0 || evidence.BookTags.Count == 0)
            {
                return null;
            }

            return new ProbeSnapshot
            {
                DecisionIndex = decisionIndex,
                BookId = book.Id,
                RawTags = tags,
                AuthorTags = evidence.AuthorTags,
                BookTags = evidence.BookTags,
                NarratorTags = evidence.NarratorTags
            };
        }

        private static Edition SelectTrackedBookEdition(
            Book targetBook,
            Author targetAuthor,
            Edition preferredEdition,
            IDictionary<string, List<string>> clusterTags,
            IEditionService editionService,
            IContainmentValidator containmentValidator)
        {
            if (clusterTags == null || clusterTags.Count == 0 || !containmentValidator.ValidateAuthorInTags(targetAuthor?.Name, clusterTags))
            {
                return null;
            }

            var allEditions = (targetBook.Editions ?? new List<Edition>())
                .Where(edition => edition != null)
                .ToList();

            if (!allEditions.Any() && targetBook.Id > 0)
            {
                allEditions = (editionService?.GetEditionsByBook(targetBook.Id) ?? new List<Edition>())
                    .Where(edition => edition != null)
                    .ToList();
            }

            if (preferredEdition != null && allEditions.All(edition => edition.Id != preferredEdition.Id))
            {
                allEditions.Add(preferredEdition);
            }

            var audiobookEditions = allEditions
                .Where(edition => edition.ReadingFormatId == 0 || edition.ReadingFormatId == 2)
                .ToList();

            if (!audiobookEditions.Any())
            {
                audiobookEditions = allEditions;
            }

            if (!audiobookEditions.Any())
            {
                return null;
            }

            var strictEdition = !targetBook.AnyEditionOk || preferredEdition?.ManualAdd == true;
            var candidates = audiobookEditions
                .Select(edition =>
                {
                    var evidence = ExactMatchEvidenceBuilder.Build(targetAuthor?.Name, targetBook.Title, edition, clusterTags, containmentValidator);

                    return new EditionCandidate
                    {
                        Edition = edition,
                        BookEvidenceCount = CountValues(evidence.BookTags),
                        NarratorEvidenceCount = CountValues(evidence.NarratorTags)
                    };
                })
                .ToList();

            if (strictEdition)
            {
                if (preferredEdition == null)
                {
                    return null;
                }

                var strictCandidate = candidates.FirstOrDefault(candidate => candidate.Edition.Id == preferredEdition.Id);
                return HasStrictEvidence(strictCandidate) ? strictCandidate.Edition : null;
            }

            var candidatesWithEditionEvidence = candidates
                .Where(candidate => candidate.BookEvidenceCount > 0)
                .OrderByDescending(candidate => candidate.NarratorEvidenceCount)
                .ThenByDescending(candidate => candidate.Edition.Id == preferredEdition?.Id)
                .ThenByDescending(candidate => candidate.Edition.Monitored)
                .ThenByDescending(candidate => candidate.BookEvidenceCount)
                .ThenBy(candidate => candidate.Edition.Id)
                .ToList();

            if (candidatesWithEditionEvidence.Any())
            {
                return candidatesWithEditionEvidence.First().Edition;
            }

            return containmentValidator.ValidateEditionInTags(targetBook.Title, clusterTags)
                ? preferredEdition ?? audiobookEditions.FirstOrDefault(edition => edition.Monitored) ?? audiobookEditions.First()
                : null;
        }

        private static bool HasStrictEvidence(EditionCandidate candidate)
        {
            if (candidate == null || candidate.BookEvidenceCount <= 0)
            {
                return false;
            }

            return ExactMatchEvidenceBuilder.GetNarratorCandidates(candidate.Edition).Count == 0 || candidate.NarratorEvidenceCount > 0;
        }

        private static ContractTagSet BuildContractTagSet(
            IDictionary<string, List<string>> left,
            IDictionary<string, List<string>> right)
        {
            var exactTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var normalizedTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            if (left == null || right == null)
            {
                return new ContractTagSet(exactTags, normalizedTags);
            }

            foreach (var kvp in left)
            {
                if (string.IsNullOrWhiteSpace(kvp.Key) || kvp.Value == null || kvp.Value.Count == 0)
                {
                    continue;
                }

                if (!right.TryGetValue(kvp.Key, out var otherValues) || otherValues == null || otherValues.Count == 0)
                {
                    continue;
                }

                var exactValues = kvp.Value
                    .Where(value => !string.IsNullOrWhiteSpace(value) && otherValues.Any(other => string.Equals(other, value, StringComparison.Ordinal)))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                if (exactValues.Count > 0)
                {
                    exactTags[kvp.Key] = exactValues;
                    continue;
                }

                var normalizedValues = FindExplainedNormalizedMatches(kvp.Value, otherValues);
                if (normalizedValues.Count > 0)
                {
                    normalizedTags[kvp.Key] = normalizedValues;
                }
            }

            return new ContractTagSet(exactTags, normalizedTags);
        }

        private static List<string> FindExplainedNormalizedMatches(IEnumerable<string> leftValues, IEnumerable<string> rightValues)
        {
            var normalizedValues = new HashSet<string>(StringComparer.Ordinal);
            var leftList = (leftValues ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var rightList = (rightValues ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            foreach (var left in leftList)
            {
                var leftComparable = NormalizeComparableValue(left);
                var leftPackaging = NormalizePackagingValue(left);
                if (string.IsNullOrWhiteSpace(leftPackaging))
                {
                    continue;
                }

                foreach (var right in rightList)
                {
                    if (string.Equals(left, right, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var rightComparable = NormalizeComparableValue(right);
                    var rightPackaging = NormalizePackagingValue(right);
                    if (string.IsNullOrWhiteSpace(rightPackaging) || !string.Equals(leftPackaging, rightPackaging, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var leftChanged = !string.Equals(leftComparable, leftPackaging, StringComparison.Ordinal);
                    var rightChanged = !string.Equals(rightComparable, rightPackaging, StringComparison.Ordinal);
                    if (!leftChanged && !rightChanged)
                    {
                        continue;
                    }

                    normalizedValues.Add(leftPackaging);
                }
            }

            return normalizedValues.ToList();
        }

        private static RepairContract MergeRepairContracts(params ContractTagSet[] parts)
        {
            var exactTags = ExactMatchEvidenceBuilder.MergeTagSets(parts.Select(part => (IDictionary<string, List<string>>)part.ExactTags).ToArray());
            var normalizedTags = ExactMatchEvidenceBuilder.MergeTagSets(parts.Select(part => (IDictionary<string, List<string>>)part.NormalizedTags).ToArray());

            return new RepairContract
            {
                ExactTags = exactTags,
                NormalizedTags = normalizedTags
            };
        }

        private static bool TagsSatisfyContract(IDictionary<string, List<string>> rawTags, RepairContract contract)
        {
            if (rawTags == null || rawTags.Count == 0 || contract == null || contract.FieldCount == 0)
            {
                return false;
            }

            return TagsContainExactTags(rawTags, contract.ExactTags) &&
                   TagsContainNormalizedTags(rawTags, contract.NormalizedTags);
        }

        private static bool TagsContainExactTags(
            IDictionary<string, List<string>> rawTags,
            IDictionary<string, List<string>> requiredTags)
        {
            if (requiredTags == null || requiredTags.Count == 0)
            {
                return true;
            }

            foreach (var required in requiredTags)
            {
                if (string.IsNullOrWhiteSpace(required.Key) || required.Value == null || required.Value.Count == 0)
                {
                    return false;
                }

                if (!rawTags.TryGetValue(required.Key, out var rawValues) || rawValues == null || rawValues.Count == 0)
                {
                    return false;
                }

                foreach (var requiredValue in required.Value)
                {
                    if (!rawValues.Any(rawValue => string.Equals(rawValue, requiredValue, StringComparison.Ordinal)))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool TagsContainNormalizedTags(
            IDictionary<string, List<string>> rawTags,
            IDictionary<string, List<string>> requiredTags)
        {
            if (requiredTags == null || requiredTags.Count == 0)
            {
                return true;
            }

            foreach (var required in requiredTags)
            {
                if (string.IsNullOrWhiteSpace(required.Key) || required.Value == null || required.Value.Count == 0)
                {
                    return false;
                }

                if (!rawTags.TryGetValue(required.Key, out var rawValues) || rawValues == null || rawValues.Count == 0)
                {
                    return false;
                }

                var normalizedValues = rawValues
                    .Select(NormalizePackagingValue)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                foreach (var requiredValue in required.Value)
                {
                    if (!normalizedValues.Contains(requiredValue, StringComparer.Ordinal))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static Dictionary<string, List<string>> ToCaseInsensitiveTags(Dictionary<string, List<string>> tags)
        {
            var output = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (tags == null || tags.Count == 0)
            {
                return output;
            }

            foreach (var kvp in tags)
            {
                if (string.IsNullOrWhiteSpace(kvp.Key) || kvp.Value == null)
                {
                    continue;
                }

                output[kvp.Key] = kvp.Value
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            return output;
        }

        private static string NormalizeComparableValue(string value)
        {
            return NormalizeProofValue(value, stripPackagingTokens: false);
        }

        private static string NormalizePackagingValue(string value)
        {
            return NormalizeProofValue(value, stripPackagingTokens: true);
        }

        private static string NormalizeProofValue(string value, bool stripPackagingTokens)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var tokens = ProofTokenRegex
                .Matches(value.ToLowerInvariant())
                .Cast<Match>()
                .Select(match => match.Value)
                .ToList();

            if (tokens.Count == 0)
            {
                return null;
            }

            if (!stripPackagingTokens)
            {
                return string.Join(" ", tokens);
            }

            var filtered = new List<string>();
            for (var index = 0; index < tokens.Count; index++)
            {
                var token = tokens[index];
                if (PackagingTokens.Contains(token))
                {
                    var previousIsPartNumber = index > 0 && IsPartNumberToken(tokens[index - 1]);
                    var nextIsPartNumber = index + 1 < tokens.Count && IsPartNumberToken(tokens[index + 1]);
                    if (previousIsPartNumber || nextIsPartNumber)
                    {
                        if (nextIsPartNumber)
                        {
                            index++;
                        }

                        continue;
                    }
                }

                if (IsPartNumberToken(token))
                {
                    var previousIsPackaging = index > 0 && PackagingTokens.Contains(tokens[index - 1]);
                    var nextIsPackaging = index + 1 < tokens.Count && PackagingTokens.Contains(tokens[index + 1]);
                    if (previousIsPackaging || nextIsPackaging)
                    {
                        continue;
                    }
                }

                filtered.Add(token);
            }

            return filtered.Count == 0 ? null : string.Join(" ", filtered);
        }

        private static bool IsPartNumberToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            return token.All(char.IsDigit) || RomanNumeralRegex.IsMatch(token);
        }

        private static bool IsAudioDecision(ImportDecision<LocalBook> decision)
        {
            var path = decision?.Item?.Path;
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var extension = Path.GetExtension(path) ?? string.Empty;
            return MediaFileExtensions.AudioExtensions.Contains(extension);
        }

        private static int CountValues(IDictionary<string, List<string>> tags)
        {
            return tags?.Sum(kvp => kvp.Value?.Count ?? 0) ?? 0;
        }

        private sealed class ProbeSnapshot
        {
            public int DecisionIndex { get; set; }
            public int BookId { get; set; }
            public Dictionary<string, List<string>> RawTags { get; set; }
            public Dictionary<string, List<string>> AuthorTags { get; set; }
            public Dictionary<string, List<string>> BookTags { get; set; }
            public Dictionary<string, List<string>> NarratorTags { get; set; }
        }

        private sealed class ContractTagSet
        {
            public ContractTagSet(Dictionary<string, List<string>> exactTags, Dictionary<string, List<string>> normalizedTags)
            {
                ExactTags = exactTags ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                NormalizedTags = normalizedTags ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            }

            public Dictionary<string, List<string>> ExactTags { get; }
            public Dictionary<string, List<string>> NormalizedTags { get; }
            public int ExactFieldCount => ExactTags.Count;
            public int FieldCount => ExactTags.Count + NormalizedTags.Count;
        }

        private sealed class RepairContract
        {
            public Dictionary<string, List<string>> ExactTags { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, List<string>> NormalizedTags { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public List<int> MatchedMemberIndexes { get; set; } = new();
            public int ExactFieldCount => ExactTags.Count;
            public int FieldCount => ExactTags.Count + NormalizedTags.Count;
            public int ValueCount => ExactTags.Sum(kvp => kvp.Value.Count) + NormalizedTags.Sum(kvp => kvp.Value.Count);
        }

        private sealed class EditionCandidate
        {
            public Edition Edition { get; set; }
            public int BookEvidenceCount { get; set; }
            public int NarratorEvidenceCount { get; set; }
        }
    }
}
