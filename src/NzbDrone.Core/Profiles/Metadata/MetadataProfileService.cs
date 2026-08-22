using System;
using System.Collections.Generic;
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
        bool Exists(int id);
        List<Book> FilterBooks(Author input, int profileId);
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
                _importListFactory.All().Any(c => c.MetadataProfileId == id) ||
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

        public bool Exists(int id)
        {
            return _profileRepository.Exists(id);
        }

        public List<Book> FilterBooks(Author input, int profileId)
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

            var seriesLinks = (input.Series ?? new List<Series>())
                .Where(x => x.LinkItems != null)
                .SelectMany(x => x.LinkItems)
                .Where(x => x.Book?.Value != null)
                .GroupBy(x => x.Book.Value)
                .ToDictionary(x => x.Key, y => y.ToList());

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
            return FilterBooks(booksToFilter, localBooks, localFiles, seriesLinks, profileId);
        }

        private List<Book> FilterBooks(IEnumerable<Book> remoteBooks, List<Book> localBooks, List<BookFile> localFiles, Dictionary<Book, List<SeriesBookLink>> seriesLinks, int metadataProfileId)
        {
            var profile = Get(metadataProfileId);

            _logger.Trace($"Filtering:\n{remoteBooks.Select(x => x.ToString()).Join("\n")}");

                var hash = new HashSet<Book>(remoteBooks);
                var titles = new HashSet<string>(remoteBooks.Select(x => x.Title));

                var remoteBookKeysByEditionToken = BuildRemoteBookKeysByEditionToken(hash);

                var localHash = new HashSet<string>(
                    localBooks
                        .Where(x => x.AddOptions?.AddType == BookAddType.Manual)
                        .Select(GetBookKey));

                localHash.UnionWith(localFiles
                    .Select(x => x.Edition?.Book)
                    .Where(x => x != null)
                    .Select(GetBookKey));

                localHash.UnionWith(GetProtectedBookKeysFromLocalFiles(localFiles, remoteBookKeysByEditionToken));

                FilterByPredicate(hash, GetBookKey, localHash, profile, BookAllowedByRating, "rating criteria not met");
                FilterByPredicate(hash, GetBookKey, localHash, profile, (x, p) => !p.SkipMissingDate || x.ReleaseDate.HasValue, "release date is missing");
                FilterByPredicate(hash, GetBookKey, localHash, profile, (x, p) => !p.SkipPartsAndSets || !IsPartOrSet(x, seriesLinks.GetValueOrDefault(x), titles), "book is part of set");
                FilterByPredicate(hash, GetBookKey, localHash, profile, (x, p) => !p.SkipSeriesSecondary || !seriesLinks.ContainsKey(x) || seriesLinks[x].Any(y => y.IsPrimary), "book is a secondary series item");
                FilterByPredicate(hash, GetBookKey, localHash, profile, OmnibusAllowedByProfile, "book is an omnibus/collection");
                FilterByPredicate(hash, GetBookKey, localHash, profile, (x, p) => !p.Ignored.Any(i => MatchesTerms(x.Title, i)), "contains ignored terms");

                foreach (var book in hash)
                {
                    var bookProviderId = GetBookProviderId(book);

                    // Metadata profile rules must not bleed across media types (audiobook vs ebook).
                    var localEditions = localBooks
                        .Where(x => GetBookProviderId(x) == bookProviderId && x.MediaType == book.MediaType)
                        .SelectMany(x => x.Editions ?? Enumerable.Empty<Edition>())
                        .ToList();

                    book.Editions = FilterEditions(book.Editions, localEditions, localFiles, profile, book.MediaType);
                }

                FilterByPredicate(hash, GetBookKey, localHash, profile, (x, p) => x.Editions.Any(e => e.PageCount >= p.MinPages) || x.Editions.All(e => e.PageCount == 0), "minimum page count not met");
                FilterByPredicate(hash, GetBookKey, localHash, profile, (x, p) => x.Editions.Any(), "all editions filtered out");

                return hash.ToList();
            }

        private List<Edition> FilterEditions(IEnumerable<Edition> editions, List<Edition> localEditions, List<BookFile> localFiles, MetadataProfile profile, BookMediaType mediaType)
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

            var hash = new HashSet<Edition>(editions);

            var remoteEditionIdsByEditionToken = BuildRemoteEditionIdsByEditionToken(hash);
            var localHash = new HashSet<string>(localEditions.Where(x => x.ManualAdd).Select(x => x.ForeignEditionId));
            localHash.UnionWith(localFiles.Select(x => x.Edition.ForeignEditionId));
            localHash.UnionWith(GetProtectedEditionIdsFromLocalFiles(localFiles, remoteEditionIdsByEditionToken, mediaType));

            // Language filtering logic: only filter if languages are explicitly configured
            FilterByPredicate(hash, x => x.ForeignEditionId, localHash, profile, (x, p) => EditionMetadataProfileFilter.IsAllowedLanguage(x, allowedLanguages, allowUnknownLanguage, languageFilterConfigured), "edition language not allowed");
            FilterByPredicate(hash, x => x.ForeignEditionId, localHash, profile, EditionMetadataProfileFilter.MeetsIdentifierRequirements, "missing required identifier(s)");
            FilterByPredicate(hash, x => x.ForeignEditionId, localHash, profile, (x, p) => !p.Ignored.Any(i => MatchesTerms(x.Title, i)), "contains ignored terms");

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

        private Dictionary<string, HashSet<string>> BuildRemoteEditionIdsByEditionToken(IEnumerable<Edition> editions)
        {
            var index = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var edition in editions ?? Enumerable.Empty<Edition>())
            {
                if (edition == null || string.IsNullOrWhiteSpace(edition.ForeignEditionId))
                {
                    continue;
                }

                foreach (var token in BookEditionIdentity.GetRemoteEditionRehomeTokens(edition))
                {
                    AddIndexValue(index, token, edition.ForeignEditionId);
                }
            }

            return index;
        }

        private HashSet<string> GetProtectedBookKeysFromLocalFiles(IEnumerable<BookFile> localFiles, Dictionary<string, HashSet<string>> remoteBookKeysByEditionToken)
        {
            var protectedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in localFiles ?? Enumerable.Empty<BookFile>())
            {
                if (!TryGetFileMediaType(file, out var mediaType))
                {
                    continue;
                }

                var tokens = BookEditionIdentity.GetEditionRehomeTokens(file.Edition);
                var keys = ResolveMediaScopedKeys(tokens, remoteBookKeysByEditionToken, mediaType);
                if (keys.Count == 1)
                {
                    protectedKeys.Add(keys.First());
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

            return protectedKeys;
        }

        private HashSet<string> GetProtectedEditionIdsFromLocalFiles(IEnumerable<BookFile> localFiles, Dictionary<string, HashSet<string>> remoteEditionIdsByEditionToken, BookMediaType mediaType)
        {
            var protectedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in localFiles ?? Enumerable.Empty<BookFile>())
            {
                if (!TryGetFileMediaType(file, out var fileMediaType) || fileMediaType != mediaType)
                {
                    continue;
                }

                var tokens = BookEditionIdentity.GetEditionRehomeTokens(file.Edition);
                var ids = ResolveKeys(tokens, remoteEditionIdsByEditionToken);
                if (ids.Count == 1)
                {
                    protectedIds.Add(ids.First());
                }
                else if (ids.Count > 1)
                {
                    _logger.Warn("Skipping metadata-profile owned-edition protection for file '{0}': edition tokens [{1}] point to multiple remote editions [{2}] for mediaType={3}.",
                        file.Path,
                        string.Join(", ", tokens),
                        string.Join(", ", ids.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)),
                        mediaType);
                }
            }

            return protectedIds;
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

        private void FilterByPredicate<T>(HashSet<T> remoteItems, Func<T, string> getId, HashSet<string> localItems, MetadataProfile profile, Func<T, MetadataProfile, bool> bookAllowed, string message)
        {
            // Performance optimization: process in batches for large collections
            const int batchSize = 1000;
            var totalItems = remoteItems.Count;

            if (totalItems > batchSize)
            {
                _logger.Debug($"Processing {totalItems} {typeof(T).Name} items in batches of {batchSize} for {message}");

                var itemsList = remoteItems.ToList();
                var allFiltered = new HashSet<T>();

                for (var i = 0; i < itemsList.Count; i += batchSize)
                {
                    var batch = itemsList.Skip(i).Take(batchSize);
                    var batchFiltered = batch.Where(x => !bookAllowed(x, profile) && !localItems.Contains(getId(x))).ToList();

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
                var filtered = new HashSet<T>(remoteItems.Where(x => !bookAllowed(x, profile) && !localItems.Contains(getId(x))));
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

        private static bool IsNumericSeriesPosition(string position)
        {
            return double.TryParse(position, out _);
        }

        private bool IsPartOrSet(Book book, List<SeriesBookLink> seriesLinks, HashSet<string> titles)
        {
            if (seriesLinks != null &&
                seriesLinks.Any(x => x.Position.IsNotNullOrWhiteSpace()) &&
                !seriesLinks.Any(s => IsNumericSeriesPosition(s.Position)))
            {
                // No non-empty series entries parse to a number, so all like 1-3 etc.
                return true;
            }

            // SeriesBookLink rows are often sparse, or hold a tidied-up position, while the book itself
            // still carries what the metadata provider returned - "3, Part 1 of 2", "2A", "2 Part B".
            // A position that is not a number means this record is one slice of a work, not the work.
            if (book.SeriesPosition.IsNotNullOrWhiteSpace() && !IsNumericSeriesPosition(book.SeriesPosition))
            {
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
