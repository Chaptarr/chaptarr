using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Net;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Datastore;
using FluentValidation;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.MediaCover.Commands;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.Extras;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MetadataSource;
// using NzbDrone.Core.MediaFiles.BookImport.Identification; // Disabled - old identification system
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser;
using NzbDrone.Core.RootFolders;
using PreparedAuthorName = (System.Collections.Generic.List<string> Tokens, int FirstCount, int LastCount, int CharacterCount, bool HasGivenInitialGroup, bool HasContributorBoundary, string LiteralKey, System.Collections.Generic.IReadOnlyList<int> JoinerIndexes);

namespace NzbDrone.Core.Books
{
    public interface IAuthorService
    {
        Author GetAuthor(int authorId);
        List<Author> GetAuthors(IEnumerable<int> authorIds);
        List<Author> GetExistingAuthors(IEnumerable<int> authorIds);
        Author AddAuthor(Author newAuthor, bool doRefresh);
        List<Author> AddAuthors(List<Author> newAuthors, bool doRefresh);
        Author FindByProviderId(string provider, string providerId);
        Author FindByName(string title);
        List<Author> GetCandidates(string title);
	        void DeleteAuthor(int authorId, bool deleteFiles, bool addImportListExclusion = false);
            // Purge/re-add deliberately retains files, so production must preserve the import
            // history that prevents a still-seeding download from being imported again.
            // The default exists only for lightweight test doubles.
            List<int> DeleteAuthorForReadd(int authorId)
            {
                throw new NotSupportedException();
            }
        void DeleteAuthors(List<int> authorIds, bool deleteFiles, bool addImportListExclusion = false, BookMediaType? mediaType = null, bool applyToBothFormats = false);
        List<Author> GetAllAuthors(bool bypassCache = false);
        Dictionary<int, List<int>> GetAllAuthorTags();
        List<Author> AllForTag(int tagId);
        Author UpdateAuthor(Author author);
        Author UpdateAuthor(Author author, Action<IDbConnection, IDbTransaction> beforeSave);
        Author UpdateAuthorProgressiveSettings(Author author, int? audiobookQualityProfileId, int? audiobookMetadataProfileId,
            bool? audiobookMonitored, NewItemMonitorTypes? audiobookMonitorNewItems,
            int? ebookQualityProfileId, int? ebookMetadataProfileId,
            bool? ebookMonitored, NewItemMonitorTypes? ebookMonitorNewItems,
            string rootFolderPath)
        {
            throw new NotSupportedException();
        }
        List<Author> UpdateAuthors(List<Author> authors, bool useExistingRelativeFolder);
        List<KeyValuePair<int, string>> AllAuthorPaths();
        bool AuthorPathExists(string folder);
        void SetAddOptions(Author author);
        void SetMediaTypeMonitoring(int authorId, string mediaType, bool monitored);
        long GetAuthorSizeForMediaType(int authorId, string mediaType);
        void UpdateLastSelectedMediaType(int authorId, string mediaType);
        List<Book> GetAuthorBooksFromCache(int authorId);
        List<int> GetAuthorIdsByMetadataProfileId(int metadataProfileId);
        void ClearAuthorCache();
    }

    public class AuthorService : IAuthorService
    {
        private readonly IAuthorRepository _authorRepository;
        private readonly IMainDatabase _mainDatabase;
        // Calibre cover resolution depends on AuthorService; resolve deletion only on demand.
        private readonly Lazy<IDeleteMediaFiles> _deleteMediaFiles;
        private readonly IEventAggregator _eventAggregator;
        private readonly IBuildAuthorPaths _authorPathBuilder;
        private readonly IRootFolderService _rootFolderService;
	        private readonly IManageCommandQueue _commandQueueManager;
	        private readonly Logger _logger;
	        private readonly ICached<(List<Author> Authors, Lazy<List<(Author Author, PreparedAuthorName Name)>> Names)> _cache;
	        private readonly ICached<Author> _authorByIdCache;
	        private static readonly TimeSpan AuthorByIdCacheTtl = TimeSpan.FromSeconds(10);
	        private readonly IBookRepository _bookRepository;
	        private readonly IMediaFileService _mediaFileService;
	        private readonly IProviderAliasService _providerAliasService;
        private readonly IImportListExclusionService _importListExclusionService;
        private readonly Lazy<IBookService> _bookService;

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
                             IImportListExclusionService importListExclusionService,
                             Lazy<IDeleteMediaFiles> deleteMediaFiles,
                             Lazy<IBookService> bookService,
                             IMainDatabase mainDatabase,
                             IProviderAliasService providerAliasService = null)
	        {
	            _authorRepository = authorRepository;
                _mainDatabase = mainDatabase;
                _deleteMediaFiles = deleteMediaFiles;
	            _eventAggregator = eventAggregator;
	            _authorPathBuilder = authorPathBuilder;
	            _rootFolderService = rootFolderService;
	            _commandQueueManager = commandQueueManager;
	            // Repeated lookups must not keep an outdated author/name snapshot alive indefinitely.
	            _cache = cacheManager.GetCache<(List<Author> Authors, Lazy<List<(Author Author, PreparedAuthorName Name)>> Names)>(
                    GetType(), "authorcache");
	            _authorByIdCache = cacheManager.GetCache<Author>(GetType(), "authorById");
	            _logger = logger;
	            _bookRepository = bookRepository;
	            _mediaFileService = mediaFileService;
	            _providerAliasService = providerAliasService;
                _importListExclusionService = importListExclusionService;
                _bookService = bookService;
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

        private void RefreshAuthorProviderAliases(IEnumerable<Author> authors, IDbConnection connection = null, IDbTransaction transaction = null)
        {
            if (_providerAliasService == null || authors == null)
            {
                return;
            }

            var rows = authors.Where(author => author != null && author.Id > 0).ToList();
            var missingIds = rows.Where(author => author.RemoteProviderIds == null).Select(author => author.Id).ToList();
            var aliases = (missingIds.Count > 0 ? _providerAliasService.GetAliases("Author", missingIds, "author") : new List<ProviderAlias>())
                .ToLookup(alias => alias.EntityId);
            foreach (var author in rows)
            {
                if (author.RemoteProviderIds == null)
                    author.RemoteProviderIds = aliases[author.Id].Select(alias => $"{alias.Provider}:{alias.NormalizedProviderId}").ToHashSet(StringComparer.OrdinalIgnoreCase);
                var providerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                AddProviderId(providerIds, author.GoodreadsAuthorId);
                AddProviderId(providerIds, author.HardcoverAuthorId);
                AddProviderId(providerIds, author.OpenLibraryAuthorId);
                AddProviderId(providerIds, author.GoogleBooksAuthorId);
                AddProviderId(providerIds, author.AudnexusAuthorId);
                foreach (var providerId in author.RemoteProviderIds) AddProviderId(providerIds, providerId);
                if (connection == null) _providerAliasService.ReplaceAliases("Author", author.Id, "author", providerIds);
                else _providerAliasService.ReplaceAliases("Author", author.Id, "author", providerIds, connection, transaction);
            }
        }

        private void RefreshAuthorProviderAliases(Author author, IDbConnection connection = null, IDbTransaction transaction = null)
        {
            RefreshAuthorProviderAliases(new[] { author }, connection, transaction);
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

            var insertedAuthor = _authorRepository.Insert(newAuthor);
            InvalidateAuthorCatalog();
            _authorByIdCache.Remove(insertedAuthor.Id.ToString());
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

            _authorRepository.InsertMany(newAuthors);
            InvalidateAuthorCatalog();
            _authorByIdCache.Clear();
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
	            DeleteAuthorsInternal(new List<int> { authorId }, deleteFiles, addImportListExclusion, false, null, true);
	        }

	        public List<int> DeleteAuthorForReadd(int authorId)
	        {
	            return DeleteAuthorsInternal(new List<int> { authorId }, false, false, true, null, true);
	        }

	        public void DeleteAuthors(List<int> authorIds, bool deleteFiles, bool addImportListExclusion = false, BookMediaType? mediaType = null, bool applyToBothFormats = false)
	        {
	            DeleteAuthorsInternal(authorIds, deleteFiles, addImportListExclusion, false, mediaType, applyToBothFormats);
	        }

	        private List<int> DeleteAuthorsInternal(
	            List<int> authorIds,
	            bool deleteFiles,
	            bool addImportListExclusion,
	            bool preserveRetainedFileHistory,
                BookMediaType? mediaType,
                bool applyToBothFormats)
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

	            var authors = _authorRepository.Get(distinctIds)
	                .Where(author => author != null)
	                .ToList();

                if (mediaType.HasValue && !Enum.IsDefined(mediaType.Value))
                {
                    throw new NzbDroneClientException(HttpStatusCode.BadRequest, "Media type must be audiobook or ebook.");
                }

                mediaType = applyToBothFormats ? null : mediaType;
                var booksByAuthor = authors.ToDictionary(author => author.Id, author =>
                    mediaType.HasValue || (deleteFiles && !applyToBothFormats)
                        ? _bookRepository.GetBooksByAuthorId(author.Id)
                        : new List<Book>());
                if (deleteFiles && !mediaType.HasValue && !applyToBothFormats)
                {
                    var ambiguous = authors.Where(author =>
                        (author.AudiobookRootFolderPath.IsNotNullOrWhiteSpace() || author.AudiobookPath.IsNotNullOrWhiteSpace() || booksByAuthor[author.Id].Any(book => book.MediaType == BookMediaType.Audiobook)) &&
                        (author.EbookRootFolderPath.IsNotNullOrWhiteSpace() || author.EbookPath.IsNotNullOrWhiteSpace() || booksByAuthor[author.Id].Any(book => book.MediaType == BookMediaType.Ebook))).ToList();
                    if (ambiguous.Any())
                    {
                        throw new NzbDroneClientException(HttpStatusCode.Conflict,
                            "Specify mediaType=audiobook or ebook, use a scoped base URL, or explicitly set applyToBothFormats=true. Ambiguous authors: {0}. Nothing was changed.",
                            string.Join("; ", ambiguous.Select(author => author.Name + " [" + string.Join(", ", ExtraFilePathHelper.GetAuthorBasePaths(author)) + "]")));
                    }
                }

                var bookService = mediaType.HasValue
                    ? _bookService?.Value ?? throw new InvalidOperationException("Book deletion service is unavailable")
                    : null;
                if (addImportListExclusion)
                {
                    // Validate identity before physical work; persistence waits until deletion succeeds.
                    _importListExclusionService.ValidateAuthorExclusions(authors);
                }

                // Delete against one complete selection while ownership still exists. A physical
                // failure must not be swallowed by events and followed by database teardown.
                if (deleteFiles)
                {
                    (_deleteMediaFiles?.Value ?? throw new InvalidOperationException("Author file deletion service is unavailable"))
                        .DeleteAuthorFiles(authors, mediaType);
                }

                if (addImportListExclusion)
                {
                    _importListExclusionService.AddAuthorExclusions(authors, mediaType);
                }

                if (mediaType.HasValue)
                {
                    ClearAuthorCache();
                    bookService.DeleteMany(booksByAuthor.Values.SelectMany(books => books)
                        .Where(book => book.MediaType == mediaType.Value).ToList());
                    var removedAuthors = new List<Author>();
                    foreach (var author in authors)
                    {
                        ClearMediaTypeSettings(author, mediaType.Value);
                        var otherRoot = mediaType == BookMediaType.Audiobook ? author.EbookRootFolderPath : author.AudiobookRootFolderPath;
                        if (otherRoot.IsNotNullOrWhiteSpace() || booksByAuthor[author.Id].Any(book => book.MediaType != mediaType.Value))
                        {
                            author.Path = (mediaType == BookMediaType.Audiobook ? author.EbookPath : author.AudiobookPath) ?? author.Path;
                            author.LastSelectedMediaType = mediaType == BookMediaType.Audiobook ? "ebook" : "audiobook";
                            UpdateAuthor(author);
                        }
                        else
                        {
                            removedAuthors.Add(author);
                        }
                    }

                    // Removing the final side may remove the author record, but never widens file permission.
                    authors = removedAuthors;
                }

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
	            InvalidateAuthorCatalog();
	            foreach (var authorId in distinctIds)
	            {
	                _authorByIdCache.Remove(authorId.ToString());
	            }

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
            return CacheLookupAuthor(GetCandidates(title).ExclusiveOrDefault());
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

        public List<Author> GetCandidates(string name)
        {
            var tokens = ReleaseTitleMatchScorer.TokenizeWithSpans(name);
            var prepared = AuthorNameMatcher.PrepareName(name, tokens);
            if (prepared.Tokens.Count == 0)
            {
                _logger.Debug("Author name lookup: rejected empty name evidence");
                return new List<Author>();
            }

            var stopwatch = Stopwatch.StartNew();
            var authors = GetAuthorCatalog().Names.Value;
            var examined = 0;
            var compared = 0;
            var contained = 0;
            var exact = new List<Author>();
            var compatible = new List<Author>();
            try
            {
                foreach (var entry in authors)
                {
                    examined++;
                    if (AuthorNameMatcher.AuthorNamesExactlyMatch(entry.Name, prepared))
                    {
                        exact.Add(entry.Author);
                    }
                }

                if (exact.Count > 0)
                {
                    return exact;
                }

                foreach (var entry in authors)
                {
                    examined++;
                    compared++;
                    var matches = AuthorNameMatcher.AuthorNamesMatch(entry.Name, prepared);
                    if (!matches)
                    {
                        contained++;
                        matches = AuthorNameMatcher.ContainsAuthorName(entry.Name, name, tokens);
                    }

                    if (matches)
                    {
                        compatible.Add(entry.Author);
                    }
                }
            }
            finally
            {
                _logger.Debug("Author name lookup: examined {0} across {1} cached names, matched {2}, compatibility comparisons {3}, credit comparisons {4}, elapsed {5} ms",
                    examined, authors.Count, exact.Count > 0 ? exact.Count : compatible.Count, compared, contained, stopwatch.ElapsedMilliseconds);
            }

            return compatible;
        }

        private static void ClearMediaTypeSettings(Author author, BookMediaType mediaType)
        {
            author.SyncMonitoredAcrossFormats = false;
            if (mediaType == BookMediaType.Audiobook)
            {
                // NOTE: AudiobookMetadataProfileId is the refresh gate for remote audiobook children.
                // RefreshAuthorService.GetRemoteChildren() treats "no audiobook metadata profile" as "audiobooks disabled"
                // and will otherwise include remote audiobooks again on refresh.
                author.AudiobookMetadataProfileId = null;
                author.AudiobookRootFolderPath = null;
                author.AudiobookPath = null;
                author.AudiobookQualityProfileId = null;
                author.AudiobookMonitored = null;
                author.AudiobookMonitorNewItems = null;
                author.AudiobookSettingsManuallyOverridden = false;
                author.AudiobookTags = new HashSet<int>();
            }
            else
            {
                // NOTE: EbookMetadataProfileId is the refresh gate for remote ebook children.
                author.EbookMetadataProfileId = null;
                author.EbookRootFolderPath = null;
                author.EbookPath = null;
                author.EbookQualityProfileId = null;
                author.EbookMonitored = null;
                author.EbookMonitorNewItems = null;
                author.EbookSettingsManuallyOverridden = false;
                author.EbookTags = new HashSet<int>();
            }
        }

        public List<Author> GetAllAuthors(bool bypassCache = false)
        {
            if (bypassCache)
            {
                return _authorRepository.All().ToList();
            }

            return GetAuthorCatalog().Authors;
        }

        private (List<Author> Authors, Lazy<List<(Author Author, PreparedAuthorName Name)>> Names) GetAuthorCatalog()
        {
            // A completed write must clear a preceding load after that load has finished filling the cache.
            lock (_authorCacheLock)
            {
                return _cache.Get("GetAllAuthors", () =>
                {
                    var authors = _authorRepository.All().ToList();
                    var names = new Lazy<List<(Author Author, PreparedAuthorName Name)>>(() =>
                    {
                        var prepared = authors.Select(author => (author, AuthorNameMatcher.PrepareName(author.Name))).ToList();
                        _logger.Debug("Author name catalog: prepared {0} names", prepared.Count);
                        return prepared;
                    });
                    return (authors, names);
                }, TimeSpan.FromSeconds(30));
            }
        }

        private void InvalidateAuthorCatalog()
        {
            lock (_authorCacheLock)
            {
                _cache.Clear();
            }
        }

        public Dictionary<int, List<int>> GetAllAuthorTags()
        {
            return _authorRepository.AllAuthorTags();
        }

        public List<KeyValuePair<int, string>> AllAuthorPaths()
        {
            return _authorRepository.AllAuthorPaths()
                .SelectMany(author => ExtraFilePathHelper.GetAuthorBasePaths(author)
                    .Select(path => new KeyValuePair<int, string>(author.Id, path)))
                .ToList();
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


        public List<Author> GetExistingAuthors(IEnumerable<int> authorIds) => _authorRepository.FindExisting(authorIds);

        public List<Author> GetAuthors(IEnumerable<int> authorIds)
        {
            return _authorRepository.Get(authorIds).ToList();
        }

	        public void SetAddOptions(Author author)
	        {
	            _authorRepository.SetFields(author, s => s.AddOptions);
	            InvalidateAuthorCatalog();
	            _authorByIdCache.Remove(author.Id.ToString());
	        }

        public Author UpdateAuthor(Author author) => UpdateAuthor(author, null);

        public Author UpdateAuthor(Author author, Action<IDbConnection, IDbTransaction> beforeSave)
        {
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

            Author updatedAuthor;
            if (beforeSave == null)
            {
                updatedAuthor = _authorRepository.Update(author);
                InvalidateAuthorCatalog();
                _authorByIdCache.Remove(updatedAuthor.Id.ToString());
                RefreshAuthorProviderAliases(updatedAuthor);
            }
            else
            {
                // Author consolidation must save ownership and aliases together before notifying readers.
                using var connection = _mainDatabase.OpenConnection();
                using var transaction = connection.BeginTransaction();
                beforeSave(connection, transaction);
                updatedAuthor = _authorRepository.Update(author, connection, transaction);
                RefreshAuthorProviderAliases(updatedAuthor, connection, transaction);
                transaction.Commit();
                InvalidateAuthorCatalog();
                _authorByIdCache.Clear();
            }
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
                if (IsMissingProfileId(author.AudiobookQualityProfileId))
                {
                    author.AudiobookQualityProfileId = audiobookQualityProfileId;
                    settingsChanged = true;
                    _logger.Debug("Progressive settings: Added audiobook settings to author {0}", author.Name);
                }

                if (IsMissingProfileId(author.AudiobookMetadataProfileId) && audiobookMetadataProfileId.HasValue)
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
                if (IsMissingProfileId(author.EbookQualityProfileId))
                {
                    author.EbookQualityProfileId = ebookQualityProfileId;
                    settingsChanged = true;
                    _logger.Debug("Progressive settings: Added ebook settings to author {0}", author.Name);
                }

                if (IsMissingProfileId(author.EbookMetadataProfileId) && ebookMetadataProfileId.HasValue)
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
            InvalidateAuthorCatalog();
            _authorByIdCache.Clear();
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

        private static bool IsMissingProfileId(int? profileId) => !profileId.HasValue || profileId.Value <= 0;

        private static void ApplyRootFolderDefaultsForUnsetMediaTypeSettings(Author author, List<RootFolder> rootFolders)
        {
            if (author == null || rootFolders == null || rootFolders.Count == 0)
            {
                return;
            }

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
	            InvalidateAuthorCatalog();
	            _authorByIdCache.Remove(authorId.ToString());
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
	            InvalidateAuthorCatalog();
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
