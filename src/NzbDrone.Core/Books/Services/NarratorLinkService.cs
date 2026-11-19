using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books.Repositories;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Parser;

namespace NzbDrone.Core.Books.Services
{
    public interface INarratorLinkService
    {
        void UpsertEditionNarratorLinks(IReadOnlyCollection<Edition> editions);
        void RebuildBookNarratorLinks(IEnumerable<int> bookIds);
    }

    public class NarratorLinkService : INarratorLinkService
    {
        private sealed class EditionNarratorCreditRow
        {
            public int EditionId { get; init; }
            public NarratorCredit Credit { get; init; }
            public string GoodreadsId { get; init; }
            public string HardcoverId { get; init; }
            public string CleanName { get; init; }
            public Narrator Narrator { get; set; }
        }

        private readonly IEditionNarratorLinkRepository _editionNarratorLinkRepository;
        private readonly IBookNarratorLinkRepository _bookNarratorLinkRepository;
        private readonly INarratorMetadataRepository _narratorMetadataRepository;
        private readonly INarratorRepository _narratorRepository;
        private readonly Logger _logger;

        public NarratorLinkService(
            IEditionNarratorLinkRepository editionNarratorLinkRepository,
            IBookNarratorLinkRepository bookNarratorLinkRepository,
            INarratorMetadataRepository narratorMetadataRepository,
            INarratorRepository narratorRepository,
            Logger logger)
        {
            _editionNarratorLinkRepository = editionNarratorLinkRepository;
            _bookNarratorLinkRepository = bookNarratorLinkRepository;
            _narratorMetadataRepository = narratorMetadataRepository;
            _narratorRepository = narratorRepository;
            _logger = logger;
        }

        public void UpsertEditionNarratorLinks(IReadOnlyCollection<Edition> editions)
        {
            if (editions == null || editions.Count == 0)
            {
                return;
            }

            var timer = Stopwatch.StartNew();
            var editionCreditRows = editions
                .Where(e => e != null && e.Id > 0)
                .SelectMany(e => (e.NarratorCredits ?? new List<NarratorCredit>())
                    .Where(c => !string.IsNullOrWhiteSpace(c?.Name))
                    .Select(c => new EditionNarratorCreditRow
                    {
                        EditionId = e.Id,
                        Credit = c,
                        GoodreadsId = ProviderIdHelper.Normalize(c.GoodreadsNarratorId, "gr"),
                        HardcoverId = ProviderIdHelper.Normalize(c.HardcoverNarratorId, "hc"),
                        CleanName = c.Name.CleanNarratorName()
                    }))
                .ToList();

            if (!editionCreditRows.Any())
            {
                return;
            }

            var editionIds = editionCreditRows.Select(r => r.EditionId).Distinct().ToList();
            var existingLinks = _editionNarratorLinkRepository.GetByEditionIds(editionIds);

            // Normalize remote IDs and build key sets for a single round-trip preload.
            var goodreadsIds = editionCreditRows
                .Select(r => r.GoodreadsId)
                .Where(id => !id.IsNullOrWhiteSpace())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var hardcoverIds = editionCreditRows
                .Select(r => r.HardcoverId)
                .Where(id => !id.IsNullOrWhiteSpace())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var cleanNames = editionCreditRows
                .Select(r => r.CleanName)
                .Where(name => !name.IsNullOrWhiteSpace())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var existingMetadata = _narratorMetadataRepository.FindByProviderIds(goodreadsIds, hardcoverIds);
            var existingNarratorsByMetadataId = _narratorRepository.GetNarratorsByMetadataId(existingMetadata.Select(m => m.Id).ToList());
            var existingNarratorsByCleanName = _narratorRepository.FindByCleanNames(cleanNames);

            var existingNarrators = existingLinks.Select(l => l.Narrator?.Value)
                .Concat(existingNarratorsByCleanName).Concat(existingNarratorsByMetadataId)
                .Where(n => n != null)
                .DistinctBy(n => n.Id)
                .ToList();

            existingMetadata = existingLinks.Select(l => l.Narrator?.Value?.Metadata?.Value)
                .Where(m => m != null).Concat(existingMetadata).DistinctBy(m => m.Id).ToList();

            // The narrator queries join for filtering but leave metadata lazy; hydrate once for the batch.
            var metadataById = existingMetadata.ToDictionary(m => m.Id);
            var missingMetadataIds = existingNarrators.Select(n => n.NarratorMetadataId)
                .Where(id => !metadataById.ContainsKey(id)).Distinct().ToList();
            if (missingMetadataIds.Any())
            {
                existingMetadata.AddRange(_narratorMetadataRepository.Get(missingMetadataIds));
                metadataById = existingMetadata.ToDictionary(m => m.Id);
            }

            foreach (var narrator in existingNarrators)
            {
                narrator.Metadata = metadataById[narrator.NarratorMetadataId];
            }

            var narratorsByMetadataId = existingNarrators
                .Where(n => n.NarratorMetadataId > 0)
                .GroupBy(n => n.NarratorMetadataId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var narratorsByCleanName = existingNarrators
                .Where(n => !n.CleanName.IsNullOrWhiteSpace())
                .GroupBy(n => n.CleanName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToHashSet<Narrator>(ReferenceEqualityComparer.Instance), StringComparer.OrdinalIgnoreCase);

            // Inspect the whole batch before a name-only match can promote an incomplete identity.
            var ambiguousNames = editionCreditRows.GroupBy(r => r.CleanName, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Select(r => r.GoodreadsId).Where(id => !id.IsNullOrWhiteSpace()).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any() ||
                            g.Select(r => r.HardcoverId).Where(id => !id.IsNullOrWhiteSpace()).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any())
                .Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var conflictingGoodreadsIds = editionCreditRows.Where(r => !r.GoodreadsId.IsNullOrWhiteSpace() && !r.HardcoverId.IsNullOrWhiteSpace())
                .GroupBy(r => r.GoodreadsId, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Select(r => r.HardcoverId).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any())
                .Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var conflictingHardcoverIds = editionCreditRows.Where(r => !r.GoodreadsId.IsNullOrWhiteSpace() && !r.HardcoverId.IsNullOrWhiteSpace())
                .GroupBy(r => r.HardcoverId, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Select(r => r.GoodreadsId).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any())
                .Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Provider-ID lookups are the highest confidence keys.
            var narratorsByGoodreadsId = new Dictionary<string, HashSet<Narrator>>(StringComparer.OrdinalIgnoreCase);
            var narratorsByHardcoverId = new Dictionary<string, HashSet<Narrator>>(StringComparer.OrdinalIgnoreCase);

            // Plan narrator rows for any metadata rows that exist but have no Narrators instance yet.
            var plannedNarrators = new List<Narrator>();

            foreach (var metadata in existingMetadata.Where(m => m != null && m.Id > 0))
            {
                if (!narratorsByMetadataId.TryGetValue(metadata.Id, out var narrators))
                {
                    var narrator = BuildNarratorShell(metadata);
                    plannedNarrators.Add(narrator);
                    narrators = new List<Narrator> { narrator };
                    narratorsByMetadataId[metadata.Id] = narrators;
                }

                foreach (var narrator in narrators)
                {
                    AddCandidate(narratorsByGoodreadsId, ProviderIdHelper.Normalize(metadata.GoodreadsNarratorId, "gr"), narrator);
                    AddCandidate(narratorsByHardcoverId, ProviderIdHelper.Normalize(metadata.HardcoverNarratorId, "hc"), narrator);
                    AddCandidate(narratorsByCleanName, narrator.CleanName, narrator);
                }
            }

            var metadataToUpdate = new Dictionary<int, NarratorMetadata>();

            // Explicit cross-provider credits establish their identity before weaker credits use it.
            foreach (var row in editionCreditRows.OrderByDescending(r =>
                         (r.GoodreadsId.IsNullOrWhiteSpace() ? 0 : 1) + (r.HardcoverId.IsNullOrWhiteSpace() ? 0 : 1)))
            {
                var credit = row.Credit;
                var normalizedGoodreadsId = row.GoodreadsId;
                var normalizedHardcoverId = row.HardcoverId;
                var cleanName = row.CleanName;
                var providerCandidates = new HashSet<Narrator>(ReferenceEqualityComparer.Instance);

                if (!normalizedGoodreadsId.IsNullOrWhiteSpace() &&
                    narratorsByGoodreadsId.TryGetValue(normalizedGoodreadsId, out var narratorsByGr))
                {
                    providerCandidates.UnionWith(narratorsByGr);
                }

                if (!normalizedHardcoverId.IsNullOrWhiteSpace() &&
                    narratorsByHardcoverId.TryGetValue(normalizedHardcoverId, out var narratorsByHc))
                {
                    providerCandidates.UnionWith(narratorsByHc);
                }

                var narrator = providerCandidates.Count == 1 ? providerCandidates.Single() : null;
                if (providerCandidates.Count > 1 || HasProviderIdConflict(narrator?.Metadata?.Value, normalizedGoodreadsId, normalizedHardcoverId) ||
                    (conflictingGoodreadsIds.Contains(normalizedGoodreadsId) && narrator?.Metadata?.Value?.HardcoverNarratorId.IsNullOrWhiteSpace() != false) ||
                    (conflictingHardcoverIds.Contains(normalizedHardcoverId) && narrator?.Metadata?.Value?.GoodreadsNarratorId.IsNullOrWhiteSpace() != false))
                {
                    _logger.Warn("[NARRATOR-IDS] Unresolved conflicting provider evidence for narrator credit '{0}'", credit.Name);
                    continue;
                }

                narratorsByCleanName.TryGetValue(cleanName, out var nameCandidates);
                if (narrator == null && !ambiguousNames.Contains(cleanName) && nameCandidates?.Count == 1 &&
                    !HasProviderIdConflict(nameCandidates.Single().Metadata?.Value, normalizedGoodreadsId, normalizedHardcoverId))
                {
                    narrator = nameCandidates.Single();
                }

                if (narrator == null && normalizedGoodreadsId.IsNullOrWhiteSpace() && normalizedHardcoverId.IsNullOrWhiteSpace() &&
                    (nameCandidates?.Count > 1 || ambiguousNames.Contains(cleanName)))
                {
                    _logger.Warn("[NARRATOR-IDS] Unresolved ambiguous name-only narrator credit '{0}'", credit.Name);
                    continue;
                }

                if (narrator == null)
                {
                    var metadata = new NarratorMetadata
                    {
                        Name = credit.Name.Trim(),
                        TitleSlug = BuildTitleSlug(credit.Name),
                        GoodreadsNarratorId = normalizedGoodreadsId,
                        HardcoverNarratorId = normalizedHardcoverId
                    };

                    narrator = BuildNarratorShell(metadata, cleanName);
                    plannedNarrators.Add(narrator);
                }
                else
                {
                    PromoteProviderIds(narrator.Metadata?.Value, normalizedGoodreadsId, normalizedHardcoverId, metadataToUpdate);
                }

                AddCandidate(narratorsByGoodreadsId, normalizedGoodreadsId, narrator);
                AddCandidate(narratorsByHardcoverId, normalizedHardcoverId, narrator);
                AddCandidate(narratorsByCleanName, cleanName, narrator);
                row.Narrator = narrator;
            }

            // Persist any new NarratorMetadata rows first (they provide the FK for Narrators).
            var newMetadata = plannedNarrators
                .Select(n => n.Metadata.Value)
                .Where(m => m != null && m.Id == 0)
                .ToList();

            if (newMetadata.Any())
            {
                _narratorMetadataRepository.InsertMany(newMetadata);
            }

            // Persist any narrator rows that don't exist yet (both "missing instance" and "brand new").
            var narratorsToInsert = plannedNarrators
                .Select(n =>
                {
                    if (n.NarratorMetadataId <= 0 && n.Metadata.Value?.Id > 0)
                    {
                        n.NarratorMetadataId = n.Metadata.Value.Id;
                    }

                    return n;
                })
                .Where(n => n != null && n.Id == 0 && n.NarratorMetadataId > 0)
                .DistinctBy(n => new { n.NarratorMetadataId, n.CleanName })
                .ToList();

            if (narratorsToInsert.Any())
            {
                _narratorRepository.InsertMany(narratorsToInsert);
            }

            if (metadataToUpdate.Any())
            {
                _narratorMetadataRepository.UpdateMany(metadataToUpdate.Values.ToList());
            }

            var currentPrimaries = new Dictionary<int, int?>();
            foreach (var group in editionCreditRows.GroupBy(row => row.EditionId))
            {
                var primaryCredits = group.Where(row => row.Credit.IsPrimary).ToList();
                if (!primaryCredits.Any())
                {
                    primaryCredits = group.Where(row => row.Credit.Order == 0).ToList();
                }

                if (primaryCredits.Any())
                {
                    var firstOrder = primaryCredits.Min(row => row.Credit.Order);
                    var leadIds = primaryCredits.Where(row => row.Credit.Order == firstOrder)
                        .Select(row => row.Narrator?.Id ?? 0).Distinct().ToList();
                    currentPrimaries[group.Key] = leadIds.Count == 1 && leadIds[0] > 0 ? leadIds[0] : (int?)null;
                }
            }

            // Build link rows once all narrator IDs are known.
            var existingPrimaryPairs = existingLinks.Where(link => link.IsPrimary)
                .Select(link => (link.EditionId, link.NarratorId)).ToHashSet();
            var editionLinks = editionCreditRows
                .Where(r => r.Narrator?.Id > 0)
                .Select(r => new EditionNarratorLink
                {
                    EditionId = r.EditionId,
                    NarratorId = r.Narrator.Id,
                    IsPrimary = currentPrimaries.TryGetValue(r.EditionId, out var primaryId) && primaryId.HasValue
                        ? r.Narrator.Id == primaryId.Value
                        : existingPrimaryPairs.Contains((r.EditionId, r.Narrator.Id)),
                    Role = r.Credit.Role.IsNullOrWhiteSpace() ? "Narrator" : r.Credit.Role
                })
                .GroupBy(l => new { l.EditionId, l.NarratorId })
                .Select(g =>
                {
                    var sample = g.First();
                    sample.IsPrimary = g.Any(x => x.IsPrimary);
                    return sample;
                })
                .ToList();

            var creditsByEdition = editionCreditRows.ToLookup(r => r.EditionId);
            var resolvedPairs = editionLinks.Select(l => (l.EditionId, l.NarratorId)).ToHashSet();
            var deletedLinkIds = existingLinks.Where(link =>
            {
                var narrator = link.Narrator?.Value;
                var metadata = narrator?.Metadata?.Value;
                var credits = creditsByEdition[link.EditionId];

                // An unresolved credit can retain an existing relation without resolving or changing its identity.
                return narrator == null || resolvedPairs.Contains((link.EditionId, link.NarratorId)) ||
                       !credits.Any(r => r.Narrator == null &&
                           (string.Equals(r.CleanName, narrator.CleanName, StringComparison.OrdinalIgnoreCase) || SharesProviderId(metadata, r)));
            }).Select(l => l.Id).ToList();

            var deletedIds = deletedLinkIds.ToHashSet();
            var demotedPrimaryLinkIds = existingLinks.Where(link => !deletedIds.Contains(link.Id) && link.IsPrimary &&
                currentPrimaries.TryGetValue(link.EditionId, out var primaryId) &&
                primaryId.HasValue && link.NarratorId != primaryId.Value)
                .Select(link => link.Id).ToList();

            _editionNarratorLinkRepository.ApplyChanges(deletedLinkIds, editionLinks, demotedPrimaryLinkIds);
            _logger.Debug("Narrator link upsert: editions={0}, credits={1}, resolved={2}, unresolved={3}, retained={4}, inserted={5}, deleted={6}, primaryUpdates={7}, elapsedMs={8:F3}",
                editionIds.Count, editionCreditRows.Count, editionCreditRows.Count(row => row.Narrator != null),
                editionCreditRows.Count(row => row.Narrator == null), existingLinks.Count - deletedLinkIds.Count,
                editionLinks.Count, deletedLinkIds.Count, demotedPrimaryLinkIds.Count, timer.Elapsed.TotalMilliseconds);
        }

        public void RebuildBookNarratorLinks(IEnumerable<int> bookIds)
        {
            if (bookIds == null)
            {
                return;
            }

            var ids = bookIds.Where(id => id > 0).Distinct().ToList();
            if (!ids.Any())
            {
                return;
            }

            var rows = _editionNarratorLinkRepository.GetByBookIds(ids);

            var links = new List<BookNarratorLink>();

            foreach (var group in rows.GroupBy(r => r.BookId))
            {
                var bookId = group.Key;
                var narratorIds = group.Select(r => r.NarratorId).Distinct().ToList();

                if (!narratorIds.Any())
                {
                    continue;
                }

                var primaryNarratorId = group
                    .Where(r => r.Monitored && r.IsPrimary)
                    .Select(r => r.NarratorId)
                    .OrderBy(id => id)
                    .FirstOrDefault();

                if (primaryNarratorId <= 0)
                {
                    primaryNarratorId = group
                        .Where(r => r.IsPrimary)
                        .Select(r => r.NarratorId)
                        .OrderBy(id => id)
                        .FirstOrDefault();
                }

                if (primaryNarratorId <= 0)
                {
                    primaryNarratorId = narratorIds.OrderBy(id => id).First();
                }

                links.AddRange(narratorIds.Select(narratorId => new BookNarratorLink
                {
                    BookId = bookId,
                    NarratorId = narratorId,
                    IsPrimary = narratorId == primaryNarratorId,
                    Role = "Narrator"
                }));
            }

            _bookNarratorLinkRepository.ReplaceByBookIds(ids, links);
        }

        private static Narrator BuildNarratorShell(NarratorMetadata metadata, string cleanName = null)
        {
            var name = metadata?.Name ?? string.Empty;

            return new Narrator
            {
                NarratorMetadataId = metadata?.Id ?? 0,
                CleanName = cleanName ?? name.CleanNarratorName(),
                Monitored = true,
                MonitorNewItems = NewItemMonitorTypes.All,
                Added = DateTime.UtcNow,
                Tags = new HashSet<int>(),
                AddOptions = null,
                Metadata = metadata
            };
        }

        private static void AddCandidate(Dictionary<string, HashSet<Narrator>> index, string key, Narrator narrator)
        {
            if (key.IsNullOrWhiteSpace())
            {
                return;
            }

            if (!index.TryGetValue(key, out var candidates))
            {
                candidates = new HashSet<Narrator>(ReferenceEqualityComparer.Instance);
                index[key] = candidates;
            }

            candidates.Add(narrator);
        }

        private static bool HasProviderIdConflict(NarratorMetadata metadata, string goodreadsId, string hardcoverId)
        {
            if (metadata == null)
            {
                return false;
            }

            if (!goodreadsId.IsNullOrWhiteSpace() &&
                !metadata.GoodreadsNarratorId.IsNullOrWhiteSpace() &&
                !string.Equals(metadata.GoodreadsNarratorId, goodreadsId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!hardcoverId.IsNullOrWhiteSpace() &&
                !metadata.HardcoverNarratorId.IsNullOrWhiteSpace() &&
                !string.Equals(metadata.HardcoverNarratorId, hardcoverId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        private static bool SharesProviderId(NarratorMetadata metadata, EditionNarratorCreditRow credit)
        {
            return (!credit.GoodreadsId.IsNullOrWhiteSpace() &&
                    string.Equals(metadata?.GoodreadsNarratorId, credit.GoodreadsId, StringComparison.OrdinalIgnoreCase)) ||
                   (!credit.HardcoverId.IsNullOrWhiteSpace() &&
                    string.Equals(metadata?.HardcoverNarratorId, credit.HardcoverId, StringComparison.OrdinalIgnoreCase));
        }

        private static void PromoteProviderIds(NarratorMetadata metadata, string goodreadsId, string hardcoverId, Dictionary<int, NarratorMetadata> updates)
        {
            if (metadata == null)
            {
                return;
            }

            var changed = false;

            if (!goodreadsId.IsNullOrWhiteSpace() && metadata.GoodreadsNarratorId.IsNullOrWhiteSpace())
            {
                metadata.GoodreadsNarratorId = goodreadsId;
                changed = true;
            }

            if (!hardcoverId.IsNullOrWhiteSpace() && metadata.HardcoverNarratorId.IsNullOrWhiteSpace())
            {
                metadata.HardcoverNarratorId = hardcoverId;
                changed = true;
            }

            if (changed && metadata.Id > 0)
            {
                updates[metadata.Id] = metadata;
            }
        }

        private static string BuildTitleSlug(string narratorName)
        {
            if (narratorName.IsNullOrWhiteSpace())
            {
                return null;
            }

            return narratorName
                .Trim()
                .ToLowerInvariant()
                .Replace(" ", "-")
                .Replace("'", "")
                .Replace("\"", "")
                .Replace(":", "")
                .Replace("(", "")
                .Replace(")", "")
                .Replace("[", "")
                .Replace("]", "")
                .Replace(",", "")
                .Replace(".", "")
                .Replace("!", "")
                .Replace("?", "");
        }
    }
}
