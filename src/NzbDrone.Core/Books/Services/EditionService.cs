using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Microsoft.Data.Sqlite;
using Npgsql;
using NLog;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Books.Extensions;
// using NzbDrone.Core.MediaFiles.BookImport.Identification; // Disabled - old identification system
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser;

namespace NzbDrone.Core.Books
{
    public interface IEditionService
    {
        Edition GetEdition(int id);
        List<Edition> GetEditions(IEnumerable<int> ids);
        Edition GetEditionByForeignEditionId(string foreignEditionId);
        Edition GetEditionByHardcoverEditionId(string hardcoverEditionId);
        Edition GetEditionByGoodreadsEditionId(long goodreadsEditionId);
        Edition GetEditionByOpenLibraryEditionId(string openLibraryEditionId);
        Edition GetEditionByGoogleBooksEditionId(string googleBooksEditionId);
        Edition GetEditionByProviderAndId(string providerPrefix, string providerId);
        List<Edition> GetEditionsByProviderAndId(string providerPrefix, string providerId);
        List<Edition> GetAllMonitoredEditions();
        void InsertMany(List<Edition> editions);
        void InsertMany(List<Edition> editions, IDbConnection connection, IDbTransaction transaction);
        void UpdateMany(List<Edition> editions);
        void DeleteMany(List<Edition> editions);
        List<Edition> GetEditionsForRefresh(int bookId);
        List<Edition> GetEditionsByBook(int bookId);
        List<Edition> GetEditionsByBook(IEnumerable<int> bookIds);
        List<Edition> GetEditionsByAuthor(int authorId);
        Edition FindByTitle(int authorId, string title);
        Edition FindByTitleInexact(int authorId, string title);
        List<Edition> GetCandidates(int authorId, string title);
        List<Edition> SetMonitored(Edition edition, bool isManualSelection = false);
    }

    public class EditionService : IEditionService,
        IHandle<BookDeletedEvent>
    {
        private readonly IEditionRepository _editionRepository;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;
        // private readonly IDeterministicMatcher _deterministicMatcher; // Disabled - old identification system

        public EditionService(IEditionRepository editionRepository,
                              IEventAggregator eventAggregator,
                              Logger logger)
        {
            _editionRepository = editionRepository;
            _eventAggregator = eventAggregator;
            _logger = logger;
            // _deterministicMatcher = new DeterministicMatchingService(_logger); // Disabled - old identification system
        }

        public Edition GetEdition(int id)
        {
            return _editionRepository.Get(id);
        }

        public List<Edition> GetEditions(IEnumerable<int> ids)
        {
            return _editionRepository.Get(ids).ToList();
        }

        public Edition GetEditionByForeignEditionId(string foreignEditionId)
        {
            return _editionRepository.FindByForeignEditionId(foreignEditionId);
        }

        public Edition GetEditionByHardcoverEditionId(string hardcoverEditionId)
        {
            return _editionRepository.FindByHardcoverEditionId(hardcoverEditionId);
        }

        public Edition GetEditionByGoodreadsEditionId(long goodreadsEditionId)
        {
            return _editionRepository.FindByGoodreadsEditionId(goodreadsEditionId);
        }

        public Edition GetEditionByOpenLibraryEditionId(string openLibraryEditionId)
        {
            return _editionRepository.FindByOpenLibraryEditionId(openLibraryEditionId);
        }

        public Edition GetEditionByGoogleBooksEditionId(string googleBooksEditionId)
        {
            return _editionRepository.FindByGoogleBooksEditionId(googleBooksEditionId);
        }

	        /// <summary>
	        /// Provider-agnostic edition lookup that routes to the correct database column based on provider prefix.
		        /// Examples: GetEditionByProviderAndId("hc", "32015950") → HardcoverEditionId column
		        ///          GetEditionByProviderAndId("gr", "123456789") → GoodreadsEditionId column
		        ///          GetEditionByProviderAndId("ol", "OL12345M") → OpenLibraryEditionId column
		        ///          GetEditionByProviderAndId("az", "B00ABC1234") → Asin/AudibleASIN column
		        ///          GetEditionByProviderAndId("isbn", "9780123456789") → Isbn13/Isbn10 columns
		        /// </summary>
	        public Edition GetEditionByProviderAndId(string providerPrefix, string providerId)
        {
            return ChooseOldestProviderEdition(GetEditionsByProviderAndId(providerPrefix, providerId), providerPrefix, providerId);
        }

        public List<Edition> GetEditionsByProviderAndId(string providerPrefix, string providerId)
        {
            if (string.IsNullOrWhiteSpace(providerPrefix) || string.IsNullOrWhiteSpace(providerId))
            {
                _logger.Warn("GetEditionsByProviderAndId called with null/empty parameters: provider='{0}', id='{1}'", providerPrefix, providerId);
                return new List<Edition>();
            }

            var prefix = providerPrefix.Trim().ToLowerInvariant();
            var id = providerId.Trim();

            switch (prefix)
            {
                case "hc":
                    _logger.Debug("Looking up Hardcover edition with ID: {0}", providerId);
                    return _editionRepository.FindAllByHardcoverEditionId(id);

                case "gr":
                    if (long.TryParse(id, out var goodreadsId))
                    {
                        _logger.Debug("Looking up Goodreads edition with ID: {0}", goodreadsId);
                        return _editionRepository.FindAllByGoodreadsEditionId(goodreadsId);
                    }

                    _logger.Error("Invalid Goodreads edition ID format: {0} (expected numeric)", id);
                    return new List<Edition>();

                case "ol":
                    _logger.Debug("Looking up OpenLibrary edition with ID: {0}", providerId);
                    return _editionRepository.FindAllByOpenLibraryEditionId(id);

                case "gb":
                    _logger.Debug("Looking up Google Books edition with ID: {0}", providerId);
                    return _editionRepository.FindAllByGoogleBooksEditionId(id);

                case "az":
                    {
                        var normalized = id.ToUpperInvariant();
                        _logger.Debug("Looking up edition with ASIN: {0}", normalized);

                        return DistinctEditions(
                            _editionRepository.FindAllByAsin(normalized),
                            string.Equals(normalized, id, StringComparison.Ordinal) ? null : _editionRepository.FindAllByAsin(id));
                    }

                case "isbn":
                    {
                        var normalized = id.Replace("-", string.Empty).Replace(" ", string.Empty);
                        _logger.Debug("Looking up edition with ISBN: {0}", normalized);

                        return DistinctEditions(
                            _editionRepository.FindAllByIsbn(normalized),
                            string.Equals(normalized, id, StringComparison.Ordinal) ? null : _editionRepository.FindAllByIsbn(id));
                    }

                default:
                    _logger.Warn("Unknown provider prefix: {0}. Supported providers: hc, gr, ol, gb, az, isbn", providerPrefix);
                    return new List<Edition>();
            }
        }

        private Edition ChooseOldestProviderEdition(List<Edition> editions, string providerPrefix, string providerId)
        {
            var matches = (editions ?? new List<Edition>())
                .Where(e => e != null)
                .OrderBy(e => e.Id)
                .ToList();

            var chosen = matches.FirstOrDefault();
            if (matches.Count > 1)
            {
                var expectedPlural = IsExpectedPluralSingularLookup(providerPrefix);
                var message = "Provider edition lookup {0}:{1} resolved to {2} local editions; legacy singular lookup using oldest EditionId={3}";
                if (expectedPlural)
                {
                    _logger.Debug(message, providerPrefix, providerId, matches.Count, chosen?.Id);
                }
                else
                {
                    _logger.Warn(message, providerPrefix, providerId, matches.Count, chosen?.Id);
                }
            }

            return chosen;
        }

        private static bool IsExpectedPluralSingularLookup(string providerPrefix)
        {
            return string.Equals(providerPrefix, "az", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(providerPrefix, "isbn", StringComparison.OrdinalIgnoreCase);
        }

        private static List<Edition> DistinctEditions(params IEnumerable<Edition>[] groups)
        {
            var results = new List<Edition>();
            var seen = new HashSet<int>();

            foreach (var group in groups.Where(g => g != null))
            {
                foreach (var edition in group.Where(e => e != null))
                {
                    if (edition.Id > 0 && !seen.Add(edition.Id))
                    {
                        continue;
                    }

                    results.Add(edition);
                }
            }

            return results
                .OrderBy(e => e.Id)
                .ToList();
        }

        public List<Edition> GetAllMonitoredEditions()
        {
            return _editionRepository.GetAllMonitoredEditions();
        }

	        public void InsertMany(List<Edition> editions)
	        {
	            if (editions == null)
	            {
	                throw new ArgumentNullException(nameof(editions));
	            }

	            var invalidEdition = editions.FirstOrDefault(e => e == null || string.IsNullOrWhiteSpace(e.Title));
	            if (invalidEdition != null)
	            {
	                var bookId = invalidEdition?.BookId.ToString() ?? "NULL";
	                var foreignEditionId = invalidEdition?.ForeignEditionId ?? "NULL";
	                throw new ArgumentException(
	                    $"Edition.Title is required (BookId={bookId}, ForeignEditionId='{foreignEditionId}')",
	                    nameof(editions));
	            }

	            try
	            {
	                if (_logger.IsDebugEnabled)
                {
	                    // Debug logging for edition images
	                    _logger.Debug("InsertMany called with {0} editions", editions.Count);
	                    foreach (var edition in editions.Take(5)) // Log first 5 to avoid spam
                    {
                        _logger.Debug("Edition '{0}' (ID: {1}) has {2} images",
                            edition.Title,
                            edition.Id,
                            edition.Images?.Count ?? 0);
                        if (edition.Images != null && edition.Images.Any())
                        {
                            foreach (var img in edition.Images)
                            {
                                _logger.Debug("  - Image: CoverType={0}, Url={1}", img.CoverType, img.Url);
                            }
                        }
                    }
                }

                // Compute MatchingTitle for FTS matching (canonical normalization)
                foreach (var edition in editions)
                {
                    edition.MatchingTitle = StringSuperNormalizer.ComputeMatchingTitle(edition.Title);
                }

                _editionRepository.InsertMany(editions);

	                if (_logger.IsDebugEnabled)
                {
	                    // Verify images were persisted
	                    _logger.Debug("Verifying edition images after insertion");
	                    foreach (var edition in editions.Take(5))
                    {
                        var savedEdition = _editionRepository.FindByForeignEditionId(edition.ForeignEditionId);
                        if (savedEdition != null)
                        {
                            _logger.Debug("Saved edition '{0}' has {1} images", savedEdition.Title, savedEdition.Images?.Count ?? 0);
                        }
                    }
                }
            }
            catch (SqliteException ex) when (IsEditionTitleSlugUniqueViolation(ex))
            {
                RetryEditionInsertWithDeduplicatedTitleSlugs(editions, ex);
            }
            catch (PostgresException ex) when (IsEditionTitleSlugUniqueViolation(ex))
            {
                RetryEditionInsertWithDeduplicatedTitleSlugs(editions, ex);
            }
        }

        private void RetryEditionInsertWithDeduplicatedTitleSlugs(List<Edition> editions, Exception originalException)
        {
            _logger.Warn(originalException, "Duplicate Edition.TitleSlug detected during edition insertion; deduplicating and retrying");

            EnsureUniqueTitleSlugsForInsert(editions);

            try
            {
                _editionRepository.InsertMany(editions);
                _logger.Info("Successfully inserted {0} editions after TitleSlug deduplication", editions.Count);
            }
            catch (Exception retryEx)
            {
                _logger.Error(retryEx, "Failed to insert editions after TitleSlug deduplication");
                throw;
            }
        }

        private void EnsureUniqueTitleSlugsForInsert(List<Edition> editions)
        {
            var baseSlugs = editions
                .Select(e => e?.TitleSlug?.Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var reservedSlugs = _editionRepository.FindExistingTitleSlugsForUniqueness(baseSlugs);

            foreach (var edition in editions)
            {
                if (edition == null || string.IsNullOrWhiteSpace(edition.TitleSlug))
                {
                    continue;
                }

                var baseSlug = edition.TitleSlug.Trim();
                var finalSlug = baseSlug;
                var counter = 2;

                while (reservedSlugs.Contains(finalSlug))
                {
                    finalSlug = $"{baseSlug}_{counter}";
                    counter++;
                }

                if (!string.Equals(edition.TitleSlug, finalSlug, StringComparison.Ordinal))
                {
                    _logger.Debug("Deduplicated edition TitleSlug: '{0}' -> '{1}' (Title='{2}', ForeignEditionId='{3}')",
                        edition.TitleSlug,
                        finalSlug,
                        edition.Title,
                        edition.ForeignEditionId);

                    edition.TitleSlug = finalSlug;
                }
                else if (!string.Equals(edition.TitleSlug, baseSlug, StringComparison.Ordinal))
                {
                    // Normalize whitespace differences.
                    edition.TitleSlug = baseSlug;
                }

                reservedSlugs.Add(edition.TitleSlug);
            }
        }

        private static bool IsEditionTitleSlugUniqueViolation(SqliteException ex)
        {
            const int sqliteConstraintUnique = 2067; // SQLITE_CONSTRAINT_UNIQUE
            return ex.SqliteExtendedErrorCode == sqliteConstraintUnique &&
                   ex.Message.Contains("Editions.TitleSlug", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsEditionTitleSlugUniqueViolation(PostgresException ex)
        {
            if (!string.Equals(ex.SqlState, "23505", StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(ex.TableName) &&
                !string.Equals(ex.TableName, "Editions", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return (!string.IsNullOrWhiteSpace(ex.ConstraintName) &&
                    ex.ConstraintName.Contains("TitleSlug", StringComparison.OrdinalIgnoreCase)) ||
                   (!string.IsNullOrWhiteSpace(ex.Detail) &&
                    ex.Detail.Contains("TitleSlug", StringComparison.OrdinalIgnoreCase));
        }

        public void InsertMany(List<Edition> editions, IDbConnection connection, IDbTransaction transaction)
        {
            // Compute MatchingTitle for FTS matching (canonical normalization)
            foreach (var edition in editions)
            {
                edition.MatchingTitle = StringSuperNormalizer.ComputeMatchingTitle(edition.Title);
            }

            // Simplified version for transaction-based call - no error handling needed
            // since the outer transaction will handle rollback if needed
            _editionRepository.InsertMany(editions, connection, transaction);
        }

        public void UpdateMany(List<Edition> editions)
        {
            // Compute MatchingTitle for FTS matching (canonical normalization)
            foreach (var edition in editions)
            {
                edition.MatchingTitle = StringSuperNormalizer.ComputeMatchingTitle(edition.Title);
            }

            _editionRepository.UpdateMany(editions);
        }

        public void DeleteMany(List<Edition> editions)
        {
            _editionRepository.DeleteMany(editions);
            foreach (var edition in editions)
            {
                _eventAggregator.PublishEvent(new EditionDeletedEvent(edition));
            }
        }

        public List<Edition> GetEditionsForRefresh(int bookId)
        {
            return _editionRepository.GetEditionsForRefresh(bookId);
        }

        public List<Edition> GetEditionsByBook(int bookId)
        {
            return _editionRepository.FindByBook(new[] { bookId });
        }

        public List<Edition> GetEditionsByBook(IEnumerable<int> bookIds)
        {
            return _editionRepository.FindByBook(bookIds);
        }

        public List<Edition> GetEditionsByAuthor(int authorId)
        {
            return _editionRepository.FindByAuthor(authorId);
        }

        public Edition FindByTitle(int authorId, string title)
        {
            return _editionRepository.FindByTitle(authorId, title);
        }

        public Edition FindByTitleInexact(int authorId, string title)
        {
            var books = _editionRepository.FindByAuthorId(authorId, true);

            foreach (var func in EditionScoringFunctions(title))
            {
                var results = FindByStringInexact(books, func.Item1, func.Item2);
                if (results.Count == 1)
                {
                    return results[0];
                }
            }

            return null;
        }

        public List<Edition> GetCandidates(int authorId, string title)
        {
            var books = _editionRepository.FindByAuthorId(authorId, true);
            var output = new List<Edition>();

            foreach (var func in EditionScoringFunctions(title))
            {
                output.AddRange(FindByStringInexact(books, func.Item1, func.Item2));
            }

            return output.DistinctBy(x => x.Id).ToList();
        }

        public List<Edition> SetMonitored(Edition edition, bool isManualSelection = false)
        {
            return _editionRepository.SetMonitored(edition, isManualSelection);
        }

        public void Handle(BookDeletedEvent message)
        {
            // BookService snapshots editions before deletion so notifications and
            // asynchronous cleanup retain the full book shape. Reuse that snapshot
            // instead of querying the same editions again for every deleted book.
            var editions = message.Book.Editions ?? GetEditionsByBook(message.Book.Id);
            DeleteMany(editions);
        }

        private List<Tuple<Func<Edition, string, double>, string>> EditionScoringFunctions(string title)
        {
            Func<Func<Edition, string, double>, string, Tuple<Func<Edition, string, double>, string>> tc = Tuple.Create;
            var scoringFunctions = new List<Tuple<Func<Edition, string, double>, string>>
            {
                tc((a, t) => a.Title.Equals(t, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0, title),
                tc((a, t) => a.Title.Equals(t, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0, title.RemoveBracketsAndContents().CleanAuthorName()),
                tc((a, t) => a.Title.Equals(t, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0, title.RemoveAfterDash().CleanAuthorName()),
                tc((a, t) => a.Title.Equals(t, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0, title.RemoveBracketsAndContents().RemoveAfterDash().CleanAuthorName()),
                tc((a, t) => t.Contains(a.Title) || a.Title.Contains(t) ? 0.8 : 0.0, title)
            };

            return scoringFunctions;
        }

        private List<Edition> FindByStringInexact(List<Edition> editions, Func<Edition, string, double> scoreFunction, string title)
        {
            const double fuzzThreshold = 0.7;
            const double fuzzGap = 0.4;

            var sortedEditions = editions.Select(s => new
            {
                MatchProb = scoreFunction(s, title),
                Edition = s
            })
                .ToList()
                .OrderByDescending(s => s.MatchProb)
                .ToList();

            return sortedEditions.TakeWhile((x, i) => i == 0 || sortedEditions[i - 1].MatchProb - x.MatchProb < fuzzGap)
                .TakeWhile((x, i) => x.MatchProb > fuzzThreshold || (i > 0 && sortedEditions[i - 1].MatchProb > fuzzThreshold))
                .Select(x => x.Edition)
                .ToList();
        }

    }
}
