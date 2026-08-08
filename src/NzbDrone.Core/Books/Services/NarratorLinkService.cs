using System;
using System.Collections.Generic;
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
        private sealed class PlannedNarrator
        {
            public NarratorMetadata Metadata { get; init; }
            public Narrator Narrator { get; init; }
        }

        private sealed class EditionNarratorCreditRow
        {
            public int EditionId { get; init; }
            public int BookId { get; init; }
            public NarratorCredit Credit { get; init; }
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

            var editionCreditRows = editions
                .Where(e => e != null && e.Id > 0)
                .SelectMany(e => (e.NarratorCredits ?? new List<NarratorCredit>())
                    .Where(c => !string.IsNullOrWhiteSpace(c?.Name))
                    .Select(c => new EditionNarratorCreditRow
                    {
                        EditionId = e.Id,
                        BookId = e.BookId,
                        Credit = c
                    }))
                .ToList();

            if (!editionCreditRows.Any())
            {
                return;
            }

            // Normalize remote IDs and build key sets for a single round-trip preload.
            var goodreadsIds = editionCreditRows
                .Select(r => ProviderIdHelper.Normalize(r.Credit.GoodreadsNarratorId, "gr"))
                .Where(id => !id.IsNullOrWhiteSpace())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var hardcoverIds = editionCreditRows
                .Select(r => ProviderIdHelper.Normalize(r.Credit.HardcoverNarratorId, "hc"))
                .Where(id => !id.IsNullOrWhiteSpace())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var cleanNames = editionCreditRows
                .Select(r => r.Credit.Name.CleanNarratorName())
                .Where(name => !name.IsNullOrWhiteSpace())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var existingMetadata = _narratorMetadataRepository.FindByProviderIds(goodreadsIds, hardcoverIds);
            var existingNarratorsByMetadataId = _narratorRepository.GetNarratorsByMetadataId(existingMetadata.Select(m => m.Id).ToList());
            var existingNarratorsByCleanName = _narratorRepository.FindByCleanNames(cleanNames);

            var narratorByMetadataId = existingNarratorsByMetadataId
                .Where(n => n != null && n.NarratorMetadataId > 0)
                .GroupBy(n => n.NarratorMetadataId)
                .ToDictionary(g => g.Key, g => g.OrderBy(n => n.Id).First());

            var narratorByCleanName = existingNarratorsByCleanName
                .Where(n => n != null && !n.CleanName.IsNullOrWhiteSpace())
                .GroupBy(n => n.CleanName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.OrderBy(n => n.Id).First(), StringComparer.OrdinalIgnoreCase);

            // Provider-ID lookups are the highest confidence keys.
            var narratorByGoodreadsId = new Dictionary<string, Narrator>(StringComparer.OrdinalIgnoreCase);
            var narratorByHardcoverId = new Dictionary<string, Narrator>(StringComparer.OrdinalIgnoreCase);

            // Plan narrator rows for any metadata rows that exist but have no Narrators instance yet.
            var plannedNarrators = new List<PlannedNarrator>();

            foreach (var metadata in existingMetadata.Where(m => m != null && m.Id > 0))
            {
                if (!narratorByMetadataId.TryGetValue(metadata.Id, out var narrator))
                {
                    narrator = BuildNarratorShell(metadata);
                    plannedNarrators.Add(new PlannedNarrator { Metadata = metadata, Narrator = narrator });
                    narratorByMetadataId[metadata.Id] = narrator;
                }

                var gr = ProviderIdHelper.Normalize(metadata.GoodreadsNarratorId, "gr");
                if (!gr.IsNullOrWhiteSpace())
                {
                    narratorByGoodreadsId[gr] = narrator;
                }

                var hc = ProviderIdHelper.Normalize(metadata.HardcoverNarratorId, "hc");
                if (!hc.IsNullOrWhiteSpace())
                {
                    narratorByHardcoverId[hc] = narrator;
                }

                if (!narrator.CleanName.IsNullOrWhiteSpace())
                {
                    narratorByCleanName[narrator.CleanName] = narrator;
                }
            }

            var metadataToUpdate = new Dictionary<int, NarratorMetadata>();

            foreach (var row in editionCreditRows)
            {
                var credit = row.Credit;
                if (credit?.Name.IsNullOrWhiteSpace() != false)
                {
                    continue;
                }

                var normalizedGoodreadsId = ProviderIdHelper.Normalize(credit.GoodreadsNarratorId, "gr");
                var normalizedHardcoverId = ProviderIdHelper.Normalize(credit.HardcoverNarratorId, "hc");
                var cleanName = credit.Name.CleanNarratorName();

                Narrator narrator = null;

                if (!normalizedGoodreadsId.IsNullOrWhiteSpace() &&
                    narratorByGoodreadsId.TryGetValue(normalizedGoodreadsId, out var narratorByGr))
                {
                    narrator = narratorByGr;
                }
                else if (!normalizedHardcoverId.IsNullOrWhiteSpace() &&
                         narratorByHardcoverId.TryGetValue(normalizedHardcoverId, out var narratorByHc))
                {
                    narrator = narratorByHc;
                }
                else if (!cleanName.IsNullOrWhiteSpace() &&
                         narratorByCleanName.TryGetValue(cleanName, out var narratorByName))
                {
                    // Only merge by name when it doesn't contradict existing provider IDs.
                    if (!HasProviderIdConflict(narratorByName?.Metadata?.Value, normalizedGoodreadsId, normalizedHardcoverId))
                    {
                        narrator = narratorByName;
                    }
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

                    narrator = new Narrator
                    {
                        NarratorMetadataId = 0,
                        CleanName = cleanName,
                        Monitored = true,
                        MonitorNewItems = NewItemMonitorTypes.All,
                        Added = DateTime.UtcNow,
                        Tags = new HashSet<int>(),
                        AddOptions = null
                    };

                    // Keep the metadata attached in-memory for immediate conflict checks/promotions.
                    narrator.Metadata = metadata;

                    plannedNarrators.Add(new PlannedNarrator { Metadata = metadata, Narrator = narrator });

                    if (!normalizedGoodreadsId.IsNullOrWhiteSpace())
                    {
                        narratorByGoodreadsId[normalizedGoodreadsId] = narrator;
                    }

                    if (!normalizedHardcoverId.IsNullOrWhiteSpace())
                    {
                        narratorByHardcoverId[normalizedHardcoverId] = narrator;
                    }

                    if (!cleanName.IsNullOrWhiteSpace())
                    {
                        narratorByCleanName[cleanName] = narrator;
                    }
                }
                else
                {
                    PromoteProviderIds(narrator.Metadata?.Value, normalizedGoodreadsId, normalizedHardcoverId, metadataToUpdate);
                }

                row.Narrator = narrator;
            }

            // Persist any new NarratorMetadata rows first (they provide the FK for Narrators).
            var newMetadata = plannedNarrators
                .Select(p => p.Metadata)
                .Where(m => m != null && m.Id == 0)
                .ToList();

            if (newMetadata.Any())
            {
                _narratorMetadataRepository.InsertMany(newMetadata);
            }

            // Persist any narrator rows that don't exist yet (both "missing instance" and "brand new").
            var narratorsToInsert = plannedNarrators
                .Select(p =>
                {
                    if (p.Narrator != null && p.Narrator.NarratorMetadataId <= 0 && p.Metadata?.Id > 0)
                    {
                        p.Narrator.NarratorMetadataId = p.Metadata.Id;
                    }

                    return p.Narrator;
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

            // Build link rows once all narrator IDs are known.
            var editionLinks = editionCreditRows
                .Where(r => r.Narrator?.Id > 0)
                .Select(r => new EditionNarratorLink
                {
                    EditionId = r.EditionId,
                    NarratorId = r.Narrator.Id,
                    IsPrimary = r.Credit.IsPrimary || r.Credit.Order == 0,
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

            var editionIds = editionCreditRows.Select(r => r.EditionId).Distinct().ToList();

            _editionNarratorLinkRepository.DeleteByEditionIds(editionIds);

            if (editionLinks.Any())
            {
                _editionNarratorLinkRepository.InsertMany(editionLinks);
            }
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

            _bookNarratorLinkRepository.DeleteByBookIds(ids);

            if (links.Any())
            {
                _bookNarratorLinkRepository.InsertMany(links);
            }
        }

        private static Narrator BuildNarratorShell(NarratorMetadata metadata)
        {
            var name = metadata?.Name ?? string.Empty;

            return new Narrator
            {
                NarratorMetadataId = metadata?.Id ?? 0,
                CleanName = name.CleanNarratorName(),
                Monitored = true,
                MonitorNewItems = NewItemMonitorTypes.All,
                Added = DateTime.UtcNow,
                Tags = new HashSet<int>(),
                AddOptions = null,
                Metadata = metadata
            };
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

        private void PromoteProviderIds(NarratorMetadata metadata, string goodreadsId, string hardcoverId, Dictionary<int, NarratorMetadata> updates)
        {
            if (metadata == null || metadata.Id <= 0)
            {
                return;
            }

            var changed = false;

            if (!goodreadsId.IsNullOrWhiteSpace())
            {
                if (metadata.GoodreadsNarratorId.IsNullOrWhiteSpace())
                {
                    metadata.GoodreadsNarratorId = goodreadsId;
                    changed = true;
                }
                else if (!string.Equals(metadata.GoodreadsNarratorId, goodreadsId, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.Warn("[NARRATOR-IDS] Conflicting GoodreadsNarratorId for '{0}': keeping '{1}', ignoring '{2}'",
                        metadata.Name.NullSafe(),
                        metadata.GoodreadsNarratorId,
                        goodreadsId);
                }
            }

            if (!hardcoverId.IsNullOrWhiteSpace())
            {
                if (metadata.HardcoverNarratorId.IsNullOrWhiteSpace())
                {
                    metadata.HardcoverNarratorId = hardcoverId;
                    changed = true;
                }
                else if (!string.Equals(metadata.HardcoverNarratorId, hardcoverId, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.Warn("[NARRATOR-IDS] Conflicting HardcoverNarratorId for '{0}': keeping '{1}', ignoring '{2}'",
                        metadata.Name.NullSafe(),
                        metadata.HardcoverNarratorId,
                        hardcoverId);
                }
            }

            if (changed)
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
