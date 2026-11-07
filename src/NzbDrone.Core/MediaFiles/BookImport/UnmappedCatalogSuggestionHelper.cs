using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.MediaFiles.BookImport
{
    internal static class UnmappedCatalogSuggestionHelper
    {
        private static readonly string[] CanonicalProviderIdTagNames =
        {
            "PROVIDER_ID",
            "BOOK_PROVIDER_ID",
            "WORK_PROVIDER_ID",
            "EDITION_PROVIDER_ID",
            "FOREIGN_BOOK_ID",
            "FOREIGN_WORK_ID",
            "FOREIGN_EDITION_ID"
        };

        private static readonly string[] GoodreadsWorkIdTagNames =
        {
            "GOODREADS_WORK_ID"
        };

        private static readonly string[] GoodreadsEditionIdTagNames =
        {
            "GOODREADS_ID",
            "GOODREADS_BOOK_ID",
            "GOODREADS_EDITION_ID",
            "GR_ID"
        };

        public static int AddSuggestions(
            ISearchForNewBook catalogSearch,
            Logger logger,
            IReadOnlyCollection<UnmatchedFile> unmatchedFiles,
            Author allowedAuthor = null,
            CancellationToken cancellationToken = default,
            bool stopAfterFirstCandidate = false,
            bool useBookshelfMatchCompatibility = false,
            int minimumMatchPercent = 70,
            Func<Author, UnmatchedFile, BookMediaType, string> preferredEditionTermsResolver = null,
            bool allowFallbackTiers = true)
        {
            if (catalogSearch == null)
            {
                return 0;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var queries = (unmatchedFiles ?? Array.Empty<UnmatchedFile>())
                .Where(unmatched => unmatched?.File?.Path != null &&
                    !unmatched.CatalogSearchAttempted &&
                    unmatched.PotentialAuthors?.Any(author => !string.IsNullOrWhiteSpace(author?.ProviderId)) != true)
                .Select(unmatched => TryBuildCatalogSearchQuery(
                    unmatched.File,
                    out var query,
                    routineMatching: stopAfterFirstCandidate,
                    useBookshelfMatchCompatibility: useBookshelfMatchCompatibility)
                    ? new CatalogSearchQueryItem(unmatched, query)
                    : null)
                .Where(item => item != null)
                .GroupBy(item => item.Query.Key, StringComparer.Ordinal)
                .ToList();

            var suggestionsAdded = 0;
            try
            {
                Parallel.ForEach(queries, new ParallelOptions
                {
                    MaxDegreeOfParallelism = 4,
                    CancellationToken = cancellationToken
                }, queryGroup =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    foreach (var item in queryGroup)
                    {
                        // A fresh attempt supersedes an older marker. If all provider calls fail,
                        // leave this null so a later scan can retry the failed search.
                        if (item?.Unmatched?.File != null)
                        {
                            item.Unmatched.CatalogSearchAttempted = true;
                            item.Unmatched.File.LastRemoteSearchTime = null;
                        }
                    }

                    var query = queryGroup.First().Query;
                    var identifierResults = new List<Book>();
                    var catalogSearchSucceeded = false;

                    int AddTierSuggestions(IEnumerable<Book> books, CatalogSearchQuery candidateQuery = null)
                    {
                        candidateQuery ??= query;
                        if (stopAfterFirstCandidate && useBookshelfMatchCompatibility)
                        {
                            var results = (books ?? Enumerable.Empty<Book>()).ToList();
                            var added = 0;
                            foreach (var item in queryGroup)
                            {
                                var suggestion = BuildBookshelfCompatibilitySuggestion(
                                    results,
                                    candidateQuery,
                                    allowedAuthor,
                                    item?.Unmatched,
                                    GetCompatibilityTrackTags(item?.Unmatched?.File),
                                    minimumMatchPercent,
                                    preferredEditionTermsResolver);
                                if (suggestion == null)
                                {
                                    continue;
                                }

                                item.Unmatched.PotentialAuthors = new[] { suggestion };
                                added++;
                            }

                            return added;
                        }

                        return AddSuggestion(queryGroup, BuildExactCatalogSuggestion(books, candidateQuery, allowedAuthor));
                    }

                    void RecordSearch(CatalogSearchOutcome outcome, ICollection<Book> destination, bool countsAsSuccessfulSearch = true)
                    {
                        if (outcome == null)
                        {
                            return;
                        }

                        if (countsAsSuccessfulSearch)
                        {
                            catalogSearchSucceeded |= outcome.Succeeded;
                        }
                        foreach (var result in outcome.Results)
                        {
                            destination.Add(result);
                        }
                    }

                    void StampSuccessfulSearch()
                    {
                        if (!catalogSearchSucceeded)
                        {
                            return;
                        }

                        var searchedAt = DateTime.UtcNow;
                        foreach (var item in queryGroup)
                        {
                            if (item?.Unmatched?.File != null)
                            {
                                item.Unmatched.File.LastRemoteSearchTime = searchedAt;
                            }
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(query.Isbn))
                    {
                        RecordSearch(SearchCatalog(logger, "ISBN", query.Title,
                            () => catalogSearch.SearchByIsbn(query.Isbn, query.MediaType), query.MediaType, cancellationToken), identifierResults);
                    }

                    if (!string.IsNullOrWhiteSpace(query.Asin))
                    {
                        RecordSearch(SearchCatalog(logger, "ASIN", query.Title,
                            () => catalogSearch.SearchByAsin(query.Asin, query.MediaType), query.MediaType, cancellationToken), identifierResults);
                    }

                    if (!string.IsNullOrWhiteSpace(query.ProviderId))
                    {
                        var goodreadsId = 0;
                        if (query.GoodreadsEditionLookup &&
                            int.TryParse(query.ProviderId.Substring("gr:".Length), NumberStyles.None, CultureInfo.InvariantCulture, out goodreadsId))
                        {
                            RecordSearch(SearchCatalog(logger, "Goodreads ID", query.Title,
                                () => catalogSearch.SearchByGoodreadsBookId(goodreadsId, getAllEditions: true, mediaType: query.MediaType), query.MediaType, cancellationToken),
                                identifierResults,
                                countsAsSuccessfulSearch: !useBookshelfMatchCompatibility);
                        }
                        else
                        {
                            RecordSearch(SearchCatalog(logger, "provider ID " + query.ProviderId, query.Title,
                                () => catalogSearch.SearchForNewBook(query.ProviderId, null, getAllEditions: true, mediaType: query.MediaType), query.MediaType, cancellationToken), identifierResults);
                        }
                    }

                    if (HasCatalogCandidateEditions(identifierResults) && stopAfterFirstCandidate)
                    {
                        Interlocked.Add(ref suggestionsAdded, AddTierSuggestions(identifierResults));
                        StampSuccessfulSearch();
                        return;
                    }

                    var results = new List<Book>(identifierResults);

                    // The explicit Unmapped Files retry keeps its historical author/title
                    // fallback. Routine import matching uses the source-compatible tiered
                    // fallback below and stops when a tier returns candidates.
                    if (!stopAfterFirstCandidate)
                    {
                        if (!string.IsNullOrWhiteSpace(query.Title) && !string.IsNullOrWhiteSpace(query.Author))
                        {
                            RecordSearch(SearchCatalog(logger, "author/title", query.Title,
                                () => catalogSearch.SearchForNewBook(query.Title, query.Author, getAllEditions: false, mediaType: query.MediaType), query.MediaType, cancellationToken), results);
                        }

                        Interlocked.Add(ref suggestionsAdded, AddSuggestion(queryGroup, BuildExactCatalogSuggestion(results, query, allowedAuthor)));
                        StampSuccessfulSearch();
                        return;
                    }

                    if (!string.IsNullOrWhiteSpace(query.Title))
                    {
                        var authorTerms = stopAfterFirstCandidate && useBookshelfMatchCompatibility
                            ? query.AuthorSearchTerms
                            : string.IsNullOrWhiteSpace(query.Author) ? new List<string>() : new List<string> { query.Author };
                        var authorTitleResults = new List<Book>();

                        foreach (var authorTerm in authorTerms)
                        {
                            var authorTitleSearch = SearchCatalog(logger, "author/title", query.Title,
                                () => catalogSearch.SearchForNewBook(query.Title, authorTerm, getAllEditions: true, mediaType: query.MediaType), query.MediaType, cancellationToken);
                            catalogSearchSucceeded |= authorTitleSearch.Succeeded;
                            authorTitleResults.AddRange(authorTitleSearch.Results);
                        }

                        if (HasCatalogCandidateEditions(authorTitleResults))
                        {
                            Interlocked.Add(ref suggestionsAdded, AddTierSuggestions(authorTitleResults));
                            StampSuccessfulSearch();
                            return;
                        }
                    }

                    if (!allowFallbackTiers)
                    {
                        // Manual preview keeps the user's exact query scope and does not
                        // fan out to title-only, filename, or author-only catalog searches.
                        StampSuccessfulSearch();
                        return;
                    }

                    if (!string.IsNullOrWhiteSpace(query.Title))
                    {
                        var titleSearch = SearchCatalog(logger, "title", query.Title,
                            () => catalogSearch.SearchForNewBook(query.Title, null, getAllEditions: true, mediaType: query.MediaType), query.MediaType, cancellationToken);
                        catalogSearchSucceeded |= titleSearch.Succeeded;
                        if (HasCatalogCandidateEditions(titleSearch.Results))
                        {
                            Interlocked.Add(ref suggestionsAdded, AddTierSuggestions(titleSearch.Results));
                            StampSuccessfulSearch();
                            return;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(query.FileNameTitle))
                    {
                        var filenameQuery = CopyWithTitle(query, query.FileNameTitle);
                        var filenameSearch = SearchCatalog(logger, "filename title", query.FileNameTitle,
                            () => catalogSearch.SearchForNewBook(query.FileNameTitle, null, getAllEditions: true, mediaType: query.MediaType), query.MediaType, cancellationToken);
                        catalogSearchSucceeded |= filenameSearch.Succeeded;
                        if (HasCatalogCandidateEditions(filenameSearch.Results))
                        {
                            Interlocked.Add(ref suggestionsAdded, AddTierSuggestions(filenameSearch.Results, filenameQuery));
                            StampSuccessfulSearch();
                            return;
                        }
                    }

                    if (stopAfterFirstCandidate && useBookshelfMatchCompatibility
                        ? query.AuthorSearchTerms.Count > 0
                        : !string.IsNullOrWhiteSpace(query.Author))
                    {
                        var authorTerms = stopAfterFirstCandidate && useBookshelfMatchCompatibility
                            ? query.AuthorSearchTerms
                            : string.IsNullOrWhiteSpace(query.Author) ? new List<string>() : new List<string> { query.Author };
                        var authorResults = new List<Book>();
                        foreach (var authorTerm in authorTerms)
                        {
                            var authorSearch = SearchCatalog(logger, "author", authorTerm,
                                () => catalogSearch.SearchForNewBook(authorTerm, null, getAllEditions: true, mediaType: query.MediaType), query.MediaType, cancellationToken);
                            catalogSearchSucceeded |= authorSearch.Succeeded;
                            authorResults.AddRange(authorSearch.Results);
                        }

                        Interlocked.Add(ref suggestionsAdded, AddTierSuggestions(authorResults));
                    }

                    StampSuccessfulSearch();
                });
            }
            catch (AggregateException ex)
            {
                var innerExceptions = ex.Flatten().InnerExceptions;
                if (innerExceptions.Any(inner => inner is OutOfMemoryException))
                {
                    throw;
                }

                if (innerExceptions.Any(inner => inner is OperationCanceledException))
                {
                    throw new OperationCanceledException("Catalog matching was cancelled", ex, cancellationToken);
                }

                throw;
            }

            if (suggestionsAdded > 0)
            {
                logger.Info("[CATALOG-MATCH] Added {0} catalog suggestions", suggestionsAdded);
            }

            return suggestionsAdded;
        }

        private static CatalogSearchOutcome SearchCatalog(
            Logger logger,
            string searchType,
            string searchText,
            Func<List<Book>> search,
            BookMediaType mediaType,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var results = WithRequestedMediaType(search() ?? new List<Book>(), mediaType).ToList();
                cancellationToken.ThrowIfCancellationRequested();
                return new CatalogSearchOutcome(results, succeeded: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException &&
                ex is not OutOfMemoryException &&
                (ex is not AggregateException aggregate || !aggregate.Flatten().InnerExceptions.Any(inner =>
                    inner is OutOfMemoryException or OperationCanceledException)))
            {
                logger?.Debug(ex, "[CATALOG-MATCH] Catalog {0} search failed for '{1}'", searchType, searchText);
                return new CatalogSearchOutcome(new List<Book>(), succeeded: false);
            }
        }

        private sealed class CatalogSearchOutcome
        {
            public CatalogSearchOutcome(List<Book> results, bool succeeded)
            {
                Results = results ?? new List<Book>();
                Succeeded = succeeded;
            }

            public List<Book> Results { get; }
            public bool Succeeded { get; }
        }

        private static int AddSuggestion(IEnumerable<CatalogSearchQueryItem> queryGroup, AuthorSuggestion suggestion)
        {
            if (suggestion == null)
            {
                return 0;
            }

            var count = 0;
            foreach (var item in queryGroup)
            {
                item.Unmatched.PotentialAuthors = new[] { suggestion };
                count++;
            }

            return count;
        }

        private static IReadOnlyList<Dictionary<string, List<string>>> GetCompatibilityTrackTags(DiscoveredFileWithMetadata file)
        {
            if (file?.BookshelfMatchCompatibilityTrackTags?.Count > 0)
            {
                return file.BookshelfMatchCompatibilityTrackTags;
            }

            if (file?.GroupMemberTags?.Count > 0)
            {
                return file.GroupMemberTags;
            }

            return file?.AllTags == null
                ? Array.Empty<Dictionary<string, List<string>>>()
                : new[] { file.AllTags };
        }

        private static IReadOnlyList<string> GetCompatibilityTrackPaths(DiscoveredFileWithMetadata file)
        {
            if (file?.GroupMemberPaths?.Count > 0)
            {
                return file.GroupMemberPaths;
            }

            return string.IsNullOrWhiteSpace(file?.Path)
                ? Array.Empty<string>()
                : new[] { file.Path };
        }

        private static bool HasCatalogCandidateEditions(IEnumerable<Book> books)
        {
            return books?.Any(book => book?.Editions?.Any(edition => edition != null) == true) == true;
        }

        internal static bool IsBookshelfRemoteCandidateBetter(double localMatchPercent, AuthorSuggestion remoteSuggestion)
        {
            return remoteSuggestion != null && remoteSuggestion.Confidence * 100.0 > localMatchPercent;
        }

        private static AuthorSuggestion BuildBookshelfCompatibilitySuggestion(
            IEnumerable<Book> results,
            CatalogSearchQuery query,
            Author allowedAuthor,
            UnmatchedFile unmatched,
            IReadOnlyList<Dictionary<string, List<string>>> trackTags,
            int minimumMatchPercent,
            Func<Author, UnmatchedFile, BookMediaType, string> preferredEditionTermsResolver)
        {
            if (minimumMatchPercent is < 50 or > 99)
            {
                return null;
            }

            var candidates = new List<(Book Book, Edition Edition, double Score, int Order, string AuthorProviderId, string WorkProviderId, string EditionProviderId)>();
            var seenEditions = new HashSet<string>(StringComparer.Ordinal);
            var order = 0;

            foreach (var book in results ?? Enumerable.Empty<Book>())
            {
                if (book == null || book.MediaType != query.MediaType ||
                    (allowedAuthor != null && !AuthorIdentity.MatchesByProviderIdIntersection(allowedAuthor, book.Author)))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(query.ProviderId) &&
                    !BookIdentity.GetStableWorkProviderIdentityTokens(book).Contains(query.ProviderId) &&
                    !BookIdentity.GetEditionProviderIdentityTokens(book).Contains(query.ProviderId))
                {
                    continue;
                }

                var authorProviderId = NormalizeProviderId(AuthorIdentity.GetPreferredProviderId(book.Author), null);
                var workProviderId = NormalizeProviderId(GetPreferredWorkProviderId(book), null);
                if (string.IsNullOrWhiteSpace(authorProviderId) ||
                    string.IsNullOrWhiteSpace(workProviderId) ||
                    string.IsNullOrWhiteSpace(book.Author?.Name))
                {
                    continue;
                }

                foreach (var edition in book.Editions ?? new List<Edition>())
                {
                    if (edition == null)
                    {
                        continue;
                    }

                    // CandidateService de-duplicates on ForeignEditionId in provider order.
                    // Keep that identity and ordering so equal-distance candidates resolve the
                    // same way as BookshelfNG.
                    if (!seenEditions.Add(edition.ForeignEditionId))
                    {
                        continue;
                    }

                    var candidateOrder = order++;
                    var editionProviderId = GetPreferredEditionProviderId(edition);

                    var preferredEditionAuthor = allowedAuthor ?? book.Author;
                    var preferredEditionTerms = preferredEditionTermsResolver?.Invoke(preferredEditionAuthor, unmatched, query.MediaType) ??
                        preferredEditionAuthor?.GetMetadataProfileFor(query.MediaType)?.PreferredEditionTerms;
                    var score = BookshelfMatchCompatibilityScorer.GetMatchPercent(
                        trackTags,
                        candidate: null,
                        edition,
                        book,
                        query.MediaType,
                        preferredEditionTerms,
                        GetCompatibilityTrackPaths(unmatched?.File));
                    if (score < minimumMatchPercent)
                    {
                        continue;
                    }

                    candidates.Add((book, edition, score, candidateOrder, authorProviderId, workProviderId, editionProviderId));
                }
            }

            if (candidates.Count == 0)
            {
                return null;
            }

            // BookshelfNG retains provider ordering for equal scores because it only replaces
            // the current winner when the next candidate has a strictly smaller distance.
            var best = candidates
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Order)
                .First();

            return new AuthorSuggestion
            {
                ProviderId = best.AuthorProviderId,
                AuthorName = best.Book.Author.Name,
                Confidence = best.Score / 100.0,
                BookProviderId = best.WorkProviderId,
                BookTitle = best.Book.Title,
                EditionHardcoverId = best.EditionProviderId,
                EditionTitle = best.Edition?.Title
            };
        }

        private static string GetPreferredEditionProviderId(Edition edition)
        {
            if (edition == null)
            {
                return null;
            }

            var trustedId = BookEditionIdentity.GetTrustedForeignEditionId(edition);
            if (!string.IsNullOrWhiteSpace(trustedId))
            {
                return trustedId;
            }

            foreach (var (provider, value) in new[]
                     {
                         ("ol", edition.OpenLibraryEditionId),
                         ("gb", edition.GoogleBooksEditionId)
                     })
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var providerId = NormalizeProviderId(value, provider);
                if (!string.IsNullOrWhiteSpace(providerId))
                {
                    return providerId;
                }
            }

            return NormalizeProviderId(edition.ForeignEditionId, null);
        }

        private static CatalogSearchQuery CopyWithTitle(CatalogSearchQuery query, string title)
        {
            return new CatalogSearchQuery
            {
                Title = title,
                Author = query.Author,
                AuthorSearchTerms = query.AuthorSearchTerms,
                NormalizedTitle = NormalizeCatalogEvidence(title),
                NormalizedAuthor = query.NormalizedAuthor,
                Isbn = query.Isbn,
                Asin = query.Asin,
                ProviderId = query.ProviderId,
                GoodreadsEditionLookup = query.GoodreadsEditionLookup,
                MediaType = query.MediaType,
                FileNameTitle = query.FileNameTitle,
                Key = query.Key
            };
        }

        private static IEnumerable<Book> WithRequestedMediaType(IEnumerable<Book> books, BookMediaType mediaType)
        {
            foreach (var book in books ?? Enumerable.Empty<Book>())
            {
                if (book == null)
                {
                    continue;
                }

                book.MediaType = mediaType;
                yield return book;
            }
        }

        private static bool TryBuildCatalogSearchQuery(
            DiscoveredFileWithMetadata file,
            out CatalogSearchQuery query,
            bool routineMatching,
            bool useBookshelfMatchCompatibility)
        {
            query = null;
            if (file == null || string.IsNullOrWhiteSpace(file.Path))
            {
                return false;
            }

            var mediaType = QualityMediaTypeHelper.GetMediaTypeFromPath(file.Path);
            if (!mediaType.HasValue)
            {
                return false;
            }

            var titleKeys = mediaType == BookMediaType.Audiobook
                ? new[] { "ALBUM", "BOOK" }
                : new[] { "TITLE", "ALBUM", "BOOK" };
            var authorKeys = mediaType == BookMediaType.Audiobook
                ? new[] { "ALBUMARTIST", "AUTHOR", "ARTIST" }
                : new[] { "AUTHOR", "ARTIST", "ALBUMARTIST" };

            var compatibilityTrackTags = useBookshelfMatchCompatibility
                ? GetCompatibilityTrackTags(file)
                : null;
            var compatibilityTrackPaths = useBookshelfMatchCompatibility
                ? GetCompatibilityTrackPaths(file)
                : null;
            var title = useBookshelfMatchCompatibility
                ? BookshelfMatchCompatibilityScorer.GetBookshelfBookTitle(compatibilityTrackTags, mediaType.Value, compatibilityTrackPaths)
                : GetSingleTagValue(file.AllTags, titleKeys);
            var authorSearchTerms = useBookshelfMatchCompatibility
                ? BookshelfMatchCompatibilityScorer.GetBookshelfRemoteAuthorSearchTerms(compatibilityTrackTags, mediaType.Value, compatibilityTrackPaths)
                : GetMostCommonAuthorSearchTerms(file, authorKeys);
            var author = useBookshelfMatchCompatibility
                ? authorSearchTerms.FirstOrDefault()
                : GetSingleTagValue(file.AllTags, authorKeys);
            var normalizedTitle = NormalizeCatalogEvidence(title);
            var normalizedAuthor = NormalizeCatalogEvidence(author);
            var hasMultipleTracks = useBookshelfMatchCompatibility
                ? compatibilityTrackTags?.Count > 1 || compatibilityTrackPaths?.Count > 1
                : file.GroupMemberTags?.Count > 1;
            var fileNameTitle = !routineMatching || hasMultipleTracks
                ? null
                : Path.GetFileNameWithoutExtension(file.Path)
                    ?.Replace('.', ' ')
                    .Replace('_', ' ')
                    .Replace('-', ' ')
                    .Trim();
            if (useBookshelfMatchCompatibility
                ? string.Equals(fileNameTitle, title, StringComparison.InvariantCultureIgnoreCase)
                : NormalizeCatalogEvidence(fileNameTitle) == normalizedTitle)
            {
                fileNameTitle = null;
            }
            if (useBookshelfMatchCompatibility && string.IsNullOrWhiteSpace(title))
            {
                // CandidateService exits before filename fallback when BookshelfNG could not
                // produce a BookTitle from the file's parsed tags.
                fileNameTitle = null;
            }

            var isbn = useBookshelfMatchCompatibility
                ? GetConsensusLegacyIdentifier(compatibilityTrackTags, NormalizeCatalogIsbn, "ISBN", "ISBN13", "ISBN10", "ISBN-13", "ISBN-10")
                : NormalizeCatalogIsbn(GetSingleTagValue(file.AllTags, "ISBN", "ISBN13", "ISBN10", "ISBN-13", "ISBN-10"));
            var asin = useBookshelfMatchCompatibility
                ? GetConsensusLegacyIdentifier(compatibilityTrackTags, NormalizeCatalogAsin, "ASIN", "AUDIBLE_ASIN")
                : NormalizeCatalogAsin(GetSingleTagValue(file.AllTags, "ASIN", "AUDIBLE_ASIN"));
            if (useBookshelfMatchCompatibility && asin?.Length != 10)
            {
                asin = null;
            }
            ProviderIdEvidence providerIdEvidence;
            var providerIdIsValid = useBookshelfMatchCompatibility
                ? TryGetBookshelfGoodreadsEvidence(compatibilityTrackTags, out providerIdEvidence)
                : TryGetProviderIdEvidence(file.AllTags, out providerIdEvidence);
            if (!providerIdIsValid && !useBookshelfMatchCompatibility)
            {
                return false;
            }

            var hasIdentifier = isbn != null || asin != null || providerIdEvidence != null;
            var hasUsableTitle = normalizedTitle != null || NormalizeCatalogEvidence(fileNameTitle) != null;
            if (routineMatching
                ? !hasIdentifier && !hasUsableTitle
                : !hasIdentifier && (normalizedTitle == null || normalizedAuthor == null))
            {
                return false;
            }

            query = new CatalogSearchQuery
            {
                Title = title,
                Author = author,
                AuthorSearchTerms = authorSearchTerms,
                NormalizedTitle = normalizedTitle,
                NormalizedAuthor = normalizedAuthor,
                Isbn = isbn,
                Asin = asin,
                ProviderId = providerIdEvidence?.ProviderId,
                GoodreadsEditionLookup = providerIdEvidence?.GoodreadsEditionLookup == true,
                FileNameTitle = fileNameTitle,
                MediaType = mediaType.Value,
                Key = $"{(int)mediaType.Value}\u001f{normalizedTitle}\u001f{normalizedAuthor}\u001f{string.Join("\u001e", authorSearchTerms.Select(NormalizeCatalogEvidence))}\u001f{isbn}\u001f{asin}\u001f{providerIdEvidence?.ProviderId}\u001f{providerIdEvidence?.GoodreadsEditionLookup}\u001f{NormalizeCatalogEvidence(fileNameTitle)}"
            };

            return true;
        }

        private static string GetConsensusLegacyIdentifier(
            IReadOnlyList<Dictionary<string, List<string>>> trackTags,
            Func<string, string> normalize,
            params string[] names)
        {
            if (trackTags == null || trackTags.Count == 0)
            {
                return null;
            }

            var values = trackTags
                .Select(tags => BookshelfMatchCompatibilityScorer.HasCalibreOverride(tags)
                    ? GetFirstTagValue(tags, "BOOKSHELF_CALIBRE_" + names[0].ToUpperInvariant())
                    : GetFirstTagValue(tags, names))
                .Select(value => normalize == null
                    ? (string.IsNullOrWhiteSpace(value) ? null : value.Trim())
                    : normalize(value))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            return values.Count == 1 ? values[0] : null;
        }

        private static string GetFirstTagValue(Dictionary<string, List<string>> tags, params string[] names)
        {
            foreach (var name in names ?? Array.Empty<string>())
            {
                var entry = tags?.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase));
                var value = entry?.Value?.FirstOrDefault();
                if (value != null)
                {
                    return value;
                }
            }

            return null;
        }

        private static List<string> GetMostCommonAuthorSearchTerms(DiscoveredFileWithMetadata file, IReadOnlyCollection<string> keys)
        {
            IEnumerable<Dictionary<string, List<string>>> tagSets = file?.GroupMemberTags?.Count > 0
                ? file.GroupMemberTags
                : new[] { file?.AllTags };

            var candidates = tagSets
                .Select(tags => GetAuthorSearchTerms(tags, keys))
                .Where(values => values.Count > 0)
                .GroupBy(values => string.Join("\u001f", values), StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .FirstOrDefault();

            return candidates?.FirstOrDefault() ?? new List<string>();
        }

        private static List<string> GetAuthorSearchTerms(Dictionary<string, List<string>> tags, IReadOnlyCollection<string> keys)
        {
            if (tags == null)
            {
                return new List<string>();
            }

            foreach (var key in keys ?? Array.Empty<string>())
            {
                var entry = tags.FirstOrDefault(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase));
                if (entry.Key == null)
                {
                    continue;
                }

                var values = (entry.Value ?? new List<string>())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (values.Count > 0)
                {
                    return values;
                }
            }

            return new List<string>();
        }

        private static bool TryGetProviderIdEvidence(Dictionary<string, List<string>> tags, out ProviderIdEvidence providerIdEvidence)
        {
            providerIdEvidence = null;
            if (tags == null || tags.Count == 0)
            {
                return true;
            }

            var evidence = new List<ProviderIdEvidence>();
            foreach (var key in CanonicalProviderIdTagNames)
            {
                if (!TryGetSingleTagValue(tags, key, out var value, out var ambiguous))
                {
                    continue;
                }

                if (ambiguous || !TryCanonicalizeProviderId(value, null, out var providerId))
                {
                    return false;
                }

                evidence.Add(new ProviderIdEvidence(providerId, false));
            }

            foreach (var key in GoodreadsWorkIdTagNames)
            {
                if (!TryGetSingleTagValue(tags, key, out var value, out var ambiguous))
                {
                    continue;
                }

                if (ambiguous || !TryCanonicalizeProviderId(value, "gr", out var providerId))
                {
                    return false;
                }

                evidence.Add(new ProviderIdEvidence(providerId, false));
            }

            foreach (var key in GoodreadsEditionIdTagNames)
            {
                if (!TryGetSingleTagValue(tags, key, out var value, out var ambiguous))
                {
                    continue;
                }

                if (ambiguous || !TryCanonicalizeProviderId(value, "gr", out var providerId))
                {
                    return false;
                }

                evidence.Add(new ProviderIdEvidence(providerId, true));
            }

            var distinctIds = evidence
                .Select(item => item.ProviderId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (distinctIds.Count == 0)
            {
                return true;
            }

            if (distinctIds.Count != 1)
            {
                return false;
            }

            providerIdEvidence = new ProviderIdEvidence(
                distinctIds[0],
                evidence.Any(item => item.GoodreadsEditionLookup));
            return true;
        }

        private static bool TryGetBookshelfGoodreadsEvidence(
            IReadOnlyList<Dictionary<string, List<string>>> trackTags,
            out ProviderIdEvidence providerIdEvidence)
        {
            providerIdEvidence = null;
            if (trackTags == null || trackTags.Count == 0)
            {
                return true;
            }

            var goodreadsValues = new List<string>();
            foreach (var tags in trackTags)
            {
                if (!TryGetBookshelfGoodreadsValue(tags, out var value))
                {
                    // An ambiguous or invalid tag disables only the Goodreads ID tier;
                    // BookshelfNG continues to its text search tiers in this case.
                    return true;
                }

                goodreadsValues.Add(value);
            }

            var consensus = goodreadsValues.Distinct(StringComparer.Ordinal).ToList();
            if (consensus.Count != 1 || !int.TryParse(consensus[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0)
            {
                return true;
            }

            providerIdEvidence = new ProviderIdEvidence("gr:" + id.ToString(CultureInfo.InvariantCulture), true);
            return true;
        }

        private static bool TryGetBookshelfGoodreadsValue(Dictionary<string, List<string>> tags, out string value)
        {
            value = null;
            if (BookshelfMatchCompatibilityScorer.HasCalibreOverride(tags))
            {
                value = GetSingleTagValue(tags, "BOOKSHELF_CALIBRE_GOODREADS_ID");
                return true;
            }

            var names = GoodreadsWorkIdTagNames.Concat(GoodreadsEditionIdTagNames).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var values = (tags ?? new Dictionary<string, List<string>>())
                .Where(pair => names.Contains(pair.Key))
                .SelectMany(pair => pair.Value ?? new List<string>())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (values.Count > 1)
            {
                return false;
            }

            value = values.FirstOrDefault();
            return true;
        }

        private static bool TryGetSingleTagValue(Dictionary<string, List<string>> tags, string key, out string value, out bool ambiguous)
        {
            value = null;
            ambiguous = false;
            var entry = tags.FirstOrDefault(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase));
            if (entry.Key == null)
            {
                return false;
            }

            var values = (entry.Value ?? new List<string>())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (values.Count == 0)
            {
                return false;
            }

            if (values.Count != 1)
            {
                ambiguous = true;
                return true;
            }

            value = values[0];
            return true;
        }

        private static bool TryCanonicalizeProviderId(string value, string defaultPrefix, out string providerId)
        {
            providerId = null;
            try
            {
                providerId = ProviderIdHelper.Canonicalize(value, defaultPrefix);
                return !string.IsNullOrWhiteSpace(providerId);
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private static string GetSingleTagValue(Dictionary<string, List<string>> tags, params string[] keys)
        {
            foreach (var key in keys)
            {
                var entry = tags?.FirstOrDefault(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase));
                var values = entry?.Value?
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (values?.Count == 1)
                {
                    return values[0];
                }

                if (values?.Count > 1)
                {
                    return null;
                }
            }

            return null;
        }

        private static AuthorSuggestion BuildExactCatalogSuggestion(IEnumerable<Book> results, CatalogSearchQuery query, Author allowedAuthor)
        {
            var candidates = (results ?? Enumerable.Empty<Book>())
                .Where(book => book != null && book.MediaType == query.MediaType &&
                    CatalogEvidenceMatches(book, query) &&
                    (allowedAuthor == null || AuthorIdentity.MatchesByProviderIdIntersection(allowedAuthor, book.Author)))
                .Select(book => new
                {
                    Book = book,
                    AuthorProviderId = NormalizeProviderId(AuthorIdentity.GetPreferredProviderId(book.Author), null),
                    WorkProviderId = GetPreferredWorkProviderId(book)
                })
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate.AuthorProviderId) &&
                    !string.IsNullOrWhiteSpace(candidate.WorkProviderId) &&
                    !string.IsNullOrWhiteSpace(candidate.Book.Author?.Name))
                .GroupBy(candidate => $"{candidate.AuthorProviderId}\u001f{candidate.WorkProviderId}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            // Search is only a source of a reviewable author suggestion. It cannot
            // establish a file-to-work match when more than one catalog identity
            // exactly fits the file's available identity evidence.
            if (candidates.Count != 1)
            {
                return null;
            }

            var candidate = candidates[0];
            return new AuthorSuggestion
            {
                ProviderId = candidate.AuthorProviderId,
                AuthorName = candidate.Book.Author.Name,
                Confidence = 0.95,
                BookProviderId = candidate.WorkProviderId,
                BookTitle = candidate.Book.Title
            };
        }

        private static bool CatalogEvidenceMatches(Book book, CatalogSearchQuery query)
        {
            if (query.NormalizedTitle != null && NormalizeCatalogEvidence(book.Title) != query.NormalizedTitle)
            {
                return false;
            }

            if (query.NormalizedAuthor != null && NormalizeCatalogEvidence(book.Author?.Name) != query.NormalizedAuthor)
            {
                return false;
            }

            if (query.NormalizedTitle == null && query.NormalizedAuthor == null &&
                query.Isbn == null && query.Asin == null && query.ProviderId == null)
            {
                return false;
            }

            if (query.Isbn != null && !BookHasIsbn(book, query.Isbn))
            {
                return false;
            }

            if (query.Asin != null && !BookHasAsin(book, query.Asin))
            {
                return false;
            }

            if (query.ProviderId != null &&
                !BookIdentity.GetStableWorkProviderIdentityTokens(book).Contains(query.ProviderId) &&
                !BookIdentity.GetEditionProviderIdentityTokens(book).Contains(query.ProviderId))
            {
                return false;
            }

            return true;
        }

        private static bool BookHasIsbn(Book book, string isbn)
        {
            return new[] { book.ISBN10, book.ISBN13 }
                .Concat((book.Editions ?? new List<Edition>()).SelectMany(edition => new[] { edition?.Isbn10, edition?.Isbn13 }))
                .Select(NormalizeCatalogIsbn)
                .Any(candidate => candidate == isbn);
        }

        private static bool BookHasAsin(Book book, string asin)
        {
            return new[] { book.ASIN, book.AudibleASIN }
                .Concat((book.Editions ?? new List<Edition>()).SelectMany(edition => new[]
                {
                    edition?.Asin,
                    edition?.AudibleASIN
                }.Concat(edition?.Asins ?? new List<string>())))
                .Select(NormalizeCatalogAsin)
                .Any(candidate => candidate == asin);
        }

        private static string NormalizeCatalogIsbn(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var normalized = new string(value
                .Where(character => character is >= '0' and <= '9' || character == 'X' || character == 'x')
                .Select(char.ToUpperInvariant)
                .ToArray());
            if (normalized.Length == 10 && IsValidCatalogIsbn(normalized))
            {
                return ConvertIsbn10To13(normalized);
            }

            return IsValidCatalogIsbn(normalized) ? normalized : null;
        }

        private static bool IsValidCatalogIsbn(string isbn)
        {
            if (isbn.Length == 10)
            {
                var sum = 0;
                for (var index = 0; index < 10; index++)
                {
                    var digit = isbn[index] == 'X' && index == 9 ? 10 :
                        isbn[index] is >= '0' and <= '9' ? isbn[index] - '0' : -1;
                    if (digit < 0)
                    {
                        return false;
                    }

                    sum += (10 - index) * digit;
                }

                return sum % 11 == 0;
            }

            if (isbn.Length == 13 && isbn.All(character => character is >= '0' and <= '9'))
            {
                var sum = 0;
                for (var index = 0; index < 13; index++)
                {
                    sum += (isbn[index] - '0') * (index % 2 == 0 ? 1 : 3);
                }

                return sum % 10 == 0;
            }

            return false;
        }

        private static string ConvertIsbn10To13(string isbn10)
        {
            var prefix = "978" + isbn10.Substring(0, 9);
            var sum = 0;
            for (var index = 0; index < prefix.Length; index++)
            {
                sum += (prefix[index] - '0') * (index % 2 == 0 ? 1 : 3);
            }

            var checkDigit = (10 - sum % 10) % 10;
            return prefix + checkDigit;
        }

        private static string NormalizeCatalogAsin(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var normalized = new string(value
                .Where(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9')
                .Select(char.ToUpperInvariant)
                .ToArray());
            return normalized.Length == 10 ? normalized : null;
        }

        private static string GetPreferredWorkProviderId(Book book)
        {
            return BookIdentity.GetStableWorkProviderIdentityTokens(book)
                .OrderBy(GetProviderIdPriority)
                .ThenBy(providerId => providerId, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        private static int GetProviderIdPriority(string providerId)
        {
            var separator = providerId?.IndexOf(':') ?? -1;
            var prefix = separator > 0 ? providerId.Substring(0, separator) : string.Empty;
            return prefix.ToLowerInvariant() switch
            {
                "hc" => 0,
                "gr" => 1,
                "ol" => 2,
                "rn" => 3,
                "meta" => 4,
                _ => 5
            };
        }

        private static string NormalizeCatalogEvidence(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            string normalized;
            try
            {
                normalized = value.Trim().Normalize(NormalizationForm.FormKD);
            }
            catch (ArgumentException)
            {
                return null;
            }
            var builder = new StringBuilder(normalized.Length);
            foreach (var character in normalized)
            {
                if (char.IsLetterOrDigit(character))
                {
                    builder.Append(char.ToLower(character, CultureInfo.InvariantCulture));
                }
            }

            return builder.Length == 0 ? null : builder.ToString();
        }

        private sealed class CatalogSearchQuery
        {
            public string Title { get; set; }
            public string Author { get; set; }
            public List<string> AuthorSearchTerms { get; set; } = new List<string>();
            public string NormalizedTitle { get; set; }
            public string NormalizedAuthor { get; set; }
            public string Isbn { get; set; }
            public string Asin { get; set; }
            public string ProviderId { get; set; }
            public bool GoodreadsEditionLookup { get; set; }
            public string FileNameTitle { get; set; }
            public BookMediaType MediaType { get; set; }
            public string Key { get; set; }
        }

        private sealed class ProviderIdEvidence
        {
            public ProviderIdEvidence(string providerId, bool goodreadsEditionLookup)
            {
                ProviderId = providerId;
                GoodreadsEditionLookup = goodreadsEditionLookup;
            }

            public string ProviderId { get; }
            public bool GoodreadsEditionLookup { get; }
        }

        private sealed class CatalogSearchQueryItem
        {
            public CatalogSearchQueryItem(UnmatchedFile unmatched, CatalogSearchQuery query)
            {
                Unmatched = unmatched;
                Query = query;
            }

            public UnmatchedFile Unmatched { get; }
            public CatalogSearchQuery Query { get; }
        }

        private static string NormalizeProviderId(string value, string defaultPrefix)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            try
            {
                return ProviderIdHelper.Canonicalize(value.Trim(), defaultPrefix);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }
}
