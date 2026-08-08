using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.ImportLists.Exclusions
{
    public interface IImportListExclusionService
    {
        ImportListExclusion Add(ImportListExclusion importListExclusion);
        List<ImportListExclusion> All();
        void Delete(int id);
        void Delete(List<int> ids);
        void Delete(string foreignId);
        ImportListExclusion Get(int id);
        ImportListExclusion FindByForeignId(string foreignId);
        List<ImportListExclusion> FindByForeignId(List<string> foreignIds);
        ImportListExclusion Update(ImportListExclusion importListExclusion);
    }

    public class ImportListExclusionService : IImportListExclusionService,
                                              IHandleAsync<AuthorDeletedEvent>,
                                              IHandleAsync<BookDeletedEvent>
    {
        private readonly IImportListExclusionRepository _repo;
        private readonly Logger _logger;

        public ImportListExclusionService(IImportListExclusionRepository repo, Logger logger)
        {
            _repo = repo;
            _logger = logger;
        }

        public ImportListExclusion Add(ImportListExclusion importListExclusion)
        {
            return _repo.Insert(importListExclusion);
        }

        public ImportListExclusion Update(ImportListExclusion importListExclusion)
        {
            return _repo.Update(importListExclusion);
        }

        public void Delete(int id)
        {
            _repo.Delete(id);
        }

        public void Delete(List<int> ids)
        {
            _repo.DeleteMany(ids);
        }

        public void Delete(string foreignId)
        {
            var exclusion = FindByForeignId(foreignId);
            if (exclusion != null)
            {
                Delete(exclusion.Id);
            }
        }

        public ImportListExclusion Get(int id)
        {
            return _repo.Get(id);
        }

        public ImportListExclusion FindByForeignId(string foreignId)
        {
            return _repo.FindByForeignId(foreignId);
        }

        public List<ImportListExclusion> FindByForeignId(List<string> foreignIds)
        {
            return _repo.FindByForeignId(foreignIds);
        }

        public List<ImportListExclusion> All()
        {
            return _repo.All().ToList();
        }

        public void HandleAsync(AuthorDeletedEvent message)
        {
            if (!message.AddImportListExclusion)
            {
                return;
            }

            var author = message.Author;
            var providerIds = AuthorIdentity.GetProviderIdentityTokenList(author);
            if (!providerIds.Any() && author?.Id > 0)
            {
                providerIds.Add(author.Id.ToString());
            }

            if (!providerIds.Any())
            {
                _logger.Warn("Cannot create import exclusion for author without any provider ID: {0}", author?.Name ?? "Unknown");
                return;
            }

            var existingIds = _repo.All()
                .Select(e => e.ForeignId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var additions = providerIds
                .Where(providerId => !string.IsNullOrWhiteSpace(providerId) && existingIds.Add(providerId))
                .Select(providerId => new ImportListExclusion
                {
                    ForeignId = providerId,
                    Name = author.Name
                })
                .ToList();

            if (additions.Any())
            {
                _repo.InsertMany(additions);
            }
        }

        public void HandleAsync(BookDeletedEvent message)
        {
            if (!message.AddImportListExclusion)
            {
                return;
            }

            var deletedBooks = (message.DeletedBooks?.Any() == true
                ? message.DeletedBooks
                : new[] { message.Book })
                .Where(book => book != null)
                .ToList();

            var exclusionScope = message.ApplyToBothFormats ? (BookMediaType?)null : message.Book?.MediaType;
            var allExclusions = _repo.All().ToList();
            var exclusionsToDelete = new List<int>();
            var exclusionsToInsert = new List<ImportListExclusion>();
            var candidateRows = deletedBooks
                .SelectMany(book => ImportListExclusionBookMatcher.GetCanonicalProviderIds(book)
                    .Select(providerId => new
                    {
                        ProviderId = providerId,
                        Name = $"{book.Author?.Name ?? message.Book?.Author?.Name ?? "Unknown"} - {book.Title}",
                        MediaType = exclusionScope ?? book.MediaType
                    }))
                .Distinct()
                .ToList();

            if (!candidateRows.Any())
            {
                _logger.Warn("Cannot create import exclusion for book without any provider ID: {0}", message.Book.Title);
                return;
            }

            foreach (var row in candidateRows)
            {
                var existing = allExclusions
                    .Where(e => string.Equals(e.ForeignId, row.ProviderId, System.StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (message.ApplyToBothFormats)
                {
                    foreach (var exclusion in existing.Where(e => e.MediaType.HasValue))
                    {
                        exclusionsToDelete.Add(exclusion.Id);
                        allExclusions.Remove(exclusion);
                    }

                    if (existing.Any(e => !e.MediaType.HasValue) ||
                        exclusionsToInsert.Any(e => string.Equals(e.ForeignId, row.ProviderId, StringComparison.OrdinalIgnoreCase) && !e.MediaType.HasValue))
                    {
                        continue;
                    }
                }
                else
                {
                    if (existing.Any(e => !e.MediaType.HasValue || e.MediaType == row.MediaType) ||
                        exclusionsToInsert.Any(e => string.Equals(e.ForeignId, row.ProviderId, StringComparison.OrdinalIgnoreCase) && (!e.MediaType.HasValue || e.MediaType == row.MediaType)))
                    {
                        continue;
                    }
                }

                var importExclusion = new ImportListExclusion
                {
                    ForeignId = row.ProviderId,
                    Name = row.Name,
                    MediaType = message.ApplyToBothFormats ? null : row.MediaType
                };

                exclusionsToInsert.Add(importExclusion);
                allExclusions.Add(importExclusion);
            }

            if (exclusionsToDelete.Any())
            {
                _repo.DeleteMany(exclusionsToDelete.Distinct().ToList());
            }

            if (exclusionsToInsert.Any())
            {
                _repo.InsertMany(exclusionsToInsert);
            }
        }
    }
}
