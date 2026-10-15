using System;
using System.Collections.Generic;
using System.Data;
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
        void AddAuthorExclusions(IEnumerable<Author> authors, BookMediaType? mediaType = null);
        void AddAuthorExclusions(IEnumerable<Author> authors, BookMediaType? mediaType, IDbConnection connection, IDbTransaction transaction);
        void Delete(int id);
        void Delete(List<int> ids);
        void Delete(string foreignId);
        ImportListExclusion Get(int id);
        ImportListExclusion FindByForeignId(string foreignId);
        List<ImportListExclusion> FindByForeignId(List<string> foreignIds);
        List<ImportListExclusion> FindByForeignId(List<string> foreignIds, IDbConnection connection, IDbTransaction transaction);
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

        public List<ImportListExclusion> FindByForeignId(List<string> foreignIds, IDbConnection connection, IDbTransaction transaction) =>
            _repo.FindByForeignId(foreignIds, connection, transaction);

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

        public void AddAuthorExclusions(IEnumerable<Author> authors, BookMediaType? mediaType = null) =>
            AddAuthorExclusions(authors, mediaType, null, null);

        public void AddAuthorExclusions(IEnumerable<Author> authors, BookMediaType? mediaType, IDbConnection connection, IDbTransaction transaction)
        {
            var selectedAuthors = (authors ?? Enumerable.Empty<Author>())
                .Where(author => author != null)
                .ToList();
            ValidateAuthorExclusions(selectedAuthors);
            AddScopedExclusions(selectedAuthors.SelectMany(author => AuthorIdentity.GetProviderIdentityTokenList(author)
                .Select(providerId => new ImportListExclusion
                {
                    ForeignId = providerId,
                    Name = author.Name,
                    MediaType = mediaType
                })), connection, transaction);
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

            var candidates = deletedBooks.SelectMany(book => ImportListExclusionBookMatcher.GetCanonicalProviderIds(book)
                .Select(providerId => new ImportListExclusion
                {
                    ForeignId = providerId,
                    Name = $"{book.Author?.Name ?? message.Book?.Author?.Name ?? "Unknown"} - {book.Title}",
                    MediaType = message.ApplyToBothFormats ? null : message.Book?.MediaType ?? book.MediaType
                })).ToList();

            if (!candidates.Any())
            {
                _logger.Warn("Cannot create import exclusion for book without any provider ID: {0}", message.Book.Title);
                return;
            }

            AddScopedExclusions(candidates);
        }

        private void AddScopedExclusions(IEnumerable<ImportListExclusion> candidates, IDbConnection connection = null, IDbTransaction transaction = null)
        {
            var existing = (connection == null ? _repo.All() : _repo.All(connection, transaction)).ToList();
            var additions = new List<ImportListExclusion>();
            var removals = new HashSet<int>();
            foreach (var candidate in candidates)
            {
                var matches = existing.Where(row => string.Equals(row.ForeignId, candidate.ForeignId, StringComparison.OrdinalIgnoreCase)).ToList();
                if (!candidate.MediaType.HasValue)
                {
                    removals.UnionWith(matches.Where(row => row.Id > 0 && row.MediaType.HasValue).Select(row => row.Id));
                }

                if (matches.Any(row => !row.MediaType.HasValue || row.MediaType == candidate.MediaType))
                {
                    continue;
                }

                additions.Add(candidate);
                existing.Add(candidate);
            }

            // Persist the broader replacement before discarding narrower exclusions.
            if (additions.Any())
            {
                if (connection == null) _repo.InsertMany(additions);
                else _repo.InsertMany(additions, connection, transaction);
            }
            if (removals.Any())
            {
                if (connection == null) _repo.DeleteMany(removals.ToList());
                else _repo.DeleteMany(removals, connection, transaction);
            }
        }
    }
}
