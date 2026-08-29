using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MetadataSource.Hardcover;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Tags;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.ImportLists.Hardcover.Library
{
    public class HardcoverLibraryImportList : ImportListBase<HardcoverLibraryImportListSettings>
    {
        private const string HardcoverGraphQLEndpoint = "https://api.hardcover.app/v1/graphql";
        private const int PageSize = 200;
        private const int WantToReadStatusId = 1;
        private const int CurrentlyReadingStatusId = 2;
        private const int ReadStatusId = 3;
        private const string OwnedListSlug = "owned";
        private static readonly TimeSpan FullSyncInterval = TimeSpan.FromDays(7);

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private static readonly string MeQuery = @"
            query Me {
                me {
                    id
                    username
                    image { url }
                }
            }";

        private static readonly string OwnedListQuery = $@"
            query OwnedList($userId: Int!) {{
                lists(
                    where: {{
                        user_id: {{ _eq: $userId }}
                        slug: {{ _eq: ""{OwnedListSlug}"" }}
                    }}
                    limit: 1
                ) {{
                    id
                }}
            }}";

	        private static readonly string OwnedListBooksPageQuery = @"
	            query OwnedListBooks($listId: Int!, $limit: Int!, $beforeId: Int!) {
	                list_books(
	                    where: {
	                        list_id: { _eq: $listId }
	                        id: { _lt: $beforeId }
	                    }
	                    limit: $limit
	                    order_by: { id: desc }
	                ) {
	                    id
	                    edition_id
	                    edition {
	                        id
	                        reading_format_id
	                        edition_format
	                        audio_seconds
	                    }
	                    book {
	                        id
	                        canonical_id
	                        title
	                        subtitle
	                        image { url }
	                        contributions(where: { _or: [{ contribution: { _is_null: true } }, { contribution: { _eq: """" } }, { contribution: { _eq: ""Author"" } }] }, order_by: { id: asc }, limit: 30) {
	                            author_id
	                            contribution
	                            author { id name canonical_id identifiers }
	                        }
	                    }
	                }
	            }";

	        // Fallback query for Hardcover schema changes: `contributions.author` was removed and/or restricted.
	        // We alias `cached_contributors` to `contributions` so existing DTOs keep working.
	        private static readonly string OwnedListBooksPageQueryCachedContributors = @"
	            query OwnedListBooks($listId: Int!, $limit: Int!, $beforeId: Int!) {
	                list_books(
	                    where: {
	                        list_id: { _eq: $listId }
	                        id: { _lt: $beforeId }
	                    }
	                    limit: $limit
	                    order_by: { id: desc }
	                ) {
	                    id
	                    edition_id
	                    edition {
	                        id
	                        reading_format_id
	                        edition_format
	                        audio_seconds
	                    }
	                    book {
	                        id
	                        canonical_id
	                        title
	                        subtitle
	                        image { url }
	                        contributions: cached_contributors
	                    }
	                }
	            }";

	        private static readonly string OwnedListBooksDeltaQuery = @"
	            query OwnedListBooksDelta($listId: Int!, $limit: Int!, $afterId: Int!) {
	                list_books(
	                    where: {
	                        list_id: { _eq: $listId }
	                        id: { _gt: $afterId }
	                    }
	                    limit: $limit
	                    order_by: { id: asc }
	                ) {
	                    id
	                    edition_id
	                    edition {
	                        id
	                        reading_format_id
	                        edition_format
	                        audio_seconds
	                    }
	                    book {
	                        id
	                        canonical_id
	                        title
	                        subtitle
	                        image { url }
	                        contributions(where: { _or: [{ contribution: { _is_null: true } }, { contribution: { _eq: """" } }, { contribution: { _eq: ""Author"" } }] }, order_by: { id: asc }, limit: 30) {
	                            author_id
	                            contribution
	                            author { id name canonical_id identifiers }
	                        }
	                    }
	                }
	            }";

	        // Fallback query for Hardcover schema changes: see OwnedListBooksPageQueryCachedContributors.
	        private static readonly string OwnedListBooksDeltaQueryCachedContributors = @"
	            query OwnedListBooksDelta($listId: Int!, $limit: Int!, $afterId: Int!) {
	                list_books(
	                    where: {
	                        list_id: { _eq: $listId }
	                        id: { _gt: $afterId }
	                    }
	                    limit: $limit
	                    order_by: { id: asc }
	                ) {
	                    id
	                    edition_id
	                    edition {
	                        id
	                        reading_format_id
	                        edition_format
	                        audio_seconds
	                    }
	                    book {
	                        id
	                        canonical_id
	                        title
	                        subtitle
	                        image { url }
	                        contributions: cached_contributors
	                    }
	                }
	            }";

	        private static string BuildUserBooksFilter(HardcoverLibraryImportListSettings settings)
	        {
	            var statusIds = new List<int>();
            if (settings.ImportWantToRead)
            {
                statusIds.Add(WantToReadStatusId);
            }

            if (settings.ImportCurrentlyReading)
            {
                statusIds.Add(CurrentlyReadingStatusId);
            }

            if (settings.ImportRead)
            {
                statusIds.Add(ReadStatusId);
            }

            return statusIds.Any()
                ? $"status_id: {{ _in: [{string.Join(", ", statusIds)}] }}"
                : null;
        }

	        private static string BuildUserBooksPageQuery(HardcoverLibraryImportListSettings settings)
	        {
	            var filter = BuildUserBooksFilter(settings);

	            return $@"
	                query UserBooks($userId: Int!, $limit: Int!, $beforeId: Int!) {{
	                    user_books(
	                        where: {{
	                            user_id: {{ _eq: $userId }}
	                            id: {{ _lt: $beforeId }}
	                            {filter}
	                        }}
	                        limit: $limit
	                        order_by: {{ id: desc }}
	                    ) {{
	                        id
	                        updated_at
	                        status_id
	                        edition_id
	                        edition {{
	                            id
	                            reading_format_id
	                            edition_format
	                            audio_seconds
	                        }}
	                        book {{
	                            id
	                            canonical_id
	                            title
	                            subtitle
	                            image {{ url }}
	                            contributions(where: {{ _or: [{{ contribution: {{ _is_null: true }} }}, {{ contribution: {{ _eq: """" }} }}, {{ contribution: {{ _eq: ""Author"" }} }}] }}, order_by: {{ id: asc }}, limit: 30) {{
	                                author_id
	                                contribution
	                                author {{ id name canonical_id identifiers }}
	                            }}
	                        }}
	                    }}
	                }}";
	        }

	        private static string BuildUserBooksPageQueryCachedContributors(HardcoverLibraryImportListSettings settings)
	        {
	            var filter = BuildUserBooksFilter(settings);

	            return $@"
	                query UserBooks($userId: Int!, $limit: Int!, $beforeId: Int!) {{
	                    user_books(
	                        where: {{
	                            user_id: {{ _eq: $userId }}
	                            id: {{ _lt: $beforeId }}
	                            {filter}
	                        }}
	                        limit: $limit
	                        order_by: {{ id: desc }}
	                    ) {{
	                        id
	                        updated_at
	                        status_id
	                        edition_id
	                        edition {{
	                            id
	                            reading_format_id
	                            edition_format
	                            audio_seconds
	                        }}
	                        book {{
	                            id
	                            canonical_id
	                            title
	                            subtitle
	                            image {{ url }}
	                            contributions: cached_contributors
	                        }}
	                    }}
	                }}";
	        }

	        private static string BuildUserBooksDeltaQuery(HardcoverLibraryImportListSettings settings)
	        {
	            var filter = BuildUserBooksFilter(settings);
	            var filterExpression = filter.IsNullOrWhiteSpace() ? null : $"{{ {filter} }}";

            var whereClause = filterExpression.IsNullOrWhiteSpace()
                ? @"
                            user_id: { _eq: $userId }
                            _or: [
                                { updated_at: { _gt: $updatedAfter } }
                                { updated_at: { _eq: $updatedAfter }, id: { _gt: $afterId } }
                            ]"
                : $@"
                            user_id: {{ _eq: $userId }}
                            _and: [
                                {filterExpression}
                                {{
                                    _or: [
                                        {{ updated_at: {{ _gt: $updatedAfter }} }}
                                        {{ updated_at: {{ _eq: $updatedAfter }}, id: {{ _gt: $afterId }} }}
                                    ]
                                }}
                            ]";

	            return $@"
	                query UserBooksDelta($userId: Int!, $limit: Int!, $updatedAfter: timestamptz!, $afterId: Int!) {{
	                    user_books(
	                        where: {{
	{whereClause}
	                        }}
	                        limit: $limit
	                        order_by: [{{ updated_at: asc }}, {{ id: asc }}]
	                    ) {{
	                        id
	                        updated_at
	                        status_id
	                        edition_id
	                        edition {{
	                            id
	                            reading_format_id
	                            edition_format
	                            audio_seconds
	                        }}
	                        book {{
	                            id
	                            canonical_id
	                            title
	                            subtitle
	                            image {{ url }}
	                            contributions(where: {{ _or: [{{ contribution: {{ _is_null: true }} }}, {{ contribution: {{ _eq: """" }} }}, {{ contribution: {{ _eq: ""Author"" }} }}] }}, order_by: {{ id: asc }}, limit: 30) {{
	                                author_id
	                                contribution
	                                author {{ id name canonical_id identifiers }}
	                            }}
	                        }}
	                    }}
	                }}";
	        }

	        private static string BuildUserBooksDeltaQueryCachedContributors(HardcoverLibraryImportListSettings settings)
	        {
	            var filter = BuildUserBooksFilter(settings);
	            var filterExpression = filter.IsNullOrWhiteSpace() ? null : $"{{ {filter} }}";

	            var whereClause = filterExpression.IsNullOrWhiteSpace()
	                ? @"
	                            user_id: { _eq: $userId }
	                            _or: [
	                                { updated_at: { _gt: $updatedAfter } }
	                                { updated_at: { _eq: $updatedAfter }, id: { _gt: $afterId } }
	                            ]"
	                : $@"
	                            user_id: {{ _eq: $userId }}
	                            _and: [
	                                {filterExpression}
	                                {{
	                                    _or: [
	                                        {{ updated_at: {{ _gt: $updatedAfter }} }}
	                                        {{ updated_at: {{ _eq: $updatedAfter }}, id: {{ _gt: $afterId }} }}
	                                    ]
	                                }}
	                            ]";

	            return $@"
	                query UserBooksDelta($userId: Int!, $limit: Int!, $updatedAfter: timestamptz!, $afterId: Int!) {{
	                    user_books(
	                        where: {{
	{whereClause}
	                        }}
	                        limit: $limit
	                        order_by: [{{ updated_at: asc }}, {{ id: asc }}]
	                    ) {{
	                        id
	                        updated_at
	                        status_id
	                        edition_id
	                        edition {{
	                            id
	                            reading_format_id
	                            edition_format
	                            audio_seconds
	                        }}
	                        book {{
	                            id
	                            canonical_id
	                            title
	                            subtitle
	                            image {{ url }}
	                            contributions: cached_contributors
	                        }}
	                    }}
	                }}";
	        }

	        private readonly IHttpClient _httpClient;
	        private readonly Lazy<IQualityProfileService> _qualityProfileService;
	        private readonly Lazy<IMetadataProfileService> _metadataProfileService;
	        private readonly Lazy<ITagService> _tagService;
	        private readonly IRootFolderService _rootFolderService;
	        private readonly IRootFolderSettingsResolver _rootFolderSettingsResolver;
	        private readonly IHardcoverLibraryImportListStateRepository _stateRepository;
	        private HardcoverLibraryImportListState _pendingState;
	        private bool _useCachedContributorsFallback;

	        public override string Name => "Hardcover Library";
	        public override ImportListType ListType => ImportListType.Other;
	        public override TimeSpan MinRefreshInterval => TimeSpan.FromHours(1);

        public override IEnumerable<ProviderDefinition> DefaultDefinitions
        {
            get
            {
                foreach (var definition in base.DefaultDefinitions.Cast<ImportListDefinition>())
                {
                    definition.ShouldMonitor = ImportListMonitorType.SpecificBook;
                    yield return definition;
                }
            }
        }

        public HardcoverLibraryImportList(IHttpClient httpClient,
            Lazy<IQualityProfileService> qualityProfileService,
            Lazy<IMetadataProfileService> metadataProfileService,
            Lazy<ITagService> tagService,
            IRootFolderService rootFolderService,
            IRootFolderSettingsResolver rootFolderSettingsResolver,
            IHardcoverLibraryImportListStateRepository stateRepository,
            IImportListStatusService importListStatusService,
            IConfigService configService,
            IParsingService parsingService,
            Logger logger)
            : base(importListStatusService, configService, parsingService, logger)
        {
            _httpClient = httpClient;
            _qualityProfileService = qualityProfileService;
            _metadataProfileService = metadataProfileService;
            _tagService = tagService;
            _rootFolderService = rootFolderService;
            _rootFolderSettingsResolver = rootFolderSettingsResolver;
            _stateRepository = stateRepository;
        }

	        public override IList<ImportListItemInfo> Fetch()
	        {
	            // Only enable the schema fallback for the current sync run. If Hardcover restores the old schema,
	            // subsequent runs will automatically prefer the richer `contributions { author { ... } }` query.
	            _useCachedContributorsFallback = false;

	            var results = new List<ImportListItemInfo>();

	            try
	            {
                var hasPerListToken = Settings?.ApiToken.IsNotNullOrWhiteSpace() == true;
                var authHeader = GetHardcoverAuthHeaderValue();

                if (authHeader.IsNullOrWhiteSpace())
                {
                    _logger.Warn("Hardcover library import skipped - no Hardcover API token configured");
                    _importListStatusService.RecordFailure(Definition.Id);
                    return CleanupListItems(results);
                }

                if (!hasPerListToken && !_configService.HardcoverEnabled)
                {
                    _logger.Warn("Hardcover library import skipped - Hardcover not enabled");
                    _importListStatusService.RecordFailure(Definition.Id);
                    return CleanupListItems(results);
                }

                _logger.Debug("Hardcover library import: using {0} API token", hasPerListToken ? "per-list" : "global");

                if (!Settings.ImportOwned && !Settings.ImportWantToRead && !Settings.ImportCurrentlyReading && !Settings.ImportRead)
                {
                    _logger.Warn("Hardcover library import skipped - no library sections selected");
                    _importListStatusService.RecordFailure(Definition.Id);
                    return CleanupListItems(results);
                }

                var user = GetCurrentUser(authHeader);
                if (user == null)
                {
                    _importListStatusService.RecordFailure(Definition.Id);
                    return CleanupListItems(results);
                }

                _logger.Debug("Hardcover library import user: {0} ({1})", user.Username, user.Id);

                var includeUserBooks = Settings.ImportWantToRead || Settings.ImportCurrentlyReading || Settings.ImportRead;

                var now = DateTime.UtcNow;
                var settingsSignature = BuildSettingsSignature();

                var state = _stateRepository.GetByImportListId(Definition.Id);
                var baseNeedsFullSync = state == null ||
                                        state.HardcoverUserId != user.Id ||
                                        !string.Equals(state.SettingsSignature, settingsSignature, StringComparison.Ordinal);

                state ??= new HardcoverLibraryImportListState
                {
                    ImportListId = Definition.Id,
                    CreatedAt = now
                };

                var periodicFullSyncDue = !state.LastFullSyncAt.HasValue || (now - state.LastFullSyncAt.Value) >= FullSyncInterval;
                if (periodicFullSyncDue && state.LastFullSyncAt.HasValue && !baseNeedsFullSync)
                {
                    _logger.Debug("Hardcover library import: periodic full sync due (last full sync at {0:o})", state.LastFullSyncAt.Value);
                }

                var stateDirty = baseNeedsFullSync || periodicFullSyncDue;

                if (includeUserBooks)
                {
                    var needsFullSync = baseNeedsFullSync ||
                                        periodicFullSyncDue ||
                                        !state.CursorUpdatedAt.HasValue ||
                                        !state.CursorUserBookId.HasValue;

                    if (needsFullSync)
                    {
                        _logger.Debug("Hardcover library import: performing full sync");

                        var beforeId = int.MaxValue;
                        DateTime? maxUpdatedAt = null;
                        int? maxUserBookId = null;

                        while (true)
                        {
                            var (page, nextBeforeId) = GetUserBooksPage(authHeader, user.Id, PageSize, beforeId);
                            if (page.Count == 0)
                            {
                                break;
                            }

                            results.AddRange(MapUserBooks(page));
                            UpdateMaxCursor(page, ref maxUpdatedAt, ref maxUserBookId);

                            if (nextBeforeId == null || nextBeforeId.Value >= beforeId)
                            {
                                break;
                            }

                            beforeId = nextBeforeId.Value;
                        }

                        // If the library is empty, persist a cursor so we can do fast delta sync on the next run.
                        maxUpdatedAt ??= now;
                        maxUserBookId ??= 0;

                        state.CursorUpdatedAt = maxUpdatedAt;
                        state.CursorUserBookId = maxUserBookId;
                        state.LastFullSyncAt = now;
                        stateDirty = true;
                    }
                    else
                    {
                        _logger.Debug("Hardcover library import: performing delta sync from {0:o} (user_book id > {1})",
                            state.CursorUpdatedAt.Value, state.CursorUserBookId.Value);

                        var cursorUpdatedAt = state.CursorUpdatedAt.Value;
                        var cursorUserBookId = state.CursorUserBookId.Value;
                        var cursorChanged = false;

                        while (true)
                        {
                            var page = GetUserBooksDelta(authHeader, user.Id, PageSize, cursorUpdatedAt, cursorUserBookId);
                            if (page.Count == 0)
                            {
                                break;
                            }

                            cursorChanged = true;
                            results.AddRange(MapUserBooks(page));

                            var last = page.Last();
                            cursorUpdatedAt = last.UpdatedAt;
                            cursorUserBookId = last.Id;

                            if (page.Count < PageSize)
                            {
                                break;
                            }
                        }

                        if (cursorChanged)
                        {
                            state.CursorUpdatedAt = cursorUpdatedAt;
                            state.CursorUserBookId = cursorUserBookId;
                            stateDirty = true;
                        }
                    }
                }

                if (Settings.ImportOwned)
                {
                    var ownedListId = GetOwnedListId(authHeader, user.Id);
                    if (!ownedListId.HasValue)
                    {
                        _logger.Warn("Hardcover library import: Owned list not found for user {0} ({1})", user.Username, user.Id);

                        if (!state.OwnedCursorListBookId.HasValue)
                        {
                            state.OwnedCursorListBookId = 0;
                            stateDirty = true;
                        }
                    }
                    else
                    {
                        var needsFullSync = baseNeedsFullSync || periodicFullSyncDue || !state.OwnedCursorListBookId.HasValue;

                        if (needsFullSync)
                        {
                            _logger.Debug("Hardcover library import: performing full sync for Owned list");

                            var beforeId = int.MaxValue;
                            int? maxListBookId = null;

                            while (true)
                            {
                                var (page, nextBeforeId) = GetOwnedListBooksPage(authHeader, ownedListId.Value, PageSize, beforeId);
                                if (page.Count == 0)
                                {
                                    break;
                                }

                                results.AddRange(MapOwnedListBooks(page));

                                foreach (var listBook in page)
                                {
                                    if (listBook == null)
                                    {
                                        continue;
                                    }

                                    if (!maxListBookId.HasValue || listBook.Id > maxListBookId.Value)
                                    {
                                        maxListBookId = listBook.Id;
                                    }
                                }

                                if (nextBeforeId == null || nextBeforeId.Value >= beforeId)
                                {
                                    break;
                                }

                                beforeId = nextBeforeId.Value;
                            }

                            maxListBookId ??= 0;
                            state.OwnedCursorListBookId = maxListBookId;
                            state.LastFullSyncAt = now;
                            stateDirty = true;
                        }
                        else
                        {
                            _logger.Debug("Hardcover library import: performing delta sync for Owned list (list_book id > {0})",
                                state.OwnedCursorListBookId.Value);

                            var cursorListBookId = state.OwnedCursorListBookId.Value;
                            var cursorChanged = false;

                            while (true)
                            {
                                var page = GetOwnedListBooksDelta(authHeader, ownedListId.Value, PageSize, cursorListBookId);
                                if (page.Count == 0)
                                {
                                    break;
                                }

                                cursorChanged = true;
                                results.AddRange(MapOwnedListBooks(page));

                                cursorListBookId = page.Last().Id;

                                if (page.Count < PageSize)
                                {
                                    break;
                                }
                            }

                            if (cursorChanged)
                            {
                                state.OwnedCursorListBookId = cursorListBookId;
                                stateDirty = true;
                            }
                        }
                    }
                }

                if (stateDirty)
                {
                    state.HardcoverUserId = user.Id;
                    state.SettingsSignature = settingsSignature;
                    state.UpdatedAt = now;
                    _pendingState = state;
                }
                else
                {
                    _pendingState = null;
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Hardcover library import failed");
                _pendingState = null;
                _importListStatusService.RecordFailure(Definition.Id);
            }

            return CleanupListItems(results);
        }

        public override void CommitState()
        {
            if (_pendingState != null)
            {
                if (_pendingState.Id == 0)
                {
                    _stateRepository.Insert(_pendingState);
                }
                else
                {
                    _stateRepository.Update(_pendingState);
                }

                _pendingState = null;
            }

            _importListStatusService.RecordSuccess(Definition.Id);
        }

        protected override IList<ImportListItemInfo> CleanupListItems(IEnumerable<ImportListItemInfo> releases)
        {
            // Hardcover library sync can include multiple editions of the same book; dedupe by provider IDs, not title.
            var deduped = releases
                .Where(r => r != null)
                .DistinctBy(r => new
                {
                    r.AuthorGoodreadsId,
                    r.BookGoodreadsId,
                    r.EditionGoodreadsId
                })
                .ToList();

            // Prefer Owned/edition-scoped items over book-level entries for the same author+book.
            var editionScopedBooks = new HashSet<(string authorId, string bookId)>(deduped
                .Where(r => r.EditionGoodreadsId.IsNotNullOrWhiteSpace())
                .Select(r => (r.AuthorGoodreadsId, r.BookGoodreadsId)));

            var result = deduped
                .Where(r => r.EditionGoodreadsId.IsNotNullOrWhiteSpace() ||
                            !editionScopedBooks.Contains((r.AuthorGoodreadsId, r.BookGoodreadsId)))
                .ToList();

            result.ForEach(c =>
            {
                c.ImportListId = Definition.Id;
                c.ImportList = Definition.Name;
            });

            return result;
        }

        public override object RequestAction(string action, IDictionary<string, string> query)
        {
            if (action.IsNullOrWhiteSpace())
            {
                return base.RequestAction(action, query);
            }

            if (action == "getAudiobookQualityProfiles")
            {
                return new
                {
                    options = BuildSelectOptions(_qualityProfileService.Value.GetByType(ProfileType.Audiobook).Select(p => (p.Id, p.Name)), includeDefault: true)
                };
            }

            if (action == "getEbookQualityProfiles")
            {
                return new
                {
                    options = BuildSelectOptions(_qualityProfileService.Value.GetByType(ProfileType.Ebook).Select(p => (p.Id, p.Name)), includeDefault: true)
                };
            }

            if (action == "getAudiobookMetadataProfiles")
            {
                return new
                {
                    options = BuildSelectOptions(_metadataProfileService.Value.All()
                        .Where(p => p.ProfileType == MetadataProfileType.General || p.ProfileType == MetadataProfileType.Audiobook)
                        .Select(p => (p.Id, p.Name)), includeDefault: true)
                };
            }

            if (action == "getEbookMetadataProfiles")
            {
                return new
                {
                    options = BuildSelectOptions(_metadataProfileService.Value.All()
                        .Where(p => p.ProfileType == MetadataProfileType.General || p.ProfileType == MetadataProfileType.Ebook)
                        .Select(p => (p.Id, p.Name)), includeDefault: true)
                };
            }

            if (action == "getTags")
            {
                return new
                {
                    options = _tagService.Value.All()
                        .OrderBy(t => t.Label)
                        .Select(t => new
                        {
                            Value = t.Id,
                            Name = t.Label
                        })
                        .ToList()
                };
            }

            return base.RequestAction(action, query);
        }

        protected override void Test(List<ValidationFailure> failures)
        {
            failures.AddIfNotNull(TestConnection());
            failures.AddRange(TestRootFolderConfig());
        }

        private ValidationFailure TestConnection()
        {
            var hasPerListToken = Settings?.ApiToken.IsNotNullOrWhiteSpace() == true;
            var authHeader = GetHardcoverAuthHeaderValue();

            if (authHeader.IsNullOrWhiteSpace())
            {
                return new ValidationFailure(string.Empty, "No Hardcover API token is configured (set one on this import list or in Settings)");
            }

            if (!hasPerListToken && !_configService.HardcoverEnabled)
            {
                return new ValidationFailure(string.Empty, "Hardcover is not enabled (enable it in Settings or set an API token on this import list)");
            }

            try
            {
                var user = GetCurrentUser(authHeader);
                if (user == null)
                {
                    return new ValidationFailure(string.Empty, "Could not retrieve current Hardcover user");
                }

                if (hasPerListToken)
                {
                    Settings.CachedUsername = user.Username ?? string.Empty;
                    Settings.CachedAvatarUrl = user.Image?.Url ?? string.Empty;
                }
                else
                {
                    Settings.CachedUsername = string.Empty;
                    Settings.CachedAvatarUrl = string.Empty;
                }

                // Sanity check: ensure we can query user_books (may be empty, that is fine)
                GetUserBooksPage(authHeader, user.Id, 1, int.MaxValue);

                return null;
            }
            catch (HttpException e)
            {
                _logger.Warn(e, "Hardcover API Error");
                if (e.Response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    return new ValidationFailure(string.Empty, "Hardcover API token is invalid or expired");
                }

                return new ValidationFailure(string.Empty, "Could not retrieve Hardcover library data");
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Unable to connect to Hardcover");
                return new ValidationFailure(string.Empty, "Unable to connect to import list, check the log for more details");
            }
        }

        private IEnumerable<ValidationFailure> TestRootFolderConfig()
        {
            var failures = new List<ValidationFailure>();

            if (Settings == null)
            {
                return failures;
            }

            if (Settings.MonitorAudiobooks)
            {
                failures.AddIfNotNull(TestRootFolder(
                    nameof(Settings.AudiobookRootFolderPath),
                    Settings.AudiobookRootFolderPath,
                    BookMediaType.Audiobook,
                    Settings.AudiobookQualityProfileId,
                    Settings.AudiobookMetadataProfileId));
            }

            if (Settings.MonitorEbooks)
            {
                failures.AddIfNotNull(TestRootFolder(
                    nameof(Settings.EbookRootFolderPath),
                    Settings.EbookRootFolderPath,
                    BookMediaType.Ebook,
                    Settings.EbookQualityProfileId,
                    Settings.EbookMetadataProfileId));
            }

            return failures;
        }

        private ValidationFailure TestRootFolder(string fieldName,
            string rootFolderPath,
            BookMediaType mediaType,
            int overrideQualityProfileId,
            int overrideMetadataProfileId)
        {
            if (rootFolderPath.IsNullOrWhiteSpace())
            {
                return new ValidationFailure(fieldName, "Root folder is required");
            }

            var rootFolder = _rootFolderService.GetBestRootFolder(rootFolderPath);
            if (rootFolder == null)
            {
                return new ValidationFailure(fieldName, $"Root folder '{rootFolderPath}' is not configured in Chaptarr");
            }

            if (mediaType == BookMediaType.Audiobook && rootFolder.FolderType == FolderType.Ebook)
            {
                return new ValidationFailure(fieldName, "Selected root folder is Ebook-only; choose an Audiobook or Mixed root folder");
            }

            if (mediaType == BookMediaType.Ebook && rootFolder.FolderType == FolderType.Audiobook)
            {
                return new ValidationFailure(fieldName, "Selected root folder is Audiobook-only; choose an Ebook or Mixed root folder");
            }

            var resolved = _rootFolderSettingsResolver.ResolveSettings(rootFolder, mediaType);
            if (resolved == null || !resolved.IsConfigured)
            {
                return new ValidationFailure(fieldName, $"Selected root folder '{rootFolder.Path}' does not have {mediaType} defaults configured");
            }

            if (overrideQualityProfileId <= 0 && (resolved.QualityProfileId ?? 0) <= 0)
            {
                return new ValidationFailure(fieldName, $"Selected root folder '{rootFolder.Path}' is missing a {mediaType} quality profile default");
            }

            if (overrideMetadataProfileId <= 0 && (resolved.MetadataProfileId ?? 0) <= 0)
            {
                return new ValidationFailure(fieldName, $"Selected root folder '{rootFolder.Path}' is missing a {mediaType} metadata profile default");
            }

            return null;
        }

        private HardcoverUser GetCurrentUser(string authHeader)
        {
            var response = ExecuteGraphQLRequest<MeResponseData>(authHeader, MeQuery, variables: null);

            if (response?.Data?.Me == null || response.Data.Me.Count == 0)
            {
                return null;
            }

            return response.Data.Me[0];
        }

        private string BuildSettingsSignature()
        {
            return $"owned:{Settings.ImportOwned};wtr:{Settings.ImportWantToRead};cr:{Settings.ImportCurrentlyReading};read:{Settings.ImportRead};ma:{Settings.MonitorAudiobooks};me:{Settings.MonitorEbooks}";
        }

	        private (List<HardcoverUserBook> items, int? nextBeforeId) GetUserBooksPage(string authHeader, int userId, int limit, int beforeId)
	        {
	            var variables = new
	            {
	                userId,
	                limit,
	                beforeId
	            };

	            var query = _useCachedContributorsFallback
	                ? BuildUserBooksPageQueryCachedContributors(Settings)
	                : BuildUserBooksPageQuery(Settings);

	            var response = ExecuteGraphQLRequestWithCachedContributorsFallback<UserBooksResponseData>(
	                authHeader,
	                query,
	                BuildUserBooksPageQueryCachedContributors(Settings),
	                variables);

	            var userBooks = response?.Data?.UserBooks ?? new List<HardcoverUserBook>();
	            if (userBooks.Count == 0)
	            {
                return (new List<HardcoverUserBook>(), null);
            }

            var nextBeforeId = userBooks.Min(ub => ub.Id);
            return (userBooks, nextBeforeId);
        }

        private int? GetOwnedListId(string authHeader, int userId)
        {
            var variables = new
            {
                userId
            };

            var response = ExecuteGraphQLRequest<OwnedListResponseData>(authHeader, OwnedListQuery, variables);
            return response?.Data?.Lists?.FirstOrDefault()?.Id;
        }

	        private (List<HardcoverListBook> items, int? nextBeforeId) GetOwnedListBooksPage(string authHeader, int listId, int limit, int beforeId)
	        {
	            var variables = new
	            {
	                listId,
	                limit,
	                beforeId
	            };

	            var response = ExecuteGraphQLRequestWithCachedContributorsFallback<ListBooksResponseData>(
	                authHeader,
	                _useCachedContributorsFallback ? OwnedListBooksPageQueryCachedContributors : OwnedListBooksPageQuery,
	                OwnedListBooksPageQueryCachedContributors,
	                variables);

	            var listBooks = response?.Data?.ListBooks ?? new List<HardcoverListBook>();
	            if (listBooks.Count == 0)
	            {
                return (new List<HardcoverListBook>(), null);
            }

            var nextBeforeId = listBooks.Min(lb => lb.Id);
            return (listBooks, nextBeforeId);
        }

	        private List<HardcoverListBook> GetOwnedListBooksDelta(string authHeader, int listId, int limit, int afterId)
	        {
	            var variables = new
	            {
	                listId,
	                limit,
	                afterId
	            };

	            var response = ExecuteGraphQLRequestWithCachedContributorsFallback<ListBooksResponseData>(
	                authHeader,
	                _useCachedContributorsFallback ? OwnedListBooksDeltaQueryCachedContributors : OwnedListBooksDeltaQuery,
	                OwnedListBooksDeltaQueryCachedContributors,
	                variables);
	            return response?.Data?.ListBooks ?? new List<HardcoverListBook>();
	        }

	        private List<HardcoverUserBook> GetUserBooksDelta(string authHeader, int userId, int limit, DateTime updatedAfter, int afterId)
	        {
	            var variables = new
	            {
	                userId,
	                limit,
	                updatedAfter,
	                afterId
	            };

	            var query = _useCachedContributorsFallback
	                ? BuildUserBooksDeltaQueryCachedContributors(Settings)
	                : BuildUserBooksDeltaQuery(Settings);

	            var response = ExecuteGraphQLRequestWithCachedContributorsFallback<UserBooksResponseData>(
	                authHeader,
	                query,
	                BuildUserBooksDeltaQueryCachedContributors(Settings),
	                variables);

	            return response?.Data?.UserBooks ?? new List<HardcoverUserBook>();
	        }

        private List<ImportListItemInfo> MapUserBooks(List<HardcoverUserBook> userBooks)
        {
            var results = new List<ImportListItemInfo>();

            foreach (var userBook in userBooks)
            {
                var item = MapUserBook(userBook);
                if (item != null)
                {
                    results.Add(item);
                }
            }

            return results;
        }

        private ImportListItemInfo MapUserBook(HardcoverUserBook userBook)
        {
            var book = userBook?.Book;
            if (book == null)
            {
                return null;
            }

            var canonicalBookId = book.CanonicalId ?? book.Id;
            var bookProviderId = $"hc:{canonicalBookId}";

            var primaryAuthor = GetPrimaryAuthor(book.Contributions);
            if (primaryAuthor == null)
            {
                return null;
            }

            var authorProviderId = ResolveAuthorProviderId(primaryAuthor);

            return new ImportListItemInfo
            {
                Author = primaryAuthor.Name,
                AuthorProviderId = authorProviderId,
                Book = book.Title,
                BookProviderId = bookProviderId,
                EditionProviderId = null,
                HardcoverReadingFormatId = null
            };
        }

        private List<ImportListItemInfo> MapOwnedListBooks(List<HardcoverListBook> listBooks)
        {
            var results = new List<ImportListItemInfo>();

            foreach (var listBook in listBooks)
            {
                var item = MapOwnedListBook(listBook);
                if (item != null)
                {
                    results.Add(item);
                }
            }

            return results;
        }

        private ImportListItemInfo MapOwnedListBook(HardcoverListBook listBook)
        {
            var book = listBook?.Book;
            if (book == null)
            {
                return null;
            }

            var canonicalBookId = book.CanonicalId ?? book.Id;
            var bookProviderId = $"hc:{canonicalBookId}";

            var editionProviderId = listBook.EditionId.HasValue ? $"hc-ed:{listBook.EditionId.Value}" : null;
            if (editionProviderId.IsNullOrWhiteSpace())
            {
                return null;
            }

            var inferredReadingFormatId = InferHardcoverReadingFormatId(listBook.Edition);
            var mappedReadingFormatId = inferredReadingFormatId == 2 ? 2 : 3;

            if (mappedReadingFormatId == 2 && !Settings.MonitorAudiobooks)
            {
                return null;
            }

            if (mappedReadingFormatId != 2 && !Settings.MonitorEbooks)
            {
                return null;
            }

            var primaryAuthor = GetPrimaryAuthor(book.Contributions);
            if (primaryAuthor == null)
            {
                return null;
            }

            var authorProviderId = ResolveAuthorProviderId(primaryAuthor);

            return new ImportListItemInfo
            {
                Author = primaryAuthor.Name,
                AuthorProviderId = authorProviderId,
                Book = book.Title,
                BookProviderId = bookProviderId,
                EditionProviderId = editionProviderId,
                HardcoverReadingFormatId = mappedReadingFormatId
            };
        }

        private static void UpdateMaxCursor(IEnumerable<HardcoverUserBook> userBooks, ref DateTime? maxUpdatedAt, ref int? maxUserBookId)
        {
            foreach (var userBook in userBooks)
            {
                if (userBook == null || userBook.UpdatedAt == default)
                {
                    continue;
                }

                if (!maxUpdatedAt.HasValue || userBook.UpdatedAt > maxUpdatedAt.Value)
                {
                    maxUpdatedAt = userBook.UpdatedAt;
                    maxUserBookId = userBook.Id;
                }
                else if (userBook.UpdatedAt == maxUpdatedAt.Value && (!maxUserBookId.HasValue || userBook.Id > maxUserBookId.Value))
                {
                    maxUserBookId = userBook.Id;
                }
            }
        }

        private static int? InferHardcoverReadingFormatId(HardcoverEdition edition)
        {
            if (edition == null)
            {
                return null;
            }

            if (edition.ReadingFormatId == 2)
            {
                return 2;
            }

            if (edition.AudioSeconds.HasValue && edition.AudioSeconds.Value > 0)
            {
                return 2;
            }

            if (edition.ReadingFormatId == 3)
            {
                return 3;
            }

            var editionFormat = edition.EditionFormat?.Trim();

            if (editionFormat.IsNotNullOrWhiteSpace())
            {
                if (editionFormat.Contains("audio", StringComparison.OrdinalIgnoreCase))
                {
                    return 2;
                }

                if (editionFormat.Contains("kindle", StringComparison.OrdinalIgnoreCase) ||
                    editionFormat.Contains("ebook", StringComparison.OrdinalIgnoreCase) ||
                    editionFormat.Contains("e-book", StringComparison.OrdinalIgnoreCase))
                {
                    return 3;
                }
            }

            return edition.ReadingFormatId > 0 ? edition.ReadingFormatId : null;
        }

        private HardcoverAuthor GetPrimaryAuthor(List<HardcoverContribution> contributions)
        {
            if (contributions == null || contributions.Count == 0)
            {
                return null;
            }

            var primaryContribution = contributions.FirstOrDefault(c => c != null && HardcoverContributionRoles.IsPrimaryAuthor(c.Contribution));

            return primaryContribution?.Author;
        }

        private static string ResolveAuthorProviderId(HardcoverAuthor author)
        {
            if (author == null)
            {
                return null;
            }

            // Prefer Hardcover canonical author IDs for import keys. Hardcover can have duplicate author rows,
            // where `canonical_id` points at the canonical author. Chaptarr's metadata server expects canonical IDs.
            var canonicalAuthorId = author.CanonicalId ?? author.Id;
            return $"hc:{canonicalAuthorId}";
        }

        private static string GetFirstIdentifierValue(JsonElement identifiers, string key)
        {
            if (key.IsNullOrWhiteSpace())
            {
                return null;
            }

            if (identifiers.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!identifiers.TryGetProperty(key, out var value))
            {
                return null;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }

            if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in value.EnumerateArray())
                {
                    if (element.ValueKind == JsonValueKind.String)
                    {
                        var str = element.GetString();
                        if (str.IsNotNullOrWhiteSpace())
                        {
                            return str;
                        }
                    }
                }
            }

            return null;
        }

        private static string NormalizeOpenLibraryAuthorId(string rawValue)
        {
            if (rawValue.IsNullOrWhiteSpace())
            {
                return null;
            }

            var value = rawValue.Trim();

            if (value.StartsWith("/authors/", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring("/authors/".Length);
            }

            if (value.Contains("/"))
            {
                value = value.Split('/').LastOrDefault() ?? value;
            }

            return value.Trim();
        }

        private string GetHardcoverAuthHeaderValue()
        {
            var token = Settings?.ApiToken;
            if (token.IsNullOrWhiteSpace())
            {
                token = _configService.HardcoverApiToken;
            }

            if (token.IsNullOrWhiteSpace())
            {
                return null;
            }

            token = token.Trim();
            return token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? token : $"Bearer {token}";
        }

	        private HardcoverGraphQLResponse<TData> ExecuteGraphQLRequest<TData>(string authHeader, string query, object variables)
	        {
	            var payload = new
	            {
	                query,
                variables
            };

            var request = new HttpRequestBuilder(HardcoverGraphQLEndpoint)
                .SetHeader("Content-Type", "application/json")
                .SetHeader("Accept", "application/json")
                .SetHeader("User-Agent", ExternalImageRequestHeaders.GetRandomUserAgent())
                .Build();

            request.Method = HttpMethod.Post;
            request.Headers.Add("Authorization", authHeader);
            request.SetContent(JsonSerializer.Serialize(payload, JsonOptions));

            var httpResponse = _httpClient.Execute(request);

            if (httpResponse.HasHttpError)
            {
                throw new HttpException(httpResponse.Request, httpResponse);
            }

            var response = JsonSerializer.Deserialize<HardcoverGraphQLResponse<TData>>(httpResponse.Content, JsonOptions);
            if (response?.Errors?.Any() == true)
            {
                var message = string.Join("; ", response.Errors.Select(e => e.Message).Where(m => m.IsNotNullOrWhiteSpace()));
                throw new HttpException(httpResponse.Request, httpResponse, message);
            }

	            return response;
	        }

	        private HardcoverGraphQLResponse<TData> ExecuteGraphQLRequestWithCachedContributorsFallback<TData>(
	            string authHeader,
	            string primaryQuery,
	            string fallbackQuery,
	            object variables)
	        {
	            if (_useCachedContributorsFallback)
	            {
	                return ExecuteGraphQLRequest<TData>(authHeader, fallbackQuery, variables);
	            }

	            try
	            {
	                return ExecuteGraphQLRequest<TData>(authHeader, primaryQuery, variables);
	            }
	            catch (HttpException ex) when (ShouldFallbackToCachedContributors(ex))
	            {
	                _useCachedContributorsFallback = true;
	                _logger.Warn(ex, "Hardcover schema change detected for contributions/author fields; retrying with cached_contributors fallback");
	                return ExecuteGraphQLRequest<TData>(authHeader, fallbackQuery, variables);
	            }
	        }

	        private static bool ShouldFallbackToCachedContributors(HttpException ex)
	        {
	            var message = ex?.Message;
	            if (message.IsNullOrWhiteSpace())
	            {
	                return false;
	            }

	            // Common validation failure when Hardcover removes `contributions.author` from the schema.
	            if (message.Contains("field 'author' not found in type: 'contributions'", StringComparison.OrdinalIgnoreCase) ||
	                message.Contains("field \"author\" not found in type: \"contributions\"", StringComparison.OrdinalIgnoreCase))
	            {
	                return true;
	            }

	            // If Hardcover re-adds `contributions.author` but does not expose these fields, we can still import
	            // via cached_contributors (at the cost of losing canonical_id/identifiers).
	            if (message.Contains("field 'canonical_id' not found", StringComparison.OrdinalIgnoreCase) ||
	                message.Contains("field \"canonical_id\" not found", StringComparison.OrdinalIgnoreCase) ||
	                message.Contains("field 'identifiers' not found", StringComparison.OrdinalIgnoreCase) ||
	                message.Contains("field \"identifiers\" not found", StringComparison.OrdinalIgnoreCase))
	            {
	                return true;
	            }

	            return false;
	        }

        private static List<object> BuildSelectOptions(IEnumerable<(int id, string name)> items, bool includeDefault)
        {
            var options = new List<object>();

            if (includeDefault)
            {
                options.Add(new
                {
                    Value = 0,
                    Name = "Use root folder defaults",
                    LocalizationKey = "UseRootFolderDefaults"
                });
            }

            options.AddRange(items
                .OrderBy(i => i.name)
                .Select(i => new
                {
                    Value = i.id,
                    Name = i.name
                }));

            return options;
        }

        private sealed class HardcoverGraphQLResponse<T>
        {
            [JsonPropertyName("data")]
            public T Data { get; set; }

            [JsonPropertyName("errors")]
            public List<HardcoverGraphQLError> Errors { get; set; } = new();
        }

        private sealed class HardcoverGraphQLError
        {
            [JsonPropertyName("message")]
            public string Message { get; set; }
        }

        private sealed class MeResponseData
        {
            [JsonPropertyName("me")]
            public List<HardcoverUser> Me { get; set; } = new();
        }

        private sealed class HardcoverUser
        {
            [JsonPropertyName("id")]
            public int Id { get; set; }

            [JsonPropertyName("username")]
            public string Username { get; set; }

            [JsonPropertyName("image")]
            public HardcoverUserImage Image { get; set; }
        }

        private sealed class HardcoverUserImage
        {
            [JsonPropertyName("url")]
            public string Url { get; set; }
        }

        private sealed class UserBooksResponseData
        {
            [JsonPropertyName("user_books")]
            public List<HardcoverUserBook> UserBooks { get; set; } = new();
        }

        private sealed class OwnedListResponseData
        {
            [JsonPropertyName("lists")]
            public List<HardcoverList> Lists { get; set; } = new();
        }

        private sealed class HardcoverList
        {
            [JsonPropertyName("id")]
            public int Id { get; set; }
        }

        private sealed class ListBooksResponseData
        {
            [JsonPropertyName("list_books")]
            public List<HardcoverListBook> ListBooks { get; set; } = new();
        }

        private sealed class HardcoverEdition
        {
            [JsonPropertyName("id")]
            public int Id { get; set; }

            [JsonPropertyName("reading_format_id")]
            public int ReadingFormatId { get; set; }

            [JsonPropertyName("edition_format")]
            public string EditionFormat { get; set; }

            [JsonPropertyName("audio_seconds")]
            public int? AudioSeconds { get; set; }
        }

        private sealed class HardcoverUserBook
        {
            [JsonPropertyName("id")]
            public int Id { get; set; }

            [JsonPropertyName("updated_at")]
            public DateTime UpdatedAt { get; set; }

            [JsonPropertyName("status_id")]
            public int StatusId { get; set; }

            [JsonPropertyName("edition_id")]
            public int? EditionId { get; set; }

            [JsonPropertyName("edition")]
            public HardcoverEdition Edition { get; set; }

            [JsonPropertyName("book")]
            public HardcoverBook Book { get; set; }
        }

        private sealed class HardcoverListBook
        {
            [JsonPropertyName("id")]
            public int Id { get; set; }

            [JsonPropertyName("edition_id")]
            public int? EditionId { get; set; }

            [JsonPropertyName("edition")]
            public HardcoverEdition Edition { get; set; }

            [JsonPropertyName("book")]
            public HardcoverBook Book { get; set; }
        }

        private sealed class HardcoverBook
        {
            [JsonPropertyName("id")]
            public int Id { get; set; }

            [JsonPropertyName("canonical_id")]
            public int? CanonicalId { get; set; }

            [JsonPropertyName("title")]
            public string Title { get; set; }

            [JsonPropertyName("subtitle")]
            public string Subtitle { get; set; }

            [JsonPropertyName("image")]
            public HardcoverImage Image { get; set; }

            [JsonPropertyName("contributions")]
            public List<HardcoverContribution> Contributions { get; set; } = new();
        }

        private sealed class HardcoverImage
        {
            [JsonPropertyName("url")]
            public string Url { get; set; }
        }

        private sealed class HardcoverContribution
        {
            [JsonPropertyName("author_id")]
            public int AuthorId { get; set; }

            [JsonPropertyName("contribution")]
            public string Contribution { get; set; }

            [JsonPropertyName("author")]
            public HardcoverAuthor Author { get; set; }
        }

        private sealed class HardcoverAuthor
        {
            [JsonPropertyName("id")]
            public int Id { get; set; }

            [JsonPropertyName("canonical_id")]
            public int? CanonicalId { get; set; }

            [JsonPropertyName("name")]
            public string Name { get; set; }

            [JsonPropertyName("identifiers")]
            public JsonElement Identifiers { get; set; }
        }
    }
}
