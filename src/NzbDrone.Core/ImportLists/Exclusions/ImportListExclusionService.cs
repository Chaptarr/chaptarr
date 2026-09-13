using System;
using System.Collections.Generic;
using System.Linq;
using FluentValidation;
using FluentValidation.Results;
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
        void ValidateAuthorExclusions(IEnumerable<Author> authors);
        void AddAuthorExclusions(IEnumerable<Author> authors);
        void Delete(int id);
        void Delete(List<int> ids);
        void Delete(string foreignId);
        ImportListExclusion Get(int id);
        ImportListExclusion FindByForeignId(string foreignId);
        List<ImportListExclusion> FindByForeignId(List<string> foreignIds);
        ImportListExclusion Update(ImportListExclusion importListExclusion);
    }

    public class ImportListExclusionService : IImportListExclusionService,
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

        public void ValidateAuthorExclusions(IEnumerable<Author> authors)
        {
            if (authors?.Any(author => author != null && !AuthorIdentity.GetProviderIdentityTokenList(author).Any()) == true)
            {
                throw new ValidationException(new[]
                {
                    new ValidationFailure("ProviderId", "Cannot create import-list exclusions for authors without provider IDs.")
                    {
                        ErrorCode = "AUTHOR_PROVIDER_ID_REQUIRED"
                    }
                });
            }
        }

        public void AddAuthorExclusions(IEnumerable<Author> authors)
        {
            var selectedAuthors = (authors ?? Enumerable.Empty<Author>())
                .Where(author => author != null)
                .ToList();
            ValidateAuthorExclusions(selectedAuthors);
            var providerIdsByAuthor = selectedAuthors
                .Select(author => new { Author = author, ProviderIds = AuthorIdentity.GetProviderIdentityTokenList(author) })
                .ToList();

            var existingIds = _repo.All()
                .Select(exclusion => exclusion.ForeignId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var additions = providerIdsByAuthor
                .SelectMany(entry => entry.ProviderIds.Select(providerId => new
                {
                    entry.Author,
                    ProviderId = providerId
                }))
                .Where(entry => existingIds.Add(entry.ProviderId))
                .Select(entry => new ImportListExclusion
                {
                    ForeignId = entry.ProviderId,
                    Name = entry.Author.Name
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
