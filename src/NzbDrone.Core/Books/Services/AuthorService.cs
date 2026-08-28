using System;
using System.Collections.Generic;
using System.Linq;
using FluentValidation;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.MediaCover.Commands;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MetadataSource;
// using NzbDrone.Core.MediaFiles.BookImport.Identification; // Disabled - old identification system
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.Books
{
    public interface IAuthorService
    {
        Author GetAuthor(int authorId);
        List<Author> GetAuthors(IEnumerable<int> authorIds);
        Author AddAuthor(Author newAuthor, bool doRefresh);
        List<Author> AddAuthors(List<Author> newAuthors, bool doRefresh);
        Author FindByProviderId(string provider, string providerId);
        Author FindByName(string title);
        Author FindByNameInexact(string title);
        List<Author> GetCandidates(string title);
	        void DeleteAuthor(int authorId, bool deleteFiles, bool addImportListExclusion = false);
            // Purge/re-add deliberately retains files, so production must preserve the import
            // history that prevents a still-seeding download from being imported again.
            // The default exists only for lightweight test doubles.
            List<int> DeleteAuthorForReadd(int authorId)
            {
                throw new NotSupportedException();
            }
	        void DeleteAuthors(List<int> authorIds, bool deleteFiles, bool addImportListExclusion = false)
	        {
	            foreach (var authorId in authorIds ?? new List<int>())
	            {
	                DeleteAuthor(authorId, deleteFiles, addImportListExclusion);
	            }
	        }
        List<Author> GetAllAuthors(bool bypassCache = false);
        Dictionary<int, List<int>> GetAllAuthorTags();
        List<Author> AllForTag(int tagId);
        Author UpdateAuthor(Author author);
        Author UpdateAuthorProgressiveSettings(Author author, int? audiobookQualityProfileId, int? audiobookMetadataProfileId,
            bool? audiobookMonitored, NewItemMonitorTypes? audiobookMonitorNewItems,
            int? ebookQualityProfileId, int? ebookMetadataProfileId,
            bool? ebookMonitored, NewItemMonitorTypes? ebookMonitorNewItems,
            string rootFolderPath)
        {
            throw new NotSupportedException();
        }
        List<Author> UpdateAuthors(List<Author> authors, bool useExistingRelativeFolder);
        Dictionary<int, string> AllAuthorPaths();
        bool AuthorPathExists(string folder);
        void RemoveAddOptions(Author author);
        void SetMediaTypeMonitoring(int authorId, string mediaType, bool monitored);
        // The default exists only for lightweight test doubles. Every production implementation must override it.
        void EnsureMediaTypeMonitoring(int authorId, string mediaType)
        {
            throw new NotSupportedException();
        }
        long GetAuthorSizeForMediaType(int authorId, string mediaType);
        void UpdateLastSelectedMediaType(int authorId, string mediaType);
        List<Book> GetAuthorBooksFromCache(int authorId);
        List<int> GetAuthorIdsByMetadataProfileId(int metadataProfileId);
        void ClearAuthorCache();
    }

    public class AuthorService : IAuthorService
    {
        private readonly IAuthorRepository _authorRepository;
        private readonly IEventAggregator _eventAggregator;
        private readonly IBuildAuthorPaths _authorPathBuilder;
        private readonly IRootFolderService _rootFolderService;
	        private readonly IManageCommandQueue _commandQueueManager;
	        private readonly Logger _logger;
	        private readonly ICached<List<Author>> _cache;
	        private readonly ICached<Author> _authorByIdCache;
	        private static readonly TimeSpan AuthorByIdCacheTtl = TimeSpan.FromSeconds(10);
	        // private readonly IDeterministicMatcher _deterministicMatcher; // Disabled - old identification system
	        private readonly IBookRepository _bookRepository;
	        private readonly IMediaFileService _mediaFileService;
	        private readonly IProviderAliasService _providerAliasService;

        // Single-author cache fields
        private volatile int _currentAuthorId = -1;
        private volatile List<Book> _currentAuthorBooks = null;
        private readonly object _authorCacheLock = new object();

        public AuthorService(IAuthorRepository authorRepository,
                             IEventAggregator eventAggregator,
                             IBuildAuthorPaths authorPathBuilder,
                             IRootFolderService rootFolderService,
                             IManageCommandQueue commandQueueManager,
	                             ICacheManager cacheManager,
	                             IBookRepository bookRepository,
	                             IMediaFileService mediaFileService,
	                             Logger logger,
	                             IProviderAliasService providerAliasService = null)
	        {
	            _authorRepository = authorRepository;
	            _eventAggregator = eventAggregator;
	            _authorPathBuilder = authorPathBuilder;
                _rootFolderService = rootFolderService;
	            _commandQueueManager = commandQueueManager;
	            _cache = cacheManager.GetRollingCache<List<Author>>(GetType(), "authorcache", TimeSpan.FromSeconds(30));
	            _authorByIdCache = cacheManager.GetCache<Author>(GetType(), "authorById");
	            _logger = logger;
	            // _deterministicMatcher = new DeterministicMatchingService(_logger); // Disabled - old identification system
	            _bookRepository = bookRepository;
	            _mediaFileService = mediaFileService;
	            _providerAliasService = providerAliasService;
	        }

        private void EnsureAuthorDbFields(Author author, bool ensurePaths, bool requirePath)
        {
            if (author == null)
            {
                return;
            }

            if (author.Name.IsNullOrWhiteSpace())
            {
                throw new InvalidOperationException("Cannot persist author with empty Name");
            }

            // Provider ID contract: IDs are always stored in canonical {prefix}:{id} form where prefix is one of:
            // az/gr/hc/ol/gb. Anything else is an upstream/data bug and should not be silently repaired here.
            static string NormalizeOrNull(string providerId, string expectedPrefix)
            {
                if (string.IsNullOrWhiteSpace(providerId))
                {
                    return null;
                }

                return ProviderIdHelper.Normalize(providerId.Trim(), expectedPrefix);
            }

            author.HardcoverAuthorId = NormalizeOrNull(author.HardcoverAuthorId, "hc");
            author.GoodreadsAuthorId = NormalizeOrNull(author.GoodreadsAuthorId, "gr");
            author.OpenLibraryAuthorId = NormalizeOrNull(author.OpenLibraryAuthorId, "ol");
            author.GoogleBooksAuthorId = NormalizeOrNull(author.GoogleBooksAuthorId, "gb");
            author.AudnexusAuthorId = NormalizeOrNull(author.AudnexusAuthorId, "az");

            if (author.CleanName.IsNullOrWhiteSpace())
            {
                author.CleanName = author.Name.CleanAuthorName();
                _logger.Debug("Computed missing CleanName for author '{0}'", author.Name);
            }

            if (author.LastSelectedMediaType.IsNullOrWhiteSpace())
            {
                author.LastSelectedMediaType = "audiobook";
            }

            // Ensure embedded documents can't violate NOT NULL constraints or deserialize to null.
            author.Aliases ??= new List<string>();
            author.Images ??= new List<MediaCover.MediaCover>();
            author.Images = author.Images
                .Where(image => image != null &&
                                !NzbDrone.Core.MediaCover.MediaCoverRendition.IsKnownPlaceholderImageUrl(image.Url))
                .ToList();
            author.Links ??= new List<Links>();
            author.Genres ??= new List<string>();
            author.Pseudonyms ??= new List<string>();
            author.ProviderUrls ??= new ProviderUrlMap();
            author.Ratings ??= new Ratings();
            author.Tags ??= new HashSet<int>();

            if (author.Added == default)
            {
                author.Added = DateTime.UtcNow;
            }

            if (ensurePaths && _authorPathBuilder != null)
            {
                try
                {
                    _authorPathBuilder.EnsureAuthorPaths(author, useExistingRelativeFolder: false);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Failed to ensure author paths before persist for {0}", author.Name);
                }
            }

            if (requirePath && author.Path.IsNullOrWhiteSpace())
            {
                throw new InvalidOperationException($"Author '{author.Name}' has no Path set (root folder may be missing or path generation failed)");
            }
        }

        private void RefreshAuthorProviderAliases(IEnumerable<Author> authors)
        {
            if (_providerAliasService == null || authors == null)
            {
                return;
            }

            foreach (var author in authors)
            {
                RefreshAuthorProviderAliases(author);
            }
        }

        private void RefreshAuthorProviderAliases(Author author)
        {
            if (_providerAliasService == null || author == null || author.Id <= 0)
            {
                return;
            }

            var providerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            AddProviderId(providerIds, author.GoodreadsAuthorId);
            AddProviderId(providerIds, author.HardcoverAuthorId);
            AddProviderId(providerIds, author.OpenLibraryAuthorId);
            AddProviderId(providerIds, author.GoogleBooksAuthorId);
            AddProviderId(providerIds, author.AudnexusAuthorId);

            if (author.RemoteProviderIds != null)
            {
                foreach (var providerId in author.RemoteProviderIds)
                {
                    AddProviderId(providerIds, providerId);
                }
            }

            _providerAliasService.ReplaceAliases("Author", author.Id, "author", providerIds);
        }

        private static void AddProviderId(ISet<string> values, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                values.Add(value.Trim());
            }
        }

        public Author AddAuthor(Author newAuthor, bool doRefresh)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var rootFolders = _rootFolderService?.All();

            ApplySyncMonitoredAcrossFormatsDefaultOnAdd(newAuthor, rootFolders);
            NormalizeOrValidateSyncMonitoredAcrossFormats(newAuthor, null, rootFolders);
            
            EnsureAuthorDbFields(newAuthor, ensurePaths: true, requirePath: true);

            _cache.Clear();
            var insertedAuthor = _authorRepository.Insert(newAuthor);
            RefreshAuthorProviderAliases(insertedAuthor);
            
            stopwatch.Stop();
            _logger.Debug("[DB-TIMING] Author '{0}' inserted in {1}ms", newAuthor.Name, stopwatch.ElapsedMilliseconds);
            
            _eventAggregator.PublishEvent(new AuthorAddedEvent(GetAuthor(insertedAuthor.Id), doRefresh));

            // Queue media download for the new author
            _commandQueueManager.Push(new DownloadAuthorMediaCommand(insertedAuthor.Id));

            return insertedAuthor;
        }

        public List<Author> AddAuthors(List<Author> newAuthors, bool doRefresh)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var rootFolders = _rootFolderService?.All();
            
            foreach (var author in newAuthors)
            {
                ApplySyncMonitoredAcrossFormatsDefaultOnAdd(author, rootFolders);
                NormalizeOrValidateSyncMonitoredAcrossFormats(author, null, rootFolders);
                EnsureAuthorDbFields(author, ensurePaths: true, requirePath: true);
            }

            _cache.Clear();
            _authorRepository.InsertMany(newAuthors);
            RefreshAuthorProviderAliases(newAuthors);
            
            stopwatch.Stop();
            _logger.Debug("[DB-TIMING] {0} authors batch inserted in {1}ms (avg {2}ms/author)",
                newAuthors.Count, stopwatch.ElapsedMilliseconds, 
                newAuthors.Count > 0 ? stopwatch.ElapsedMilliseconds / newAuthors.Count : 0);
            
            _eventAggregator.PublishEvent(new AuthorsImportedEvent(newAuthors.Select(s => s.Id).ToList(), doRefresh));

            // Queue media downloads for all new authors
            foreach (var author in newAuthors)
            {
                _commandQueueManager.Push(new DownloadAuthorMediaCommand(author.Id));
            }

            return newAuthors;
        }

        public bool AuthorPathExists(string folder)
        {
            return _authorRepository.AuthorPathExists(folder);
        }

	        public void DeleteAuthor(int authorId, bool deleteFiles, bool addImportListExclusion = false)
	        {
	            DeleteAuthorsInternal(new List<int> { authorId }, deleteFiles, addImportListExclusion, false);
	        }

	        public List<int> DeleteAuthorForReadd(int authorId)
	        {
	            return DeleteAuthorsInternal(new List<int> { authorId }, false, false, true);
	        }

	        public void DeleteAuthors(List<int> authorIds, bool deleteFiles, bool addImportListExclusion = false)
	        {
	            DeleteAuthorsInternal(authorIds, deleteFiles, addImportListExclusion, false);
	        }

	        private List<int> DeleteAuthorsInternal(
	            List<int> authorIds,
	            bool deleteFiles,
	            bool addImportListExclusion,
	            bool preserveRetainedFileHistory)
	        {
	            var retainedBookFileIds = new List<int>();
	            var distinctIds = (authorIds ?? new List<int>())
	                .Where(id => id > 0)
	                .Distinct()
	                .ToList();

	            if (!distinctIds.Any())
	            {
	                return retainedBookFileIds;
	            }

	            _cache.Clear();
	            foreach (var authorId in distinctIds)
	            {
	                _authorByIdCache.Remove(authorId.ToString());
	            }

	            var authors = _authorRepository.Get(distinctIds)
	                .Where(author => author != null)
	                .ToList();

	            // Keep every parent row alive while synchronous handlers remove Books and Editions.
	            // Besides satisfying the PostgreSQL FK, this lets the SQLite edition_fts delete trigger
	            // read the original Book and Author values before its Edition row disappears.
	            foreach (var author in authors)
	            {
	                var retainedBookFiles = preserveRetainedFileHistory
	                    ? _mediaFileService.GetFilesByAuthor(author.Id)
	                        .Where(file => file != null && file.Id > 0)
	                        .ToList()
	                    : new List<BookFile>();
	                var authorRetainedBookFileIds = retainedBookFiles
	                    .Select(file => file.Id)
	                    .Distinct()
	                    .ToList();
	                retainedBookFileIds.AddRange(authorRetainedBookFileIds);
	                var retainedBookFileEditionIds = retainedBookFiles
	                    .Where(file => !string.IsNullOrWhiteSpace(file.Edition?.ForeignEditionId))
	                    .GroupBy(file => file.Id)
	                    .ToDictionary(group => group.Key, group => group.First().Edition.ForeignEditionId);

	                _eventAggregator.PublishEvent(new AuthorDeletedEvent(
	                    author,
	                    deleteFiles,
	                    addImportListExclusion,
	                    preserveRetainedFileHistory,
	                    authorRetainedBookFileIds,
	                    retainedBookFileEditionIds));
	                _providerAliasService?.DeleteAliases("Author", author.Id);
	            }

	            _authorRepository.DeleteMany(authors.Select(author => author.Id));

	            return retainedBookFileIds.Distinct().ToList();
	        }

        public Author FindByProviderId(string provider, string providerId)
        {
            if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(providerId))
            {
                return null;
            }

            provider = provider.Trim().ToLowerInvariant();
            providerId = providerId.Trim();

            string canonicalPrefix;
            Func<string, Author> finder;

	            switch (provider)
	            {
	                case "gr":
	                    canonicalPrefix = "gr";
	                    finder = _authorRepository.FindByGoodreadsId;
	                    break;

	                case "hc":
	                    canonicalPrefix = "hc";
	                    finder = _authorRepository.FindByHardcoverId;
	                    break;

	                case "ol":
	                    canonicalPrefix = "ol";
	                    finder = _authorRepository.FindByOpenLibraryId;
	                    break;

	                case "gb":
	                    canonicalPrefix = "gb";
	                    finder = _authorRepository.FindByGoogleBooksId;
	                    break;

	                case "az":
	                    canonicalPrefix = "az";
	                    finder = _authorRepository.FindByAudnexusId;
	                    break;

	                default:
	                    return null;
	            }

            var normalizedProviderId = providerId.Contains(":")
                ? ProviderIdHelper.Normalize(providerId, canonicalPrefix)
                : ProviderIdHelper.WithPrefix(canonicalPrefix, providerId);

            var direct = CacheLookupAuthor(finder(normalizedProviderId));
            if (direct != null)
            {
                return direct;
            }

            var aliasAuthorId = _providerAliasService?.FindAuthorIds(new[] { normalizedProviderId })
                .OrderBy(id => id)
                .FirstOrDefault();

            return aliasAuthorId.GetValueOrDefault() > 0 ? CacheLookupAuthor(_authorRepository.Get(aliasAuthorId.Value)) : null;
        }

        public Author FindByName(string title)
        {
            return CacheLookupAuthor(_authorRepository.FindByName(title.CleanAuthorName()));
        }

        private Author CacheLookupAuthor(Author author)
        {
            if (author == null || author.Id <= 0)
            {
                return author;
            }

            var key = author.Id.ToString();
            var cached = _authorByIdCache.Find(key);
            if (cached != null)
            {
                return cached;
            }

            _authorByIdCache.Set(key, author, AuthorByIdCacheTtl);
            return author;
        }

        public List<Tuple<Func<Author, string, double>, string>> AuthorScoringFunctions(string title, string cleanTitle)
        {
            // DEPRECATED-IDENTIFICATION: Old deterministic matching system
            // var matcher = new DeterministicMatchingService(_logger);
            // IDeterministicMatcher matcher = null;

            Func<Func<Author, string, double>, string, Tuple<Func<Author, string, double>, string>> tc = Tuple.Create;
            var scoringFunctions = new List<Tuple<Func<Author, string, double>, string>>
            {
                // tc((a, t) => matcher.AuthorNamesMatch(a.Metadata.Name, t) ? 1.0 : 0.0, title),
                // tc((a, t) => matcher.AuthorNamesMatch(a.Metadata.NameLastFirst, t) ? 1.0 : 0.0, title)
                tc((a, t) => a.Name.Equals(t, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0, title),
                tc((a, t) => a.NameLastFirst.Equals(t, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0, title)
            };

            return scoringFunctions;
        }

        public Author FindByNameInexact(string title)
        {
            var authors = GetAllAuthors();

            foreach (var func in AuthorScoringFunctions(title, title.CleanAuthorName()))
            {
                // AuthorScoringFunctions is intentionally exact-name / last-first
                // matching. Avoid sorting the full author library for every RSS
                // release that only needs an exact fallback.
                var results = authors
                    .Where(author => func.Item1(author, func.Item2) > 0.8)
                    .Take(2)
                    .ToList();

                if (results.Count == 1)
                {
                    return results[0];
                }
            }

            return null;
        }

        public List<Author> GetCandidates(string title)
        {
            var authors = GetAllAuthors();
            var output = new List<Author>();

            // Normalize the search term for comparison
            var normalizedSearch = NormalizeAuthorNameForComparison(title);

            // First try exact normalized match
            var exactMatches = authors.Where(a =>
                NormalizeAuthorNameForComparison(a.Name) == normalizedSearch);

            output.AddRange(exactMatches);

            if (!output.Any())
            {
                foreach (var func in AuthorScoringFunctions(title, title.CleanAuthorName()))
                {
                    output.AddRange(FindByStringInexact(authors, func.Item1, func.Item2));
                }
            }

            return output.DistinctBy(x => x.Id).ToList();
        }

        private static string NormalizeAuthorNameForComparison(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            // Full normalization: remove all punctuation and spaces for comparison
            // This ensures "A. F. Kay", "A.F. Kay", "AF Kay", and "A F Kay" all match
            var normalized = new System.Text.StringBuilder();

            foreach (var c in name.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c))
                {
                    normalized.Append(c);
                }
            }

            return normalized.ToString();
        }

        private List<Author> FindByStringInexact(List<Author> authors, Func<Author, string, double> scoreFunction, string title)
        {
            const double fuzzThreshold = 0.8;
            const double fuzzGap = 0.2;

            var sortedAuthors = authors.Select(s => new
            {
                MatchProb = scoreFunction(s, title),
                Author = s
            })
                .ToList()
                .OrderByDescending(s => s.MatchProb)
                .ToList();

            return sortedAuthors.TakeWhile((x, i) => i == 0 || sortedAuthors[i - 1].MatchProb - x.MatchProb < fuzzGap)
                .TakeWhile((x, i) => x.MatchProb > fuzzThreshold || (i > 0 && sortedAuthors[i - 1].MatchProb > fuzzThreshold))
                .Select(x => x.Author)
                .ToList();
        }

        public List<Author> GetAllAuthors(bool bypassCache = false)
        {
            if (bypassCache)
            {
                return _authorRepository.All().ToList();
            }

            return _cache.Get("GetAllAuthors", () => _authorRepository.All().ToList(), TimeSpan.FromSeconds(30));
        }

        public Dictionary<int, List<int>> GetAllAuthorTags()
        {
            return _authorRepository.AllAuthorTags();
        }

        public Dictionary<int, string> AllAuthorPaths()
        {
            return _authorRepository.AllAuthorPaths();
        }

        public List<Author> AllForTag(int tagId)
        {
            return GetAllAuthors().Where(s => s.Tags.Contains(tagId))
                                 .ToList();
        }

	        public Author GetAuthor(int authorId)
	        {
	            var key = authorId.ToString();
	            var cached = _authorByIdCache.Find(key);
	            if (cached != null)
	            {
	                return cached;
	            }

	            var author = _authorRepository.Get(authorId);
	            _authorByIdCache.Set(key, author, AuthorByIdCacheTtl);
	            return author;
	        }


        public List<Author> GetAuthors(IEnumerable<int> authorIds)
        {
            return _authorRepository.Get(authorIds).ToList();
        }

	        public void RemoveAddOptions(Author author)
	        {
	            _authorRepository.SetFields(author, s => s.AddOptions);
	            _cache.Clear();
	            _authorByIdCache.Remove(author.Id.ToString());
	        }

	        public Author UpdateAuthor(Author author)
	        {
	            _cache.Clear();
	            _authorByIdCache.Remove(author.Id.ToString());

	            var storedAuthor = GetAuthor(author.Id);

            // Never update AddOptions when updating an author, keep it the same as the existing stored author.
            author.AddOptions = storedAuthor.AddOptions;

                var rootFolders = _rootFolderService?.All();
                if (rootFolders != null && rootFolders.Count > 0)
                {
                    ValidateRootFolderCompatibilityOrThrow(author, storedAuthor, rootFolders);
                    ApplyRootFolderDefaultsForUnsetMediaTypeSettings(author, rootFolders);
                    ApplySyncMonitoredAcrossFormatsDefaultWhenAuthorBecomesEligible(author, storedAuthor, rootFolders);
                    NormalizeOrValidateSyncMonitoredAcrossFormats(author, storedAuthor, rootFolders);
                }

	            // Keep the legacy aggregate projection consistent with the per-media-type gates.
	            author.Monitored = author.IsMonitoredFromMediaSettings();

	            // Keep per-media-type and primary paths consistent with configured root folders.
	            // Preserve existing relative folder when correcting drift/out-of-root paths.
	            if (_authorPathBuilder != null)
	            {
	                try
	                {
	                    _authorPathBuilder.EnsureAuthorPaths(author, useExistingRelativeFolder: true);
	                }
	                catch (Exception ex)
	                {
	                    _logger.Debug(ex, "Failed to ensure author paths for {0} (ID: {1})", author.Name, author.Id);
	                }
	            }

                EnsureAuthorDbFields(author, ensurePaths: false, requirePath: false);

	            var updatedAuthor = _authorRepository.Update(author);
	            RefreshAuthorProviderAliases(updatedAuthor);
	            _authorByIdCache.Remove(updatedAuthor.Id.ToString());
	            _eventAggregator.PublishEvent(new AuthorEditedEvent(updatedAuthor, storedAuthor));

	            return updatedAuthor;
	        }

        public Author UpdateAuthorProgressiveSettings(Author author, int? audiobookQualityProfileId, int? audiobookMetadataProfileId,
            bool? audiobookMonitored, NewItemMonitorTypes? audiobookMonitorNewItems,
            int? ebookQualityProfileId, int? ebookMetadataProfileId,
            bool? ebookMonitored, NewItemMonitorTypes? ebookMonitorNewItems,
            string rootFolderPath)
        {
            var settingsChanged = false;

            // Apply audiobook settings if author doesn't have them (NULL means inherit).
            // Also fill missing root-folder path/settings if the profile exists but the path is missing.
            if (audiobookQualityProfileId.HasValue && audiobookQualityProfileId.Value > 0)
            {
                if (!author.AudiobookQualityProfileId.HasValue)
                {
                    author.AudiobookQualityProfileId = audiobookQualityProfileId;
                    settingsChanged = true;
                    _logger.Debug("Progressive settings: Added audiobook settings to author {0}", author.Name);
                }

                if (!author.AudiobookMetadataProfileId.HasValue && audiobookMetadataProfileId.HasValue)
                {
                    author.AudiobookMetadataProfileId = audiobookMetadataProfileId;
                    settingsChanged = true;
                }

                if (!author.AudiobookMonitored.HasValue && audiobookMonitored.HasValue)
                {
                    author.AudiobookMonitored = audiobookMonitored;
                    settingsChanged = true;
                }

                if (!author.AudiobookMonitorNewItems.HasValue && audiobookMonitorNewItems.HasValue)
                {
                    author.AudiobookMonitorNewItems = audiobookMonitorNewItems;
                    settingsChanged = true;
                }

                if (author.AudiobookRootFolderPath.IsNullOrWhiteSpace() && !rootFolderPath.IsNullOrWhiteSpace())
                {
                    author.AudiobookRootFolderPath = rootFolderPath;
                    settingsChanged = true;
                    _logger.Debug("Progressive settings: Filled missing audiobook root folder for author {0}", author.Name);
                }
            }

            // Apply ebook settings if author doesn't have them (NULL means inherit).
            // Also fill missing root-folder path/settings if the profile exists but the path is missing.
            if (ebookQualityProfileId.HasValue && ebookQualityProfileId.Value > 0)
            {
                if (!author.EbookQualityProfileId.HasValue)
                {
                    author.EbookQualityProfileId = ebookQualityProfileId;
                    settingsChanged = true;
                    _logger.Debug("Progressive settings: Added ebook settings to author {0}", author.Name);
                }

                if (!author.EbookMetadataProfileId.HasValue && ebookMetadataProfileId.HasValue)
                {
                    author.EbookMetadataProfileId = ebookMetadataProfileId;
                    settingsChanged = true;
                }

                if (!author.EbookMonitored.HasValue && ebookMonitored.HasValue)
                {
                    author.EbookMonitored = ebookMonitored;
                    settingsChanged = true;
                }

                if (!author.EbookMonitorNewItems.HasValue && ebookMonitorNewItems.HasValue)
                {
                    author.EbookMonitorNewItems = ebookMonitorNewItems;
                    settingsChanged = true;
                }

                if (author.EbookRootFolderPath.IsNullOrWhiteSpace() && !rootFolderPath.IsNullOrWhiteSpace())
                {
                    author.EbookRootFolderPath = rootFolderPath;
                    settingsChanged = true;
                    _logger.Debug("Progressive settings: Filled missing ebook root folder for author {0}", author.Name);
                }
            }

            if (settingsChanged)
            {
                return UpdateAuthor(author);
            }

            return author;
        }

	        public List<Author> UpdateAuthors(List<Author> author, bool useExistingRelativeFolder)
	        {
	            _cache.Clear();
	            _authorByIdCache.Clear();
	            _logger.Debug("Updating {0} author", author.Count);

                var rootFolders = _rootFolderService?.All() ?? new List<RootFolder>();
                var storedAuthors = _authorRepository.Get(author.Select(a => a.Id)).ToDictionary(a => a.Id);

	            foreach (var s in author)
	            {
	                _logger.Trace("Updating: {0}", s.Name);

                    if (storedAuthors.TryGetValue(s.Id, out var stored))
                    {
                        ValidateRootFolderCompatibilityOrThrow(s, stored, rootFolders);
                        ApplySyncMonitoredAcrossFormatsDefaultWhenAuthorBecomesEligible(s, stored, rootFolders);
                    }

                    ApplyRootFolderDefaultsForUnsetMediaTypeSettings(s, rootFolders);
                    NormalizeOrValidateSyncMonitoredAcrossFormats(s, storedAuthors.GetValueOrDefault(s.Id), rootFolders);
                    EnsureAuthorDbFields(s, ensurePaths: false, requirePath: false);

	                // Keep legacy Author.Monitored consistent with per-media settings for bulk updates too.
	                s.Monitored = s.IsMonitoredFromMediaSettings();

	                if (!s.AudiobookRootFolderPath.IsNullOrWhiteSpace() || !s.EbookRootFolderPath.IsNullOrWhiteSpace())
	                {
	                    if (_authorPathBuilder != null)
	                    {
	                        _authorPathBuilder.EnsureAuthorPaths(s, useExistingRelativeFolder);
	                    }

	                    _logger.Trace("Changing path for {0} to {1}", s.Name, s.Path);
                }
                else
                {
                    _logger.Trace("Not changing path for: {0}", s.Name);
                }
            }

            _authorRepository.UpdateMany(author);
            RefreshAuthorProviderAliases(author);
            _logger.Debug("{0} authors updated", author.Count);

            return author;
        }

        private static bool RootFolderPathEquals(string a, string b)
        {
            if (a.IsNullOrWhiteSpace() && b.IsNullOrWhiteSpace())
            {
                return true;
            }

            if (a.IsNullOrWhiteSpace() || b.IsNullOrWhiteSpace())
            {
                return false;
            }

            return a.PathEquals(b);
        }

        private static RootFolder GetRootFolderForPath(List<RootFolder> rootFolders, string rootFolderPath)
        {
            if (rootFolders == null || rootFolderPath.IsNullOrWhiteSpace())
            {
                return null;
            }

            return rootFolders.FirstOrDefault(r => r.Path.PathEquals(rootFolderPath));
        }

        private static bool IsCompatibleRootFolder(RootFolder rootFolder, BookMediaType mediaType)
        {
            if (rootFolder == null)
            {
                return false;
            }

            if (rootFolder.FolderType == FolderType.Mixed)
            {
                return true;
            }

            return mediaType == BookMediaType.Audiobook
                ? rootFolder.FolderType == FolderType.Audiobook
                : rootFolder.FolderType == FolderType.Ebook;
        }

        private static void ValidateRootFolderCompatibilityOrThrow(Author author, Author storedAuthor, List<RootFolder> rootFolders)
        {
            if (author == null || storedAuthor == null || rootFolders == null)
            {
                return;
            }

            var failures = new List<ValidationFailure>();

            if (!RootFolderPathEquals(author.AudiobookRootFolderPath, storedAuthor.AudiobookRootFolderPath))
            {
                ValidateRootFolderPath(failures, nameof(Author.AudiobookRootFolderPath), author.AudiobookRootFolderPath, rootFolders, BookMediaType.Audiobook);
            }

            if (!RootFolderPathEquals(author.EbookRootFolderPath, storedAuthor.EbookRootFolderPath))
            {
                ValidateRootFolderPath(failures, nameof(Author.EbookRootFolderPath), author.EbookRootFolderPath, rootFolders, BookMediaType.Ebook);
            }

            if (failures.Count > 0)
            {
                throw new ValidationException(failures);
            }
        }

        private static void ValidateRootFolderPath(List<ValidationFailure> failures, string propertyName, string rootFolderPath, List<RootFolder> rootFolders, BookMediaType mediaType)
        {
            if (failures == null)
            {
                return;
            }

            if (rootFolderPath.IsNullOrWhiteSpace())
            {
                return;
            }

            var rootFolder = GetRootFolderForPath(rootFolders, rootFolderPath);
            if (rootFolder == null)
            {
                failures.Add(new ValidationFailure(propertyName, "Selected root folder is not configured. Add it in Settings → Media Management."));
                return;
            }

            if (!IsCompatibleRootFolder(rootFolder, mediaType))
            {
                var expected = mediaType == BookMediaType.Audiobook ? "audiobooks" : "ebooks";
                failures.Add(new ValidationFailure(propertyName, $"Selected root folder is not configured for {expected}."));
            }
        }

        private static void ApplyRootFolderDefaultsForUnsetMediaTypeSettings(Author author, List<RootFolder> rootFolders)
        {
            if (author == null || rootFolders == null || rootFolders.Count == 0)
            {
                return;
            }

            static bool IsMissingProfileId(int? profileId) => !profileId.HasValue || profileId.Value <= 0;

            if (!author.AudiobookRootFolderPath.IsNullOrWhiteSpace())
            {
                var needsAudiobookDefaults =
                    IsMissingProfileId(author.AudiobookQualityProfileId) ||
                    IsMissingProfileId(author.AudiobookMetadataProfileId) ||
                    !author.AudiobookMonitored.HasValue ||
                    !author.AudiobookMonitorNewItems.HasValue;

                if (needsAudiobookDefaults)
                {
                    var rootFolder = GetRootFolderForPath(rootFolders, author.AudiobookRootFolderPath);
                    var settings = rootFolder?.GetAudiobookSettings();

                    if (settings != null)
                    {
                        if (IsMissingProfileId(author.AudiobookQualityProfileId) && settings.QualityProfileId.HasValue && settings.QualityProfileId.Value > 0)
                        {
                            author.AudiobookQualityProfileId = settings.QualityProfileId;
                        }

                        if (IsMissingProfileId(author.AudiobookMetadataProfileId) && settings.MetadataProfileId.HasValue && settings.MetadataProfileId.Value > 0)
                        {
                            author.AudiobookMetadataProfileId = settings.MetadataProfileId;
                        }

                        if (!author.AudiobookMonitored.HasValue && settings.Monitored.HasValue)
                        {
                            author.AudiobookMonitored = settings.Monitored;
                        }

                        if (!author.AudiobookMonitorNewItems.HasValue && settings.MonitorNewItems.HasValue)
                        {
                            author.AudiobookMonitorNewItems = settings.MonitorNewItems;
                        }
                    }
                }
            }

            if (!author.EbookRootFolderPath.IsNullOrWhiteSpace())
            {
                var needsEbookDefaults =
                    IsMissingProfileId(author.EbookQualityProfileId) ||
                    IsMissingProfileId(author.EbookMetadataProfileId) ||
                    !author.EbookMonitored.HasValue ||
                    !author.EbookMonitorNewItems.HasValue;

                if (needsEbookDefaults)
                {
                    var rootFolder = GetRootFolderForPath(rootFolders, author.EbookRootFolderPath);
                    var settings = rootFolder?.GetEbookSettings();

                    if (settings != null)
                    {
                        if (IsMissingProfileId(author.EbookQualityProfileId) && settings.QualityProfileId.HasValue && settings.QualityProfileId.Value > 0)
                        {
                            author.EbookQualityProfileId = settings.QualityProfileId;
                        }

                        if (IsMissingProfileId(author.EbookMetadataProfileId) && settings.MetadataProfileId.HasValue && settings.MetadataProfileId.Value > 0)
                        {
                            author.EbookMetadataProfileId = settings.MetadataProfileId;
                        }

                        if (!author.EbookMonitored.HasValue && settings.Monitored.HasValue)
                        {
                            author.EbookMonitored = settings.Monitored;
                        }

                        if (!author.EbookMonitorNewItems.HasValue && settings.MonitorNewItems.HasValue)
                        {
                            author.EbookMonitorNewItems = settings.MonitorNewItems;
                        }
                    }
                }
            }
        }

        private static bool HasCompatibleRootFolder(Author author, List<RootFolder> rootFolders, BookMediaType mediaType)
        {
            if (author == null || rootFolders == null || rootFolders.Count == 0)
            {
                return false;
            }

            var rootFolderPath = mediaType == BookMediaType.Audiobook
                ? author.AudiobookRootFolderPath
                : author.EbookRootFolderPath;

            if (rootFolderPath.IsNullOrWhiteSpace())
            {
                return false;
            }

            var rootFolder = GetRootFolderForPath(rootFolders, rootFolderPath);
            return IsCompatibleRootFolder(rootFolder, mediaType);
        }

        private static bool HasSyncMonitoredAcrossFormatsEligibility(Author author, List<RootFolder> rootFolders)
        {
            return HasCompatibleRootFolder(author, rootFolders, BookMediaType.Audiobook) &&
                   HasCompatibleRootFolder(author, rootFolders, BookMediaType.Ebook);
        }

        private void NormalizeOrValidateSyncMonitoredAcrossFormats(Author author, Author storedAuthor, List<RootFolder> rootFolders)
        {
            if (author?.SyncMonitoredAcrossFormats != true)
            {
                return;
            }

            var isEligible = HasSyncMonitoredAcrossFormatsEligibility(author, rootFolders);
            if (isEligible)
            {
                return;
            }

            if (storedAuthor?.SyncMonitoredAcrossFormats == true)
            {
                var wasEligible = HasSyncMonitoredAcrossFormatsEligibility(storedAuthor, rootFolders);
                if (wasEligible)
                {
                    author.SyncMonitoredAcrossFormats = false;
                    _logger.Info("Disabled SyncMonitoredAcrossFormats for author {0} because one of the configured media roots was removed or became incompatible", author.Name);
                }

                return;
            }

            throw new ValidationException(new[]
            {
                new ValidationFailure(nameof(Author.SyncMonitoredAcrossFormats), "Sync monitored across formats requires both audiobook and ebook root folders to be configured for this author.")
            });
        }

        private void ApplySyncMonitoredAcrossFormatsDefaultWhenAuthorBecomesEligible(Author author, Author storedAuthor, List<RootFolder> rootFolders)
        {
            if (author == null || author.SyncMonitoredAcrossFormats.HasValue || rootFolders == null || rootFolders.Count == 0)
            {
                return;
            }

            var wasEligible = HasSyncMonitoredAcrossFormatsEligibility(storedAuthor, rootFolders);
            var isEligible = HasSyncMonitoredAcrossFormatsEligibility(author, rootFolders);

            if (!isEligible || wasEligible)
            {
                return;
            }

            TrySeedSyncMonitoredAcrossFormatsFromRootFolderDefaults(author, rootFolders, "when author became dual-format eligible");
        }

        private void ApplySyncMonitoredAcrossFormatsDefaultOnAdd(Author author, List<RootFolder> rootFolders)
        {
            if (author == null || author.SyncMonitoredAcrossFormats.HasValue || rootFolders == null || rootFolders.Count == 0)
            {
                return;
            }

            if (!HasSyncMonitoredAcrossFormatsEligibility(author, rootFolders))
            {
                return;
            }

            TrySeedSyncMonitoredAcrossFormatsFromRootFolderDefaults(author, rootFolders, "on add");
        }

        private void TrySeedSyncMonitoredAcrossFormatsFromRootFolderDefaults(Author author, List<RootFolder> rootFolders, string context)
        {
            if (author == null || author.SyncMonitoredAcrossFormats.HasValue || rootFolders == null || rootFolders.Count == 0)
            {
                return;
            }

            var explicitDefaults = new List<bool>();

            void TryAddDefault(string rootFolderPath)
            {
                var rootFolder = GetRootFolderForPath(rootFolders, rootFolderPath);
                if (rootFolder?.DefaultSyncMonitoredAcrossFormats is bool syncDefault)
                {
                    explicitDefaults.Add(syncDefault);
                }
            }

            TryAddDefault(author.AudiobookRootFolderPath);
            TryAddDefault(author.EbookRootFolderPath);

            var distinctDefaults = explicitDefaults.Distinct().ToList();
            if (distinctDefaults.Count == 1)
            {
                author.SyncMonitoredAcrossFormats = distinctDefaults[0];
                _logger.Debug("Seeded SyncMonitoredAcrossFormats={0} for author {1} {2}", distinctDefaults[0], author.Name, context);
            }
            else if (distinctDefaults.Count > 1)
            {
                _logger.Warn("Conflicting root folder sync defaults for author {0}; leaving SyncMonitoredAcrossFormats unset", author.Name);
            }
        }


	        public void SetMediaTypeMonitoring(int authorId, string mediaType, bool monitored)
	        {
	            var isAudiobook = string.Equals(mediaType, "audiobook", StringComparison.OrdinalIgnoreCase);
	            var isEbook = string.Equals(mediaType, "ebook", StringComparison.OrdinalIgnoreCase);
	            if (!isAudiobook && !isEbook)
	            {
	                throw new ArgumentException($"Unknown media type '{mediaType}'", nameof(mediaType));
	            }

	            var author = GetAuthor(authorId);
            if (isAudiobook)
            {
                author.AudiobookMonitored = monitored;
            }
            else
            {
                author.EbookMonitored = monitored;
            }

            author.Monitored = author.IsMonitoredFromMediaSettings();
            _authorRepository.Update(author);
	            _cache.Clear();
	            _authorByIdCache.Remove(authorId.ToString());
	            _eventAggregator.PublishEvent(new AuthorEditedEvent(author, author));
	        }

        public void EnsureMediaTypeMonitoring(int authorId, string mediaType)
        {
            var isAudiobook = string.Equals(mediaType, "audiobook", StringComparison.OrdinalIgnoreCase);
            var isEbook = string.Equals(mediaType, "ebook", StringComparison.OrdinalIgnoreCase);
            if (!isAudiobook && !isEbook)
            {
                throw new ArgumentException($"Unknown media type '{mediaType}'", nameof(mediaType));
            }

            var author = GetAuthor(authorId);
            var currentMonitoring = isAudiobook
                ? author.AudiobookMonitored
                : author.EbookMonitored;

            // An explicit book request enables only the named side. The opposite side and
            // all book-row selections remain unchanged.
            if (currentMonitoring == true)
            {
                return;
            }

            if (isAudiobook)
            {
                author.AudiobookMonitored = true;
            }
            else
            {
                author.EbookMonitored = true;
            }

            author.Monitored = author.IsMonitoredFromMediaSettings();
            _authorRepository.Update(author);
            _cache.Clear();
            _authorByIdCache.Remove(authorId.ToString());

            // Do not run root/default reconciliation here: an explicit single-book request
            // must not configure or monitor the opposite media side.
            // Reusing the same snapshot intentionally suppresses diff-driven root/profile reconciliation;
            // the event still refreshes API, statistics, and full-text-search consumers.
            _eventAggregator.PublishEvent(new AuthorEditedEvent(author, author));
        }

        public long GetAuthorSizeForMediaType(int authorId, string mediaType)
        {
            var files = _mediaFileService.GetFilesByAuthor(authorId);
            return files.Where(f => f.MediaType == mediaType).Sum(f => f.Size);
        }

	        public void UpdateLastSelectedMediaType(int authorId, string mediaType)
	        {
	            _authorRepository.UpdateLastSelectedMediaType(authorId, mediaType);
	            _cache.Clear();
	            _authorByIdCache.Remove(authorId.ToString());
	        }

        public List<Book> GetAuthorBooksFromCache(int authorId)
        {
            lock (_authorCacheLock)
            {
                if (_currentAuthorId != authorId || _currentAuthorBooks == null)
                {
                    _logger.Debug("[CACHE-TRACE] AuthorService.GetAuthorBooksFromCache - CACHE MISS for author {0}, loading from database", authorId);
                    var startTime = DateTime.UtcNow;
                    _currentAuthorBooks = _bookRepository.GetBooks(authorId);
                    var loadTime = (DateTime.UtcNow - startTime).TotalMilliseconds;
                    _currentAuthorId = authorId;
                    _logger.Debug("[CACHE-TRACE] AuthorService loaded {0} books for author {1} in {2:F2}ms", _currentAuthorBooks.Count, authorId, loadTime);
                }
                else
                {
                    _logger.Debug("[CACHE-TRACE] AuthorService.GetAuthorBooksFromCache - CACHE HIT for author {0}, returning {1} cached books", authorId, _currentAuthorBooks.Count);
                }
                return _currentAuthorBooks;
            }
        }

        public List<int> GetAuthorIdsByMetadataProfileId(int metadataProfileId)
        {
            return _authorRepository.GetAuthorIdsByMetadataProfileId(metadataProfileId);
        }

        public void ClearAuthorCache()
        {
            lock (_authorCacheLock)
            {
                _logger.Debug("[CACHE-TRACE] AuthorService.ClearAuthorCache - Clearing cache for author {0}", _currentAuthorId);
                _currentAuthorId = -1;
                _currentAuthorBooks = null;
                _logger.Debug("[CACHE-TRACE] AuthorService.ClearAuthorCache - Author book cache cleared successfully");
            }
        }
    }
}
