using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser;

namespace NzbDrone.Core.Books
{
    public class NarratorService : INarratorService
    {
        private readonly INarratorRepository _narratorRepository;
        private readonly IMainDatabase _database;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;
        private readonly ICached<List<Narrator>> _cache;

        public NarratorService(INarratorRepository narratorRepository,
                             IMainDatabase database,
                             IEventAggregator eventAggregator,
                             ICacheManager cacheManager,
                             Logger logger)
        {
            _narratorRepository = narratorRepository;
            _database = database;
            _eventAggregator = eventAggregator;
            _logger = logger;
            _cache = cacheManager.GetRollingCache<List<Narrator>>(GetType(), "narrators", TimeSpan.FromSeconds(30));
        }

        public Narrator GetNarrator(int narratorId)
        {
            return _narratorRepository.Get(narratorId);
        }

        public Narrator GetNarratorByMetadataId(int narratorMetadataId)
        {
            return _narratorRepository.GetNarratorByMetadataId(narratorMetadataId);
        }

        public List<Narrator> GetNarrators(IEnumerable<int> narratorIds)
        {
            return _narratorRepository.Get(narratorIds).ToList();
        }

        public Narrator AddNarrator(Narrator newNarrator)
        {
            _cache.Clear();

            // LocalNarratorId generation removed - using database IDs directly

            _narratorRepository.Insert(newNarrator);
            _eventAggregator.PublishEvent(new NarratorAddedEvent(newNarrator));
            return newNarrator;
        }

        public List<Narrator> AddNarrators(List<Narrator> newNarrators)
        {
            _cache.Clear();

            // LocalNarratorId generation removed - using database IDs directly

            _narratorRepository.InsertMany(newNarrators);
            newNarrators.ForEach(n => _eventAggregator.PublishEvent(new NarratorAddedEvent(n)));
            return newNarrators;
        }

        public Narrator FindById(string foreignNarratorId)
        {
            return _narratorRepository.FindById(foreignNarratorId);
        }

        public Narrator FindByName(string name)
        {
            return _narratorRepository.FindByName(name.CleanNarratorName());
        }

        public Narrator FindByNameInexact(string name)
        {
            var cleanName = name.CleanNarratorName();

            var narrator = _narratorRepository.FindByName(cleanName);

            if (narrator == null)
            {
                var candidates = GetCandidates(name);
                narrator = candidates.FirstOrDefault();
            }

            return narrator;
        }

        public Narrator FindByNarratorTitleSlug(string narratorTitleSlug)
        {
            return _narratorRepository.FindByNarratorTitleSlug(narratorTitleSlug);
        }

        public List<Narrator> GetCandidates(string name)
        {
            var normalizedName = name.CleanNarratorName();
            var narrators = GetAllNarrators();

            // Use deterministic matching instead of fuzzy distance
            var candidates = new List<Narrator>();

            foreach (var narrator in narrators)
            {
                // Exact match (case insensitive)
                if (string.Equals(narrator.CleanName, normalizedName, StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Insert(0, narrator); // Exact matches go first
                    continue;
                }

                // Check if one contains the other
                if (narrator.CleanName.IndexOf(normalizedName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    normalizedName.IndexOf(narrator.CleanName, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    candidates.Add(narrator);
                }
            }

            return candidates;
        }

        public List<Narrator> GetReportCandidates(string reportName)
        {
            var normalizedName = reportName.CleanNarratorName();
            var narrators = GetAllNarrators();

            return narrators.Where(n =>
                {
                    var distance = normalizedName.LevenshteinDistance(n.CleanName);
                    var score = 1.0 - ((double)distance / Math.Max(normalizedName.Length, n.CleanName.Length));
                    return score >= 0.4;
                })
                .OrderByDescending(n =>
                {
                    var distance = normalizedName.LevenshteinDistance(n.CleanName);
                    return 1.0 - ((double)distance / Math.Max(normalizedName.Length, n.CleanName.Length));
                })
                .ToList();
        }

        public void DeleteNarrator(int narratorId)
        {
            _cache.Clear();
            var narrator = _narratorRepository.Get(narratorId);
            CleanupNarratorReferences(narratorId);
            _narratorRepository.Delete(narratorId);
            _eventAggregator.PublishEvent(new NarratorDeletedEvent(narrator));
        }

        private void CleanupNarratorReferences(int narratorId)
        {
            using var mapper = _database.OpenConnection();

            mapper.Execute(@"DELETE FROM ""BookNarratorLink"" WHERE ""NarratorId"" = @NarratorId", new { NarratorId = narratorId });
            mapper.Execute(@"DELETE FROM ""EditionNarratorLink"" WHERE ""NarratorId"" = @NarratorId", new { NarratorId = narratorId });
            mapper.Execute(@"UPDATE ""Books"" SET ""NarratorId"" = NULL WHERE ""NarratorId"" = @NarratorId", new { NarratorId = narratorId });
            mapper.Execute(@"UPDATE ""Books"" SET ""WantedNarratorId"" = NULL WHERE ""WantedNarratorId"" = @NarratorId", new { NarratorId = narratorId });
            mapper.Execute(@"UPDATE ""Series"" SET ""PreferredNarratorId"" = NULL WHERE ""PreferredNarratorId"" = @NarratorId", new { NarratorId = narratorId });
        }

        public List<Narrator> GetAllNarrators()
        {
            return _cache.Get("all", () => _narratorRepository.All().ToList(), TimeSpan.FromSeconds(30));
        }

        public Dictionary<int, List<int>> GetAllNarratorTags()
        {
            return _narratorRepository.AllNarratorTags();
        }

        public List<Narrator> AllForTag(int tagId)
        {
            return GetAllNarrators().Where(n => n.Tags.Contains(tagId)).ToList();
        }

        public Narrator UpdateNarrator(Narrator narrator)
        {
            _cache.Clear();
            var storedNarrator = GetNarrator(narrator.Id);
            var updatedNarrator = _narratorRepository.Update(narrator);
            _eventAggregator.PublishEvent(new NarratorUpdatedEvent(updatedNarrator, storedNarrator));
            return updatedNarrator;
        }

        public List<Narrator> UpdateNarrators(List<Narrator> narrators)
        {
            _cache.Clear();
            _narratorRepository.UpdateMany(narrators);
            narrators.ForEach(n =>
            {
                _logger.Trace("Updating narrator {0}", n.Name);
                _eventAggregator.PublishEvent(new NarratorUpdatedEvent(n, n));
            });

            return narrators;
        }
    }
}
