using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
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
        ImportListExclusion Get(int id);
        List<ImportListExclusion> FindByForeignId(List<string> foreignIds);
        List<ImportListExclusion> FindByForeignId(List<string> foreignIds, IDbConnection connection, IDbTransaction transaction);
        ImportListExclusion Update(ImportListExclusion importListExclusion);
    }

    public class ImportListExclusionService : IImportListExclusionService,
                                              IHandleAsync<BookDeletedEvent>
    {
        private readonly IImportListExclusionRepository _repo;
        private readonly Lazy<IBookService> _bookService;
        private readonly Logger _logger;

        // IBookService is lazy: AuthorService depends on this service, and BookService depends on AuthorService.
        public ImportListExclusionService(IImportListExclusionRepository repo, Logger logger, Lazy<IBookService> bookService = null)
        {
            _repo = repo;
            _bookService = bookService;
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

        public ImportListExclusion Get(int id)
        {
            return _repo.Get(id);
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
            var missing = (authors ?? Enumerable.Empty<Author>()).Where(author => author != null && !AuthorIdentity.GetProviderIdentityTokenList(author).Any()).ToList();
            if (missing.Count > 0)
            {
                throw new ValidationException(new[]
                {
                    new ValidationFailure("ProviderId", $"Cannot create import-list exclusions for authors without provider IDs: {string.Join(", ", missing.Select(author => $"{author.Name} (ID {author.Id})"))}.")
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

            candidates = RemoveIdsSharedWithRemainingBooks(deletedBooks, message.Book, candidates);
            if (!candidates.Any())
            {
                return;
            }

            AddScopedExclusions(candidates);
        }

        // Duplicate book rows for one work carry the same provider IDs. Excluding those IDs while
        // deleting a duplicate would also exclude the copy the user kept (and orphan its files on
        // the next refresh), so IDs still used by a remaining book in the same scope are skipped.
        private List<ImportListExclusion> RemoveIdsSharedWithRemainingBooks(List<Book> deletedBooks, Book eventBook, List<ImportListExclusion> candidates)
        {
            if (_bookService == null)
            {
                return candidates;
            }

            var authorIds = deletedBooks
                .Select(book => book.AuthorId > 0 ? book.AuthorId : eventBook?.AuthorId ?? 0)
                .Where(id => id > 0)
                .Distinct()
                .ToList();
            if (!authorIds.Any())
            {
                return candidates;
            }

            var deletedIds = deletedBooks.Select(book => book.Id).Where(id => id > 0).ToHashSet();
            List<Book> remainingBooks;
            try
            {
                remainingBooks = authorIds
                    .SelectMany(authorId => _bookService.Value.GetBooksByAuthor(authorId))
                    .Where(book => book != null && !deletedIds.Contains(book.Id))
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Could not load remaining books to check shared provider IDs; excluding all IDs of the deleted book(s)");
                return candidates;
            }

            var remainingIds = remainingBooks
                .SelectMany(book => ImportListExclusionBookMatcher.GetCanonicalProviderIds(book)
                    .Select(providerId => (ProviderId: providerId.Trim(), Book: book)))
                .ToLookup(row => row.ProviderId, row => row.Book, StringComparer.OrdinalIgnoreCase);
            if (remainingIds.Count == 0)
            {
                return candidates;
            }

            var kept = new List<ImportListExclusion>();
            var skipped = new List<(ImportListExclusion Exclusion, Book SharedWith)>();
            foreach (var candidate in candidates)
            {
                var sharedWith = remainingIds[candidate.ForeignId?.Trim() ?? string.Empty]
                    .FirstOrDefault(book => !candidate.MediaType.HasValue || book.MediaType == candidate.MediaType.Value);

                if (sharedWith == null)
                {
                    kept.Add(candidate);
                }
                else
                {
                    skipped.Add((candidate, sharedWith));
                }
            }

            foreach (var group in skipped.GroupBy(row => row.Exclusion.Name))
            {
                var keptBook = group.First().SharedWith;
                _logger.Warn("Not adding {0} import list exclusion(s) for deleted '{1}': the same provider IDs ({2}) belong to '{3}' (book {4}), which is still in the library",
                    group.Count(),
                    group.Key,
                    string.Join(", ", group.Select(row => row.Exclusion.ForeignId)),
                    keptBook.Title,
                    keptBook.Id);
            }

            return kept;
        }

        private void AddScopedExclusions(IEnumerable<ImportListExclusion> candidates, IDbConnection connection = null, IDbTransaction transaction = null)
        {
            var timer = Stopwatch.StartNew();
            var requested = candidates.ToList();
            var ids = requested.Select(row => row.ForeignId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var existing = (connection == null ? _repo.FindByForeignId(ids) : _repo.FindByForeignId(ids, connection, transaction))
                .ToLookup(row => row.ForeignId.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
            _logger.Debug("[IMPORT-LIST-EXCLUSION] Checked {0} IDs, read {1} exclusions in {2} ms", ids.Count, existing.Sum(pair => pair.Value.Count), timer.ElapsedMilliseconds);
            var additions = new List<ImportListExclusion>();
            var removals = new HashSet<int>();
            foreach (var candidate in requested)
            {
                if (!existing.TryGetValue(candidate.ForeignId, out var matches))
                {
                    matches = new List<ImportListExclusion>();
                    existing.Add(candidate.ForeignId, matches);
                }
                if (!candidate.MediaType.HasValue)
                {
                    removals.UnionWith(matches.Where(row => row.Id > 0 && row.MediaType.HasValue).Select(row => row.Id));
                }

                if (matches.Any(row => !row.MediaType.HasValue || row.MediaType == candidate.MediaType))
                {
                    continue;
                }

                additions.Add(candidate);
                matches.Add(candidate);
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
