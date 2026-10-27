using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Profiles.Releases;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.Profiles.Metadata
{
    public interface IMetadataProfileService
    {
        MetadataProfile Add(MetadataProfile profile);
        void Update(MetadataProfile profile);
        void Delete(int id);
        List<MetadataProfile> All();
        MetadataProfile Get(int id);
        bool Exists(int id, MetadataProfileType? profileType = null);
        List<Book> FilterBooks(Author input, int profileId);
        List<Book> FilterBooks(Author input, int profileId, IReadOnlyDictionary<BookMediaType, IReadOnlySet<string>> requestedNarrationIds);
        List<Book> FilterBooks(Author input, int profileId, IReadOnlyDictionary<BookMediaType, IReadOnlySet<string>> requestedNarrationIds, IReadOnlyDictionary<BookMediaType, IReadOnlySet<string>> requestedNarrationWorkIds);
    }

    public class MetadataProfileService : IMetadataProfileService, IHandle<ApplicationStartedEvent>
    {
        public const string NONE_PROFILE_NAME = "None";
        public const double NONE_PROFILE_MIN_POPULARITY = 1e10;

        private static readonly Regex PartOrSetRegex = new Regex(@"(?<from>\d+) of (?<to>\d+)|(?<from>\d+)\s?/\s?(?<to>\d+)|(?<from>\d+)\s?-\s?(?<to>\d+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly IMetadataProfileRepository _profileRepository;
        private readonly IAuthorService _authorService;
        private readonly IBookService _bookService;
        private readonly IEditionService _editionService;
        private readonly IMediaFileService _mediaFileService;
        private readonly IImportListFactory _importListFactory;
        private readonly IRootFolderService _rootFolderService;
        private readonly ITermMatcherService _termMatcherService;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;

        public MetadataProfileService(IMetadataProfileRepository profileRepository,
                                      IAuthorService authorService,
                                      IBookService bookService,
                                      IEditionService editionService,
                                      IMediaFileService mediaFileService,
                                      IImportListFactory importListFactory,
                                      IRootFolderService rootFolderService,
                                      ITermMatcherService termMatcherService,
                                      IEventAggregator eventAggregator,
                                      Logger logger)
        {
            _profileRepository = profileRepository;
            _authorService = authorService;
            _bookService = bookService;
            _editionService = editionService;
            _mediaFileService = mediaFileService;
            _importListFactory = importListFactory;
            _rootFolderService = rootFolderService;
            _termMatcherService = termMatcherService;
            _eventAggregator = eventAggregator;
            _logger = logger;
        }

        public MetadataProfile Add(MetadataProfile profile)
        {
            return _profileRepository.Insert(profile);
        }

        public void Update(MetadataProfile profile)
        {
            if (profile.Name == NONE_PROFILE_NAME)
            {
                throw new InvalidOperationException("Not permitted to alter None metadata profile");
            }

            var previousProfile = profile.Id > 0 ? _profileRepository.Find(profile.Id) : null;

            _profileRepository.Update(profile);

            // Publish event so book monitoring can be updated
            _eventAggregator.PublishEvent(new Events.MetadataProfileUpdatedEvent(profile, previousProfile));
        }

        public void Delete(int id)
        {
            var profile = _profileRepository.Get(id);

            if (profile.Name == NONE_PROFILE_NAME ||
                _authorService.GetAllAuthors().Any(c =>
                    c.MetadataProfileId == id ||
                    c.AudiobookMetadataProfileId == id ||
                    c.EbookMetadataProfileId == id) ||
                _importListFactory.All().Any(c => c.MetadataProfileId == id || c.Settings is DualMediaImportListSettings settings &&
                    (settings.AudiobookMetadataProfileId == id || settings.EbookMetadataProfileId == id)) ||
                _rootFolderService.All().Any(r =>
                    (r.GetAudiobookSettings()?.MetadataProfileId == id) ||
                    (r.GetEbookSettings()?.MetadataProfileId == id)) ||
                IsLastProfileOfType(profile))
            {
                throw new MetadataProfileInUseException(profile.Name);
            }

            _profileRepository.Delete(id);
        }

        private bool IsLastProfileOfType(MetadataProfile profile)
        {
            // Check if this is the only remaining profile of its type
            // Use Any() for performance - short-circuits and avoids materializing list
            return !_profileRepository.All()
                .Where(p => p.ProfileType == profile.ProfileType && p.Id != profile.Id)
                .Any();
        }

        public List<MetadataProfile> All()
        {
            return _profileRepository.All().ToList();
        }

        public MetadataProfile Get(int id)
        {
            return _profileRepository.Get(id);
        }

        public bool Exists(int id, MetadataProfileType? profileType = null)
        {
            if (!profileType.HasValue) return _profileRepository.Exists(id);

            var profile = _profileRepository.Find(id);
            return profile != null && (profile.ProfileType == MetadataProfileType.General || profile.ProfileType == profileType.Value);
        }

        public List<Book> FilterBooks(Author input, int profileId) => FilterBooks(input, profileId, null);

        public List<Book> FilterBooks(Author input, int profileId, IReadOnlyDictionary<BookMediaType, IReadOnlySet<string>> requestedNarrationIds)
            => FilterBooks(input, profileId, requestedNarrationIds, null);

        public List<Book> FilterBooks(Author input, int profileId, IReadOnlyDictionary<BookMediaType, IReadOnlySet<string>> requestedNarrationIds, IReadOnlyDictionary<BookMediaType, IReadOnlySet<string>> requestedNarrationWorkIds)
        {
            // Defensive null checks to prevent crashes
            if (input == null)
            {
                _logger.Warn("FilterBooks called with null author input");
                return new List<Book>();
            }

            // Missing profiles should not break refresh. If the profile was deleted or never existed,
            // treat it as "no filtering" rather than throwing.
            if (profileId <= 0 || !Exists(profileId))
            {
                _logger.Warn("Metadata profile with ID {0} does not exist, skipping metadata filtering for author '{1}'",
                    profileId,
                    input.Name ?? input.Id.ToString());
                return input.Books ?? new List<Book>();
            }

            // V5 series objects intentionally have no local book links yet. Profile filtering uses
            // the provider relationships already attached to each remote book instead.
            var seriesLinks = (input.Books ?? new List<Book>())
                .Where(x => x?.SeriesLinks?.Any() == true)
                .ToDictionary(x => x, x => x.SeriesLinks, BookReferenceComparer.Instance);

            // Use the local database ID that was passed in
            Author dbAuthor = null;
            if (input.Id > 0)
            {
                dbAuthor = _authorService.GetAuthor(input.Id);
            }
            else
            {
                // Fallback to provider IDs only if no local ID provided
                if (!string.IsNullOrEmpty(input.GoodreadsAuthorId))
                {
                    dbAuthor = _authorService.FindByProviderId("gr", input.GoodreadsAuthorId);
                }
                else if (!string.IsNullOrEmpty(input.HardcoverAuthorId))
                {
                    dbAuthor = _authorService.FindByProviderId("hc", input.HardcoverAuthorId);
                }
                else if (!string.IsNullOrEmpty(input.OpenLibraryAuthorId))
                {
                    dbAuthor = _authorService.FindByProviderId("ol", input.OpenLibraryAuthorId);
                }
            }

            var localBooks = new List<Book>();
            if (dbAuthor != null)
            {
                localBooks = _bookService.GetBooksByAuthor(dbAuthor.Id);
                var editions = _editionService.GetEditionsByAuthor(dbAuthor.Id).GroupBy(x => x.BookId).ToDictionary(x => x.Key, y => y.ToList());

                foreach (var book in localBooks)
                {
                    if (editions.TryGetValue(book.Id, out var bookEditions))
                    {
                        book.Editions = bookEditions;
                    }
                    else
                    {
                        book.Editions = new List<Edition>();
                    }
                }
            }

            var localFiles = _mediaFileService.GetFilesByAuthor(dbAuthor?.Id ?? 0);

            // Ensure input.Books is not null
            var booksToFilter = input.Books ?? new List<Book>();
            return FilterBooks(booksToFilter, localBooks, localFiles, seriesLinks, profileId, requestedNarrationIds, requestedNarrationWorkIds);
        }

        private List<Book> FilterBooks(IEnumerable<Book> remoteBooks, List<Book> localBooks, List<BookFile> localFiles, Dictionary<Book, List<SeriesBookLink>> seriesLinks, int metadataProfileId, IReadOnlyDictionary<BookMediaType, IReadOnlySet<string>> requestedNarrationIds, IReadOnlyDictionary<BookMediaType, IReadOnlySet<string>> requestedNarrationWorkIds)
        {
            requestedNarrationIds = requestedNarrationIds?.ToDictionary(pair => pair.Key, pair => EditionMetadataProfileFilter.NormalizeProtectionIds(pair.Value));
            var profile = Get(metadataProfileId);

            _logger.Trace($"Filtering:\n{remoteBooks.Select(x => x.ToString()).Join("\n")}");

                var hash = new HashSet<Book>(remoteBooks, BookReferenceComparer.Instance);
                var titles = new HashSet<string>(remoteBooks.Select(x => x.Title));

                var remoteBookKeysByEditionToken = BuildRemoteBookKeysByEditionToken(hash);
                var protectedFilesByBook = GetProtectedFilesByBook(localFiles, remoteBookKeysByEditionToken);

                var localHash = new HashSet<string>(
                    localBooks
                        .Where(BookIdentity.HasUserPreservationMarker)
                        .Select(GetBookKey));

                localHash.UnionWith(protectedFilesByBook.Select(group => group.Key));

                // Explicit edition intent preserves its same-media parent even when that edition is unavailable.
                var requestedBooks = new HashSet<Book>(hash.Where(book =>
                    ((requestedNarrationWorkIds?.Count > 0 && BookIdentity.GetStableWorkProviderIdentityTokens(book).Any(id => requestedNarrationWorkIds.GetValueOrDefault(book.MediaType)?.Contains(id) == true)) ||
                     (requestedNarrationIds?.Count > 0 && EditionMetadataProfileFilter.ResolveProtectedEditions(book.Editions, null, requestedNarrationIds.GetValueOrDefault(book.MediaType)).Count > 0))), BookReferenceComparer.Instance);
                bool IsProtectedBook(Book book) => book.AddOptions?.AddType == BookAddType.Manual || requestedBooks.Contains(book) || localHash.Contains(GetBookKey(book));

                var localEditionsByBook = localBooks.ToLookup(book => (GetBookProviderId(book), book.MediaType));
                FilterByPredicate(hash, IsProtectedBook, profile, BookAllowedByRating, "rating criteria not met");
                FilterByPredicate(hash, IsProtectedBook, profile, (x, p) => !p.SkipMissingDate || x.ReleaseDate.HasValue, "release date is missing");
                FilterByPredicate(hash, IsProtectedBook, profile, (x, p) => !p.SkipPartsAndSets || !IsPartOrSet(x, seriesLinks.GetValueOrDefault(x), titles), "book is part of set");
                FilterByPredicate(hash, IsProtectedBook, profile, (x, p) => !p.SkipSeriesSecondary || !seriesLinks.ContainsKey(x) || seriesLinks[x].Any(y => y.IsPrimary != false), "book is a secondary series item");
                FilterByPredicate(hash, IsProtectedBook, profile, OmnibusAllowedByProfile, "book is an omnibus/collection");
                FilterByPredicate(hash, IsProtectedBook, profile, (x, p) => !p.Ignored.Any(i => MatchesTerms(x.Title, i)), "contains ignored terms");

                foreach (var book in hash)
                {
                    var bookProviderId = GetBookProviderId(book);

                    // Metadata profile rules must not bleed across media types (audiobook vs ebook).
                    var matchingLocalBooks = localEditionsByBook[(bookProviderId, book.MediaType)].ToList();
                    var localEditions = matchingLocalBooks
                        .SelectMany(x => x.Editions ?? Enumerable.Empty<Edition>())
                        .ToList();

                    var originalEditions = book.Editions;
                    var filteredEditions = FilterEditions(originalEditions, localEditions, protectedFilesByBook[GetBookKey(book)].ToList(), profile, book.MediaType, requestedNarrationIds?.GetValueOrDefault(book.MediaType));
                    book.Editions = book.AddOptions?.AddType == BookAddType.Manual || matchingLocalBooks.Any(local => local.AddOptions?.AddType == BookAddType.Manual)
                        ? EditionSelector.EnsureWorkRepresentative(book.MediaType, originalEditions, filteredEditions)
                        : filteredEditions;
                }

                FilterByPredicate(hash, IsProtectedBook, profile, (x, p) => x.Editions.Any(e => e.PageCount >= p.MinPages) || x.Editions.All(e => e.PageCount == 0), "minimum page count not met");
                FilterByPredicate(hash, IsProtectedBook, profile, (x, p) => x.Editions.Any(), "all editions filtered out");

                return hash.ToList();
            }

        private List<Edition> FilterEditions(IEnumerable<Edition> editions, List<Edition> localEditions, List<BookFile> localFiles, MetadataProfile profile, BookMediaType mediaType, IReadOnlySet<string> requestedNarrationIds)
        {
            EditionMetadataProfileFilter.ParseAllowedLanguages(
                profile.AllowedLanguages,
                out var allowedLanguages,
                out var allowUnknownLanguage,
                out var languageFilterConfigured,
                out var unknownTokens);

            if (profile.AllowedLanguages.IsNotNullOrWhiteSpace())
            {
                if (unknownTokens.Any())
                {
                    _logger.Warn("Ignoring unknown AllowedLanguages token(s) in metadata profile '{0}': {1}", profile.Name, string.Join(", ", unknownTokens));
                }

                if (languageFilterConfigured)
                {
                    _logger.Debug("Language filtering enabled for metadata profile '{0}'. Allowed=[{1}] AllowUnknown={2}",
                        profile.Name,
                        string.Join(", ", allowedLanguages),
                        allowUnknownLanguage);
                }
                else
                {
                    _logger.Warn("AllowedLanguages is configured for metadata profile '{0}', but no valid language tokens were parsed. Disabling language filtering.", profile.Name);
                }
            }
            else
            {
                _logger.Debug("No language filtering configured - all languages allowed");
            }

            var hash = new HashSet<Edition>(editions, ReferenceEqualityComparer.Instance);

            var localProtectedEditions = localEditions.Where(x => x.ManualAdd).Concat(localFiles.Select(file => file.Edition));
            var protectedEditions = EditionMetadataProfileFilter.ResolveProtectedEditions(hash, localProtectedEditions,
                requestedNarrationIds, _logger);

            // Language filtering logic: only filter if languages are explicitly configured
            FilterByPredicate(hash, x => EditionMetadataProfileFilter.IsProtectedEdition(x, protectedEditions), profile, (x, p) => EditionMetadataProfileFilter.IsAllowedLanguage(x, allowedLanguages, allowUnknownLanguage, languageFilterConfigured), "edition language not allowed");
            FilterByPredicate(hash, x => EditionMetadataProfileFilter.IsProtectedEdition(x, protectedEditions), profile, EditionMetadataProfileFilter.MeetsIdentifierRequirements, "missing required identifier(s)");
            FilterByPredicate(hash, x => EditionMetadataProfileFilter.IsProtectedEdition(x, protectedEditions), profile, (x, p) => !p.Ignored.Any(i => MatchesTerms(x.Title, i)), "contains ignored terms");

            return hash.ToList();
        }


        private static string GetBookProviderId(Book book)
        {
            return BookEditionIdentity.GetCanonicalWorkProviderIds(book).FirstOrDefault()
                   ?? book?.GoodreadsBookId
                   ?? book?.Id.ToString();
        }

        private static bool HasCanonicalWorkProviderId(Book book)
        {
            return BookEditionIdentity.GetCanonicalWorkProviderIds(book).Any();
        }

        private static string GetBookKey(Book book)
        {
            // Provider IDs are shared across audiobook/ebook instances, so include MediaType to avoid cross-filtering.
            return $"{GetBookProviderId(book)}::{book.MediaType}";
        }

        private Dictionary<string, HashSet<string>> BuildRemoteBookKeysByEditionToken(IEnumerable<Book> remoteBooks)
        {
            var index = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var book in remoteBooks ?? Enumerable.Empty<Book>())
            {
                if (book == null)
                {
                    continue;
                }

                if (!HasCanonicalWorkProviderId(book))
                {
                    continue;
                }

                var bookKey = GetBookKey(book);
                foreach (var edition in book.Editions ?? Enumerable.Empty<Edition>())
                {
                    foreach (var token in BookEditionIdentity.GetRemoteEditionRehomeTokens(edition))
                    {
                        AddIndexValue(index, token, bookKey);
                    }
                }
            }

            return index;
        }

        private ILookup<string, BookFile> GetProtectedFilesByBook(IEnumerable<BookFile> localFiles, Dictionary<string, HashSet<string>> remoteBookKeysByEditionToken)
        {
            var protectedFiles = new List<(string BookKey, BookFile File)>();

            foreach (var file in localFiles ?? Enumerable.Empty<BookFile>())
            {
                var storedBookKey = file?.Edition?.Book == null ? null : GetBookKey(file.Edition.Book);
                if (storedBookKey != null)
                {
                    protectedFiles.Add((storedBookKey, file));
                }

                if (!TryGetFileMediaType(file, out var mediaType))
                {
                    continue;
                }

                var tokens = BookEditionIdentity.GetEditionRehomeTokens(file.Edition);
                var keys = ResolveMediaScopedKeys(tokens, remoteBookKeysByEditionToken, mediaType);
                if (keys.Count == 1)
                {
                    var remoteBookKey = keys.First();
                    if (!string.Equals(storedBookKey, remoteBookKey, StringComparison.OrdinalIgnoreCase))
                    {
                        protectedFiles.Add((remoteBookKey, file));
                    }
                }
                else if (keys.Count > 1)
                {
                    _logger.Warn("Skipping metadata-profile owned-file protection for file '{0}': edition tokens [{1}] point to multiple remote book pockets [{2}] for mediaType={3}.",
                        file.Path,
                        string.Join(", ", tokens),
                        string.Join(", ", keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)),
                        mediaType);
                }
            }

            return protectedFiles.ToLookup(item => item.BookKey, item => item.File, StringComparer.OrdinalIgnoreCase);
        }

        private static HashSet<string> ResolveMediaScopedKeys(IEnumerable<string> tokens, Dictionary<string, HashSet<string>> index, BookMediaType mediaType)
        {
            return ResolveKeys(tokens, index)
                .Where(key => RemoteKeyMatchesMediaType(key, mediaType))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private static HashSet<string> ResolveKeys(IEnumerable<string> tokens, Dictionary<string, HashSet<string>> index)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (index == null)
            {
                return keys;
            }

            foreach (var token in tokens ?? Enumerable.Empty<string>())
            {
                if (index.TryGetValue(token, out var values))
                {
                    keys.UnionWith(values);
                }
            }

            return keys;
        }

        private static bool RemoteKeyMatchesMediaType(string remoteKey, BookMediaType mediaType)
        {
            return !string.IsNullOrWhiteSpace(remoteKey) &&
                   remoteKey.EndsWith($"::{mediaType}", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryGetFileMediaType(BookFile file, out BookMediaType mediaType)
        {
            mediaType = BookMediaType.Audiobook;

            if (file?.Edition?.Book != null)
            {
                mediaType = file.Edition.Book.MediaType;
                return true;
            }

            if (string.Equals(file?.MediaType, "ebook", StringComparison.OrdinalIgnoreCase))
            {
                mediaType = BookMediaType.Ebook;
                return true;
            }

            if (string.Equals(file?.MediaType, "audiobook", StringComparison.OrdinalIgnoreCase))
            {
                mediaType = BookMediaType.Audiobook;
                return true;
            }

            return false;
        }

        private static void AddIndexValue(Dictionary<string, HashSet<string>> index, string token, string value)
        {
            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (!index.TryGetValue(token, out var values))
            {
                values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                index[token] = values;
            }

            values.Add(value);
        }

        private void FilterByPredicate<T>(HashSet<T> remoteItems, Func<T, bool> isProtected, MetadataProfile profile, Func<T, MetadataProfile, bool> bookAllowed, string message)
        {
            // Performance optimization: process in batches for large collections
            const int batchSize = 1000;
            var totalItems = remoteItems.Count;

            if (totalItems > batchSize)
            {
                _logger.Debug($"Processing {totalItems} {typeof(T).Name} items in batches of {batchSize} for {message}");

                var itemsList = remoteItems.ToList();
                var allFiltered = new HashSet<T>(remoteItems.Comparer);

                for (var i = 0; i < itemsList.Count; i += batchSize)
                {
                    var batch = itemsList.Skip(i).Take(batchSize);
                    var batchFiltered = batch.Where(x => !bookAllowed(x, profile) && !isProtected(x)).ToList();

                    foreach (var item in batchFiltered)
                    {
                        allFiltered.Add(item);
                    }

                    // Log progress for very large collections
                    if (totalItems > 5000 && i % (batchSize * 5) == 0)
                    {
                        _logger.Debug($"Processed {Math.Min(i + batchSize, totalItems)}/{totalItems} items for {message}");
                    }
                }

                if (allFiltered.Any())
                {
                    _logger.Debug($"Skipping {allFiltered.Count}/{totalItems} {typeof(T).Name} because {message}");
                    remoteItems.RemoveWhere(x => allFiltered.Contains(x));
                }
            }
            else
            {
                // Original logic for smaller collections
                var filtered = new HashSet<T>(remoteItems.Where(x => !bookAllowed(x, profile) && !isProtected(x)), remoteItems.Comparer);
                if (filtered.Any())
                {
                    _logger.Trace($"Skipping {filtered.Count} {typeof(T).Name} because {message}:\n{filtered.ConcatToString(x => x.ToString(), "\n")}");
                    remoteItems.RemoveWhere(x => filtered.Contains(x));
                }
            }
        }

        private bool BookAllowedByRating(Book b, MetadataProfile p)
        {
            // hack for the 'none' metadata profile
            if (p.MinPopularity == NONE_PROFILE_MIN_POPULARITY)
            {
                return false;
            }

            return (b.Ratings.Popularity >= p.MinPopularity) || b.ReleaseDate > DateTime.UtcNow;
        }

        private static bool OmnibusAllowedByProfile(Book book, MetadataProfile profile)
        {
            if (!book.IsOmnibus)
            {
                return true;
            }

            if (profile.SkipOmnibus)
            {
                return false;
            }

            return !profile.SkipMissingIdentifierOmnibus || BookHasIdentifier(book);
        }

        private static bool BookHasIdentifier(Book book)
        {
            return (book.Editions ?? Enumerable.Empty<Edition>())
                .Any(e =>
                    e.Isbn13.IsNotNullOrWhiteSpace() ||
                    e.Isbn10.IsNotNullOrWhiteSpace() ||
                    e.Asin.IsNotNullOrWhiteSpace() ||
                    (e.Asins != null && e.Asins.Any(a => a.IsNotNullOrWhiteSpace())));
        }

        private bool IsPartOrSet(Book book, List<SeriesBookLink> seriesLinks, HashSet<string> titles)
        {
            if (seriesLinks != null &&
                seriesLinks.Any(x => x.Position.IsNotNullOrWhiteSpace()) &&
                !seriesLinks.Any(s => IsNonBundleSeriesPosition(s.Position)))
            {
                // No non-empty series entries parse to a number, so all like 1-3 etc.
                return true;
            }

            // Skip things of form Title1 / Title2 when Title1 and Title2 are already in the list
            var bookTitles = new[] { book.Title }.Concat(book.Editions.Select(x => x.Title)).ToList();
            foreach (var title in bookTitles)
            {
                var split = title.Split('/').Select(x => x.Trim()).ToList();
                if (split.Count() > 1 && split.All(x => titles.Contains(x)))
                {
                    return true;
                }
            }

            var match = PartOrSetRegex.Match(book.Title);

            if (match.Groups["from"].Success)
            {
                if (int.TryParse(match.Groups["from"].Value, out var from))
                {
                    return from <= 1800 || from > DateTime.UtcNow.Year;
                }

                // If parsing fails, assume it's a part/set indicator
                return true;
            }

            return false;
        }

        private static bool IsNonBundleSeriesPosition(string position)
        {
            if (double.TryParse(position, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                return true;
            }

            var slots = (position ?? string.Empty)
                .Split(',')
                .Select(x => x.Trim())
                .ToList();

            // Decimal comma-lists are multiple exact slots, not integer bundle positions.
            return slots.Count > 1 &&
                   slots.Any(x => x.Contains('.')) &&
                   slots.All(x => double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out _));
        }

        private bool MatchesTerms(string value, string terms)
        {
            var foundTerms = EditionMetadataProfileFilter.FindMatchingIgnoredTerms(
                EditionMetadataProfileFilter.ExpandIgnoredTerms(new[] { terms }),
                value,
                _termMatcherService.IsMatch);

            // Debug logging for exclusion matching
            if (foundTerms.Any())
            {
                _logger.Debug($"Excluded '{value}' because it matches ignored terms: [{string.Join(", ", foundTerms)}]");
            }

            return foundTerms.Any();
        }

        public void Handle(ApplicationStartedEvent message)
        {
            var profiles = All();

            // Name is a unique property
            var emptyProfile = profiles.FirstOrDefault(x => x.Name == NONE_PROFILE_NAME);

            // make sure empty profile exists and is actually empty
            // TODO: reinstate
            if (emptyProfile != null &&
                emptyProfile.MinPopularity == NONE_PROFILE_MIN_POPULARITY)
            {
                return;
            }

            if (!profiles.Any())
            {
                _logger.Info("Setting up standard metadata profile");

                Add(new MetadataProfile
                {
                    Name = "Standard",
                    MinPopularity = 350,
                    SkipMissingDate = true,
                    SkipPartsAndSets = true,
                    AllowedLanguages = "eng, null"
                });
            }

            if (emptyProfile != null)
            {
                // emptyProfile is not the correct empty profile - move it out of the way
                _logger.Info($"Renaming non-empty metadata profile {emptyProfile.Name}");

                var names = profiles.Select(x => x.Name).ToList();

                var i = 1;
                emptyProfile.Name = $"{NONE_PROFILE_NAME}.{i}";

                while (names.Contains(emptyProfile.Name))
                {
                    i++;
                }

                _profileRepository.Update(emptyProfile);
            }

            _logger.Info("Setting up empty metadata profile");

            Add(new MetadataProfile
            {
                Name = NONE_PROFILE_NAME,
                MinPopularity = NONE_PROFILE_MIN_POPULARITY
            });
        }
    }
}
