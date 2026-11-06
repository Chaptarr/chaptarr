using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using FluentMigrator;
using System.Text.Json;
using System.Threading.Tasks;
using Dapper;
using DryIoc;
using DryIoc.Microsoft.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using NLog;
using NLog.Extensions.Logging;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Composition.Extensions;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Common.Http.Dispatchers;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Common.Options;
using NzbDrone.Common.TPL;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Extensions;
using NzbDrone.Core.Datastore.Migration.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Http;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.SignalR;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using FileSystem = System.IO.Abstractions.FileSystem;
using HttpClient = System.Net.Http.HttpClient;
using PostgresOptions = NzbDrone.Core.Datastore.PostgresOptions;

namespace NzbDrone.MatchBench
{
    internal static class Program
    {
        private static readonly Logger Logger = NzbDroneLogger.GetLogger(typeof(Program));

        private sealed class ProwlarrCategory
        {
            public int Id { get; set; }
            public string? Name { get; set; }
        }

        private sealed class ProwlarrSearchResult
        {
            public string Title { get; set; } = string.Empty;
            public string? Author { get; set; }
            public string? Guid { get; set; }
            public string? DownloadUrl { get; set; }
            public string? Indexer { get; set; }
            public int IndexerId { get; set; }
            public long Size { get; set; }
            public int Seeders { get; set; }
            public int Leechers { get; set; }
            public string? Protocol { get; set; }
            public List<ProwlarrCategory>? Categories { get; set; }
        }

        private sealed class ProwlarrSearchFetchResult
        {
            public List<ProwlarrSearchResult> Results { get; set; } = new();
            public string? Error { get; set; }
        }

        private sealed class LibraryBookRow
        {
            public int BookId { get; set; }
            public string BookTitle { get; set; } = string.Empty;
            public string? BookSubtitle { get; set; }
            public string? OriginalTitle { get; set; }
            public string? EditionTitle { get; set; }
            public string? SeriesName { get; set; }
            public string? SeriesPosition { get; set; }
            public int? PublicationYear { get; set; }
            public BookMediaType MediaType { get; set; }
            public bool AnyEditionOk { get; set; }
            public string? GoodreadsBookId { get; set; }
            public string? GoodreadsWorkId { get; set; }
            public string? HardcoverBookId { get; set; }
            public string? OpenLibraryEditionId { get; set; }
            public string? OpenLibraryWorkId { get; set; }
            public string? GoogleBooksId { get; set; }
            public string? Asin { get; set; }
            public string? AudibleAsin { get; set; }
            public int AuthorId { get; set; }
            public string AuthorName { get; set; } = string.Empty;
            public int? MetadataProfileId { get; set; }
            public int? AudiobookMetadataProfileId { get; set; }
            public int? EbookMetadataProfileId { get; set; }
        }

        private sealed class StoredIndexerRow
        {
            public int Id { get; set; }
            public string Name { get; set; } = string.Empty;
            public string Settings { get; set; } = string.Empty;
        }

        private sealed class StagedRow
        {
            public string Path { get; set; } = string.Empty;
            public long SizeBytes { get; set; }
            public long MtimeNs { get; set; }
            public string TagsJson { get; set; } = string.Empty;
            public int? DurationSeconds { get; set; }
            public string Status { get; set; } = string.Empty;
        }

        private sealed class AuditInputFile
        {
            public int? BookFileId { get; set; }
            public string Path { get; set; } = string.Empty;
            public long SizeBytes { get; set; }
            public DateTime ModifiedUtc { get; set; }
            public Dictionary<string, List<string>> Tags { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public int? DurationSeconds { get; set; }
            public int? CurrentEditionId { get; set; }
            public string? CurrentEditionTitle { get; set; }
            public int? CurrentBookId { get; set; }
            public string? CurrentBookTitle { get; set; }
            public int? CurrentAuthorId { get; set; }
            public string? CurrentAuthorName { get; set; }
            public string? SelectionSource { get; set; }
            public string Source { get; set; } = string.Empty;
            public bool ExistsOnDisk { get; set; }
        }

        private sealed class AuditCurrentMapping
        {
            public int? BookFileId { get; set; }
            public string Path { get; set; } = string.Empty;
            public long SizeBytes { get; set; }
            public DateTime ModifiedUtc { get; set; }
            public string? TagsJson { get; set; }
            public int? DurationSeconds { get; set; }
            public int? CurrentEditionId { get; set; }
            public string? CurrentEditionTitle { get; set; }
            public int? CurrentBookId { get; set; }
            public string? CurrentBookTitle { get; set; }
            public int? CurrentAuthorId { get; set; }
            public string? CurrentAuthorName { get; set; }
            public string? MatchProvenanceJson { get; set; }
        }

        private sealed class AuditReport
        {
            public DateTimeOffset GeneratedAt { get; set; }
            public string SourceAppData { get; set; } = string.Empty;
            public string WorkspaceAppData { get; set; } = string.Empty;
            public bool UsedSnapshot { get; set; }
            public string InputSource { get; set; } = string.Empty;
            public string Prefix { get; set; } = string.Empty;
            public int? SelectedBookFileId { get; set; }
            public int? SelectedBookId { get; set; }
            public bool FtsDetailsRequested { get; set; }
            public int FtsTop { get; set; }
            public int InputCount { get; set; }
            public bool IncludeMissingFiles { get; set; }
            public bool IncludeUserSelectedMappings { get; set; }
            public int ExistingInputCount { get; set; }
            public int MissingInputCount { get; set; }
            public List<AuditRunReport> Runs { get; set; } = new();
        }

        private sealed class EditionAuditInfo
        {
            public int EditionId { get; set; }
            public string? Title { get; set; }
            public string? Subtitle { get; set; }
            public int BookId { get; set; }
            public string? BookTitle { get; set; }
            public int AuthorId { get; set; }
            public string? AuthorName { get; set; }
            public string? ForeignEditionId { get; set; }
            public string? HardcoverEditionId { get; set; }
            public int? GoodreadsEditionId { get; set; }
            public string? OpenLibraryEditionId { get; set; }
            public string? GoogleBooksEditionId { get; set; }
            public string? Asin { get; set; }
            public string? AudibleASIN { get; set; }
            public string? AsinsJson { get; set; }
            public List<string> Asins { get; set; } = new();
            public string? Isbn13 { get; set; }
            public string? Isbn10 { get; set; }
            public string? Narrator { get; set; }
            public string? NarratorNamesJson { get; set; }
            public string? LinkedNarratorNamesJson { get; set; }
            public List<string> Narrators { get; set; } = new();
            public int? DurationSeconds { get; set; }
            public DateTime? ReleaseDate { get; set; }
            public int? ChapterCount { get; set; }
            public bool HasChapters { get; set; }
            public int? ReadingFormatId { get; set; }
            public string? EditionFormat { get; set; }
            public string? Format { get; set; }
            public string? Publisher { get; set; }
            public string? Disambiguation { get; set; }
            public bool IsFallbackEdition { get; set; }
            public string? BookBaseBookId { get; set; }
            public string? SeriesName { get; set; }
            public string? SeriesPosition { get; set; }
        }

        private sealed class BookAuditInfo
        {
            public int BookId { get; set; }
            public string? BaseBookId { get; set; }
            public string? Title { get; set; }
            public int AuthorId { get; set; }
            public string? AuthorName { get; set; }
        }

        private sealed class AuditRunReport
        {
            public string Flow { get; set; } = string.Empty;
            public string Strictness { get; set; } = string.Empty;
            public bool PathFallback { get; set; }
            public int MatchedCount { get; set; }
            public int UnmatchedCount { get; set; }
            public int ChangedCount { get; set; }
            public int PreviouslyMappedNowUnmatchedCount { get; set; }
            public long ElapsedMs { get; set; }
            public bool TraceEnabled { get; set; }
            public int TraceLimit { get; set; }
            public int TraceTotalEventCount { get; set; }
            public int TraceDroppedEventCount { get; set; }
            public bool TraceTruncated { get; set; }
            public List<AuditFileResult> Results { get; set; } = new();
            public List<MatchingTraceEvent> Trace { get; set; } = new();
        }

        private sealed class AuditFileResult
        {
            public int? BookFileId { get; set; }
            public string Path { get; set; } = string.Empty;
            public string Source { get; set; } = string.Empty;
            public long SizeBytes { get; set; }
            public int? FileDurationSeconds { get; set; }
            public List<string> TagSummary { get; set; } = new();
            public Dictionary<string, List<string>>? EvidenceTags { get; set; }
            public int? CurrentEditionId { get; set; }
            public string? CurrentEditionTitle { get; set; }
            public EditionAuditInfo? CurrentEdition { get; set; }
            public List<string> CurrentEditionProviderKeys { get; set; } = new();
            public string? CurrentBookProviderKey { get; set; }
            public string? SelectionSource { get; set; }
            public int? MatchedEditionId { get; set; }
            public string? MatchedBookTitle { get; set; }
            public string? MatchedAuthorName { get; set; }
            public EditionAuditInfo? MatchedEdition { get; set; }
            public List<string> MatchedEditionProviderKeys { get; set; } = new();
            public string? MatchedBookProviderKey { get; set; }
            public List<string> MatchedNarratorsFoundInTags { get; set; } = new();
            public List<string> MatchedNarratorsMissingFromTags { get; set; } = new();
            public string? UnmatchedReason { get; set; }
            public bool ChangedFromCurrent { get; set; }
            public List<string> Flags { get; set; } = new();
            public List<string> Signals { get; set; } = new();
            public List<FtsAttemptSummary> FtsAttempts { get; set; } = new();
        }

        private sealed class FtsAttemptSummary
        {
            private readonly HashSet<string> _step1ProviderBooks = new(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> _expandedProviderBooks = new(StringComparer.OrdinalIgnoreCase);

            public string Path { get; set; } = string.Empty;
            public string Phase { get; set; } = string.Empty;
            public int AttemptIndex { get; set; }
            public string? ExpectedProviderBookKey { get; set; }
            public List<string> Terms { get; set; } = new();
            public string? Step1TopProviderBookKey { get; set; }
            public int? Step1ExpectedProviderBookRank { get; set; }
            public int Step1RawRowCount { get; set; }
            public int Step1DistinctLocalBookCount { get; set; }
            public int Step1DistinctProviderBookCount { get; set; }
            public long? Step1ElapsedMilliseconds { get; set; }
            public string? ExpansionTopProviderBookKey { get; set; }
            public int? ExpansionExpectedProviderBookRank { get; set; }
            public int ExpansionEditionRowCount { get; set; }
            public int ExpansionDistinctLocalBookCount { get; set; }
            public int ExpansionDistinctProviderBookCount { get; set; }
            public long? ExpansionElapsedMilliseconds { get; set; }
            public string? ProductionTopProviderBookKey { get; set; }
            public int? ProductionExpectedProviderBookRank { get; set; }
            public int ProductionEligibleCandidateCount { get; set; }
            public int CapturedRejectedCandidateCount { get; set; }
            public int? SelectedEditionId { get; set; }
            public int? SelectedBookId { get; set; }
            public string? SelectionReason { get; set; }
            public long? TotalFtsMilliseconds { get; set; }
            public string? ResultSource { get; set; }

            public void ObserveFtsCandidate(string step, string providerBookKey)
            {
                if (string.Equals(step, "step1", StringComparison.OrdinalIgnoreCase))
                {
                    Step1RawRowCount++;
                    if (_step1ProviderBooks.Add(providerBookKey))
                    {
                        Step1DistinctProviderBookCount = _step1ProviderBooks.Count;
                        Step1TopProviderBookKey ??= providerBookKey;
                        if (!string.IsNullOrWhiteSpace(ExpectedProviderBookKey) &&
                            string.Equals(providerBookKey, ExpectedProviderBookKey, StringComparison.OrdinalIgnoreCase))
                        {
                            Step1ExpectedProviderBookRank = _step1ProviderBooks.Count;
                        }
                    }

                    return;
                }

                ExpansionEditionRowCount++;
                if (_expandedProviderBooks.Add(providerBookKey))
                {
                    ExpansionDistinctProviderBookCount = _expandedProviderBooks.Count;
                    ExpansionTopProviderBookKey ??= providerBookKey;
                    if (!string.IsNullOrWhiteSpace(ExpectedProviderBookKey) &&
                        string.Equals(providerBookKey, ExpectedProviderBookKey, StringComparison.OrdinalIgnoreCase))
                    {
                        ExpansionExpectedProviderBookRank = _expandedProviderBooks.Count;
                    }
                }
            }
        }

        private sealed class TraceCollector : IMatchingTraceSink
        {
            private readonly int _limit;
            private readonly IReadOnlyDictionary<string, string> _expectedProviderBookByPath;
            private readonly IReadOnlyDictionary<int, string> _providerBookByLocalBookId;
            private readonly Dictionary<string, FtsAttemptSummary> _currentAttemptByPath = new(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, List<FtsAttemptSummary>> _attemptsByPath = new(StringComparer.OrdinalIgnoreCase);
            private readonly object _gate = new();

            public TraceCollector(
                int limit,
                IReadOnlyDictionary<string, string> expectedProviderBookByPath,
                IReadOnlyDictionary<int, string> providerBookByLocalBookId)
            {
                _limit = Math.Max(0, limit);
                _expectedProviderBookByPath = expectedProviderBookByPath ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _providerBookByLocalBookId = providerBookByLocalBookId ?? new Dictionary<int, string>();
            }

            public List<MatchingTraceEvent> Events { get; } = new();
            public int Limit => _limit;
            public int TotalEventCount { get; private set; }
            public int DroppedEventCount { get; private set; }
            public bool Truncated => DroppedEventCount > 0;

            public void Record(MatchingTraceEvent evt)
            {
                if (evt == null)
                {
                    return;
                }

                lock (_gate)
                {
                    ObserveCompactFtsSummary(evt);

                    TotalEventCount++;
                    if (Events.Count >= _limit)
                    {
                        DroppedEventCount++;
                        return;
                    }

                    Events.Add(evt);
                }
            }

            public List<FtsAttemptSummary> GetAttempts(string path)
            {
                lock (_gate)
                {
                    return _attemptsByPath.TryGetValue(path ?? string.Empty, out var attempts)
                        ? attempts.ToList()
                        : new List<FtsAttemptSummary>();
                }
            }

            private void ObserveCompactFtsSummary(MatchingTraceEvent evt)
            {
                var path = evt.FilePath ?? string.Empty;
                if (string.Equals(evt.EventType, "fts_input", StringComparison.OrdinalIgnoreCase))
                {
                    if (!_attemptsByPath.TryGetValue(path, out var attempts))
                    {
                        attempts = new List<FtsAttemptSummary>();
                        _attemptsByPath[path] = attempts;
                    }

                    _expectedProviderBookByPath.TryGetValue(path, out var expectedProviderBookKey);
                    var attempt = new FtsAttemptSummary
                    {
                        Path = path,
                        Phase = evt.Phase ?? string.Empty,
                        AttemptIndex = attempts.Count + 1,
                        ExpectedProviderBookKey = expectedProviderBookKey,
                        Terms = evt.Terms?.ToList() ?? new List<string>()
                    };
                    attempts.Add(attempt);
                    _currentAttemptByPath[path] = attempt;
                    return;
                }

                if (!_currentAttemptByPath.TryGetValue(path, out var current))
                {
                    return;
                }

                var isBookRecallCandidate =
                    string.Equals(evt.EventType, "fts_step1_book_recall_candidate", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(evt.EventType, "fts_stage1_book_recall_candidate", StringComparison.OrdinalIgnoreCase);
                var isEditionRankingCandidate =
                    string.Equals(evt.EventType, "fts_edition_expansion_candidate", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(evt.EventType, "fts_stage2_field_ranking_candidate", StringComparison.OrdinalIgnoreCase);
                if (isBookRecallCandidate || isEditionRankingCandidate)
                {
                    var providerBookKey = ProviderBookKey(evt.BookId);
                    current.ObserveFtsCandidate(
                        isBookRecallCandidate ? "step1" : "expansion",
                        providerBookKey);
                    return;
                }

                if (string.Equals(evt.EventType, "fts_step1_book_recall_summary", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(evt.EventType, "fts_stage1_book_recall_summary", StringComparison.OrdinalIgnoreCase))
                {
                    current.Step1ElapsedMilliseconds = evt.ElapsedMilliseconds;
                    current.Step1RawRowCount = evt.ResultCount ?? current.Step1RawRowCount;
                    current.Step1DistinctLocalBookCount = evt.DistinctBookCount ?? 0;
                    return;
                }

                if (string.Equals(evt.EventType, "fts_edition_expansion_summary", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(evt.EventType, "fts_stage2_field_ranking_summary", StringComparison.OrdinalIgnoreCase))
                {
                    current.ExpansionElapsedMilliseconds = evt.ElapsedMilliseconds;
                    current.ExpansionEditionRowCount = evt.ResultCount ?? current.ExpansionEditionRowCount;
                    current.ExpansionDistinctLocalBookCount = evt.DistinctBookCount ?? 0;
                    return;
                }

                if (string.Equals(evt.EventType, "candidate_ranked", StringComparison.OrdinalIgnoreCase) &&
                    TraceData(evt, "selectionScope").StartsWith("global-", StringComparison.OrdinalIgnoreCase))
                {
                    current.ProductionEligibleCandidateCount++;
                    var providerBookKey = ProviderBookKey(evt.BookId);
                    if ((evt.Rank ?? int.MaxValue) == 1)
                    {
                        current.ProductionTopProviderBookKey = providerBookKey;
                    }

                    if (!string.IsNullOrWhiteSpace(current.ExpectedProviderBookKey) &&
                        string.Equals(providerBookKey, current.ExpectedProviderBookKey, StringComparison.OrdinalIgnoreCase))
                    {
                        current.ProductionExpectedProviderBookRank ??= evt.Rank;
                    }

                    return;
                }

                if (string.Equals(evt.EventType, "candidate_rejected", StringComparison.OrdinalIgnoreCase))
                {
                    current.CapturedRejectedCandidateCount++;
                    return;
                }

                if (string.Equals(evt.EventType, "match_selected", StringComparison.OrdinalIgnoreCase))
                {
                    current.SelectedEditionId = evt.EditionId;
                    current.SelectedBookId = evt.BookId;
                    current.SelectionReason = evt.Reason;
                    return;
                }

                if (string.Equals(evt.EventType, "fts_completed", StringComparison.OrdinalIgnoreCase))
                {
                    current.TotalFtsMilliseconds = evt.TotalElapsedMilliseconds;
                    current.ResultSource = TraceData(evt, "resultSource");
                }
            }

            private string ProviderBookKey(int? localBookId)
            {
                if (localBookId.HasValue &&
                    _providerBookByLocalBookId.TryGetValue(localBookId.Value, out var providerBookKey) &&
                    !string.IsNullOrWhiteSpace(providerBookKey))
                {
                    return providerBookKey;
                }

                return localBookId.HasValue
                    ? $"local-book:{localBookId.Value.ToString(CultureInfo.InvariantCulture)}"
                    : "unknown-book";
            }
        }

        private sealed class FtsAttemptTrace
        {
            public string Phase { get; set; } = string.Empty;
            public List<MatchingTraceEvent> Events { get; set; } = new();
        }

        private sealed class AuditWorkspace : IDisposable
        {
            public string SourceAppData { get; set; } = string.Empty;
            public string AppData { get; set; } = string.Empty;
            public bool IsTemporary { get; set; }
            public StartupContext StartupContext { get; set; } = null!;

            public void Dispose()
            {
                if (!IsTemporary || string.IsNullOrWhiteSpace(AppData))
                {
                    return;
                }

                try
                {
                    Directory.Delete(AppData, recursive: true);
                }
                catch
                {
                    // Best-effort cleanup; leaving a temp audit snapshot is safer than failing the run.
                }
            }
        }

        private sealed class NoOpSignalRBroadcaster : IBroadcastSignalRMessage
        {
            public bool IsConnected => false;

            public System.Threading.Tasks.Task BroadcastMessage(SignalRMessage message)
            {
                return System.Threading.Tasks.Task.CompletedTask;
            }
        }

        internal sealed class OfflineReplayHttpDispatcher : IHttpDispatcher
        {
            private readonly ConcurrentQueue<string> _blockedRequests = new();

            public int BlockedRequestCount => _blockedRequests.Count;
            public IReadOnlyList<string> BlockedRequests => _blockedRequests.ToArray();

            public Task<HttpResponse> GetResponseAsync(HttpRequest request, CookieContainer cookies)
            {
                var destination = request?.Url?.Host ?? "unknown-host";
                var method = request?.Method?.Method ?? "unknown-method";
                _blockedRequests.Enqueue($"{method} {destination}");
                throw new HttpRequestException("MatchBench decision replay blocks outbound HTTP requests.");
            }
        }

        internal sealed class OfflineReplayIndexerHttpClient : NzbDrone.Common.Http.HttpClient, IIndexerHttpClient, IIndexerHttpClientFactory
        {
            public OfflineReplayIndexerHttpClient(IEnumerable<IHttpRequestInterceptor> requestInterceptors,
                ICacheManager cacheManager, IRateLimitService rateLimitService, IHttpDispatcher dispatcher, Logger logger)
                : base(requestInterceptors, cacheManager, rateLimitService, dispatcher, logger)
            {
            }

            public IIndexerHttpClient GetClient(int? proxyId) => this;
        }

        internal sealed class ProductionSearchDecisionReplay
        {
            public List<DownloadDecision> Decisions { get; init; } = new();
            public int BlockedExternalRequestCount { get; init; }
            public IReadOnlyList<string> BlockedRequests { get; init; } = Array.Empty<string>();
            public bool UsedSyntheticInput { get; init; }
            public bool HasCompleteSourceData { get; init; } = true;
            public int UnaccountedReportCount { get; init; }
            public ProcessedDecisions? Processed { get; init; }
            public bool Complete => BlockedExternalRequestCount == 0 && !UsedSyntheticInput && HasCompleteSourceData && UnaccountedReportCount == 0;
        }

        internal sealed class ProductionSearchInput
        {
            public Author Author { get; init; } = null!;
            public Book TargetBook { get; init; } = null!;
            public List<Book> AuthorCatalog { get; init; } = new();
            public BookSearchCriteria Criteria { get; init; } = null!;
            public bool IsSynthetic { get; init; }
        }

        private sealed class ProductionSearchInputLoader
        {
            private readonly IBookService _bookService;
            private readonly IAuthorService _authorService;
            private readonly ReleaseSearchService _searchService;
            private readonly Dictionary<int, List<Book>> _authorCatalogById = new();

            public ProductionSearchInputLoader(IServiceProvider services)
            {
                _bookService = services.GetRequiredService<IBookService>();
                _authorService = services.GetRequiredService<IAuthorService>();
                _searchService = services.GetRequiredService<ReleaseSearchService>();
            }

            public ProductionSearchInput Load(LibraryBookRow row, bool userInvokedSearch, bool interactiveSearch)
            {
                var isSynthetic = row.BookId <= 0;
                Book targetBook;
                Author author;
                List<Book> authorCatalog;

                if (isSynthetic)
                {
                    author = new Author { Id = row.AuthorId, Name = row.AuthorName };
                    targetBook = BuildBook(row, author);
                    authorCatalog = new List<Book> { targetBook };
                }
                else
                {
                    targetBook = _bookService.GetBook(row.BookId)
                        ?? throw new InvalidOperationException($"Could not hydrate local book {row.BookId} for production search replay.");
                    author = targetBook.Author ?? _authorService.GetAuthor(targetBook.AuthorId);
                    targetBook.Author = author;

                    if (!_authorCatalogById.TryGetValue(author.Id, out var cachedCatalog))
                    {
                        cachedCatalog = _bookService.GetBooksByAuthor(author.Id, onlyMonitoredEditions: true);
                        _authorCatalogById[author.Id] = cachedCatalog;
                    }

                    authorCatalog = cachedCatalog;
                }

                var criteria = _searchService.CreateBookSearchCriteria(
                    author, targetBook, authorCatalog, userInvokedSearch, interactiveSearch);

                return new ProductionSearchInput
                {
                    Author = author,
                    TargetBook = targetBook,
                    AuthorCatalog = authorCatalog,
                    Criteria = criteria,
                    IsSynthetic = isSynthetic
                };
            }
        }

        internal static ProductionSearchDecisionReplay RunProductionSearchDecision(
            ReleaseSearchService searchService,
            IPrioritizeDownloadDecision prioritizer,
            OfflineReplayHttpDispatcher networkGuard,
            IReadOnlyCollection<ReleaseInfo> reports,
            BookSearchCriteria criteria,
            bool usedSyntheticInput = false,
            bool hasCompleteSourceData = true)
        {
            return RunProductionDecision(
                () => searchService.EvaluateSearchReports(reports?.ToList() ?? new List<ReleaseInfo>(), criteria),
                prioritizer, networkGuard, usedSyntheticInput, hasCompleteSourceData);
        }

        private sealed class RssReplayInputs : IFetchAndParseRss, IMakeDownloadDecision
        {
            private readonly List<ReleaseInfo> _freshReports;
            private readonly IMakeDownloadDecision _decisionMaker;
            public List<ReleaseInfo> Reports { get; private set; } = new();
            public List<DownloadDecision> Decisions { get; private set; } = new();

            public RssReplayInputs(IReadOnlyCollection<ReleaseInfo> reports, IMakeDownloadDecision decisionMaker)
            {
                _freshReports = reports.ToList();
                _decisionMaker = decisionMaker;
            }

            public Task<List<ReleaseInfo>> Fetch() => Task.FromResult(_freshReports);

            public List<DownloadDecision> GetRssDecision(List<ReleaseInfo> reports, bool pushedRelease = false)
                => GetRssDecision(reports, pushedRelease, null!);

            public List<DownloadDecision> GetRssDecision(List<ReleaseInfo> reports, bool pushedRelease,
                IReadOnlyDictionary<(int AuthorId, int BookId), DateTime> oldestPublishDates)
            {
                Reports = reports;
                Decisions = _decisionMaker.GetRssDecision(reports, pushedRelease, oldestPublishDates);
                return Decisions;
            }

            public List<DownloadDecision> GetSearchDecision(List<ReleaseInfo> reports, SearchCriteriaBase criteria)
                => _decisionMaker.GetSearchDecision(reports, criteria);
        }

        internal sealed class ReplayDownloadService : IDownloadService
        {
            public List<RemoteBook> Planned { get; } = new();

            public Task DownloadReport(RemoteBook remoteBook, int? downloadClientId)
            {
                Planned.Add(remoteBook);
                return Task.CompletedTask;
            }
        }

        internal static ProductionSearchDecisionReplay RunProductionRssDecision(
            IMakeDownloadDecision decisionMaker,
            IPrioritizeDownloadDecision prioritizer,
            OfflineReplayHttpDispatcher networkGuard,
            IReadOnlyCollection<ReleaseInfo> reports,
            IPendingReleaseService pendingReleaseService,
            IProcessDownloadDecisions processor,
            bool hasCompleteSourceData = true)
        {
            var previousAttempts = networkGuard.BlockedRequestCount;
            var inputs = new RssReplayInputs(reports, decisionMaker);
            var rss = new RssSyncService(null!, null!, inputs, inputs, processor, pendingReleaseService, null!, Logger);
            var processed = rss.Sync().GetAwaiter().GetResult();
            var represented = inputs.Decisions.Select(decision => decision.RemoteBook?.Release).ToHashSet();
            var blocked = networkGuard.BlockedRequests.Skip(previousAttempts).ToList();
            return new ProductionSearchDecisionReplay
            {
                Decisions = prioritizer.PrioritizeDecisions(inputs.Decisions),
                Processed = processed,
                BlockedExternalRequestCount = blocked.Count,
                BlockedRequests = blocked,
                HasCompleteSourceData = hasCompleteSourceData,
                UnaccountedReportCount = inputs.Reports.Count(report => !represented.Contains(report))
            };
        }

        private static ProductionSearchDecisionReplay RunProductionDecision(
            Func<List<DownloadDecision>> evaluate,
            IPrioritizeDownloadDecision prioritizer,
            OfflineReplayHttpDispatcher networkGuard,
            bool usedSyntheticInput,
            bool hasCompleteSourceData)
        {
            var previousAttempts = networkGuard.BlockedRequestCount;
            var prioritized = prioritizer.PrioritizeDecisions(evaluate());
            var blockedRequests = networkGuard.BlockedRequests.Skip(previousAttempts).ToList();

            return new ProductionSearchDecisionReplay
            {
                Decisions = prioritized,
                BlockedExternalRequestCount = blockedRequests.Count,
                BlockedRequests = blockedRequests,
                UsedSyntheticInput = usedSyntheticInput,
                HasCompleteSourceData = hasCompleteSourceData
            };
        }

        private static string DescribeReplayOutcome(ProductionSearchDecisionReplay replay)
        {
            if (!replay.Complete)
            {
                return $"decisions={replay.Decisions.Count} verdicts=withheld audit=INCOMPLETE";
            }

            var matched = replay.Decisions.Count(decision => decision.RemoteBook?.SearchCriteriaMatch?.IsMatch == true);
            var approved = replay.Decisions.Count(decision => decision.Approved);
            return $"decisions={replay.Decisions.Count} titleMatched={matched} approved={approved} rejected={replay.Decisions.Count - approved} audit=complete";
        }

        public static int Main(string[] args)
        {
            var startupContext = new StartupContext(args);
            var flags = startupContext.Flags ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!flags.Contains("matchbench") && !flags.Contains("prowlarrbench") && !flags.Contains("releasereplay") && !flags.Contains("mambench") && !flags.Contains("libraryaudit"))
            {
                PrintUsage();
                return 2;
            }

            NzbDroneLogger.Register(startupContext, false, true);

            if (flags.Contains("prowlarrbench"))
            {
                return RunProwlarrBench(startupContext);
            }

            if (flags.Contains("releasereplay"))
            {
                return RunReleaseReplay(startupContext);
            }

            if (flags.Contains("mambench"))
            {
                return RunMamBench(startupContext);
            }

            if (flags.Contains("libraryaudit"))
            {
                return RunLibraryAudit(startupContext);
            }

            var prefix = GetArg(startupContext, "prefix");
            if (string.IsNullOrWhiteSpace(prefix))
            {
                System.Console.Error.WriteLine("Missing required argument: /prefix=<path>");
                PrintUsage();
                return 2;
            }

            var limit = GetIntArg(startupContext, "limit", 250);
            var repeat = Math.Max(1, GetIntArg(startupContext, "repeat", 1));
            var restrictToAuthorId = GetNullableIntArg(startupContext, "authorid");

            var allowV5 = GetBoolArg(startupContext, "allowv5", false);
            var allowAuthorImport = GetBoolArg(startupContext, "allowimport", false);
            var allowUnscopedFallback = GetBoolArg(startupContext, "unscopedfallback", false);
            var deferUnmatched = GetBoolArg(startupContext, "defer", false);
            var perFileMatching = GetBoolArg(startupContext, "perfile", false);
            var requireTags = GetBoolArg(startupContext, "requiretags", true);

            System.Console.WriteLine($"[matchbench] prefix='{prefix}', limit={limit}, repeat={repeat}, authorId={(restrictToAuthorId.HasValue ? restrictToAuthorId.Value.ToString(CultureInfo.InvariantCulture) : "null")}");
            System.Console.WriteLine($"[matchbench] allowV5={allowV5}, allowImport={allowAuthorImport}, defer={deferUnmatched}, unscopedFallback={allowUnscopedFallback}, perFile={perFileMatching}");

            using var host = BuildHost(startupContext);
            using var scope = host.Services.CreateScope();
            var services = scope.ServiceProvider;

            var stagedRows = LoadStagedRows(services, prefix, limit, requireTags);
            if (stagedRows.Count == 0)
            {
                System.Console.Error.WriteLine("[matchbench] No staged rows found for prefix (or all rows had empty tags).");
                return 1;
            }

            var discovered = BuildDiscoveredFiles(stagedRows, requireTags);
            if (discovered.Count == 0)
            {
                System.Console.Error.WriteLine("[matchbench] No usable discovered files (all tags empty?).");
                return 1;
            }

            var matcher = services.GetRequiredService<IFileMatchingService>();
            var matchingLogger = services.GetRequiredService<IMatchingUploadLogger>();
            var ctx = new MatchingContext
            {
                AllowV5Identification = allowV5,
                AllowAuthorImport = allowAuthorImport,
                DeferUnmatchedToAuthorReady = deferUnmatched,
                AllowUnscopedFallback = allowUnscopedFallback,
                PerFileMatching = perFileMatching
            };

            var timingsMs = new List<long>();
            FileMatchResult? last = null;
            var runStartedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            for (var i = 0; i < repeat; i++)
            {
                var sw = Stopwatch.StartNew();
                last = matcher.MatchFilesToLibraryAsync(discovered.ToArray(), restrictToAuthorId: restrictToAuthorId, context: ctx)
                    .GetAwaiter()
                    .GetResult();
                sw.Stop();

                timingsMs.Add(sw.ElapsedMilliseconds);
                System.Console.WriteLine($"[matchbench] run={i + 1}/{repeat} matched={last.MatchedFiles?.Length ?? 0} unmatched={last.UnmatchedFiles?.Length ?? 0} time={sw.ElapsedMilliseconds}ms");
            }

            if (timingsMs.Count > 0)
            {
                timingsMs.Sort();
                System.Console.WriteLine();
                System.Console.WriteLine("[matchbench] ===== summary =====");
                System.Console.WriteLine($"[matchbench] stagedRows={stagedRows.Count} discoveredFiles={discovered.Count}");
                System.Console.WriteLine($"[matchbench] ms: min={timingsMs.First()} p50={Percentile(timingsMs, 0.50):F0} p95={Percentile(timingsMs, 0.95):F0} max={timingsMs.Last()}");
            }

            if (last != null)
            {
                var matchedPreview = last.MatchedFiles?
                    .Where(m => m?.File?.Path != null)
                    .Take(20)
                    .ToList() ?? new List<FileMatch>();

                if (matchedPreview.Count > 0)
                {
                    System.Console.WriteLine();
                    System.Console.WriteLine("[matchbench] Matched preview:");
                    foreach (var match in matchedPreview)
                    {
                        System.Console.WriteLine($"  - MATCHED {match.File.Path}");
                        System.Console.WriteLine($"      author={match.AuthorName} localBookId={match.BookId} localEditionId={match.EditionId} book='{match.BookTitle}'");
                    }
                }

                var unmatchedPreview = last.UnmatchedFiles?
                    .Where(u => u?.File?.Path != null)
                    .Take(20)
                    .ToList() ?? new List<UnmatchedFile>();

                if (unmatchedPreview.Count > 0)
                {
                    System.Console.WriteLine();
                    System.Console.WriteLine("[matchbench] Unmatched preview:");
                    foreach (var unmatched in unmatchedPreview)
                    {
                        System.Console.WriteLine($"  - UNMATCHED {unmatched.File.Path}");
                        System.Console.WriteLine($"      reason={unmatched.Reason}");
                    }
                }
            }

            var decisionLogs = matchingLogger
                .GetRecentLogs(Math.Max(100, discovered.Count * 4))
                .Where(e => e != null && e.Timestamp >= runStartedAt && e.MatchResult?.Decision != null)
                .OrderBy(e => e.Timestamp)
                .ToList();

            if (decisionLogs.Count > 0)
            {
                System.Console.WriteLine();
                System.Console.WriteLine("[matchbench] Decision log:");
                foreach (var entry in decisionLogs.Take(100))
                {
                    var result = entry.MatchResult;
                    System.Console.WriteLine($"  - {result.Decision} {entry.FileName}");
                    System.Console.WriteLine($"      reason={result.Reason}");

                    if (!string.IsNullOrWhiteSpace(result.AuthorMatched) ||
                        !string.IsNullOrWhiteSpace(result.BookMatched) ||
                        !string.IsNullOrWhiteSpace(result.EditionMatched))
                    {
                        System.Console.WriteLine($"      author={result.AuthorMatched ?? "-"} book={result.BookMatched ?? "-"} edition={result.EditionMatched ?? "-"}");
                    }

                    var rejections = result.Rejections?.Take(5).ToList();
                    if (rejections != null && rejections.Count > 0)
                    {
                        foreach (var rejection in rejections)
                        {
                            var detail = string.IsNullOrWhiteSpace(rejection.Detail) ? string.Empty : $" detail={rejection.Detail}";
                            System.Console.WriteLine($"      reject phase={rejection.Phase} reason={rejection.Reason}{detail}");
                        }
                    }
                }
            }

            return 0;
        }

        private static int RunLibraryAudit(StartupContext startupContext)
        {
            var sourceAppData = GetArg(startupContext, "data") ??
                                Environment.GetEnvironmentVariable("CHAPTARR_APPDATA") ??
                                "/config";
            var prefix = GetArg(startupContext, "prefix") ?? "/audiobooks";
            var limit = Math.Max(1, GetIntArg(startupContext, "limit", 250));
            var inputSource = (GetArg(startupContext, "source") ?? "bookfiles").Trim().ToLowerInvariant();
            var requireTags = GetBoolArg(startupContext, "requiretags", inputSource != "bookfiles");
            var includeMissing = GetBoolArg(startupContext, "includemissing", false);
            var mappedOnly = GetBoolArg(startupContext, "mappedonly", false);
            var unmappedOnly = GetBoolArg(startupContext, "unmappedonly", false);
            var live = GetBoolArg(startupContext, "live", false);
            var allowMutations = GetBoolArg(startupContext, "allowmutations", false);
            var restrictToAuthorId = GetNullableIntArg(startupContext, "authorid");
            var traceEnabled = GetBoolArg(startupContext, "trace", true);
            var traceLimit = Math.Max(0, GetIntArg(startupContext, "tracelimit", 5000));
            var selectedBookFileId = GetNullableIntArg(startupContext, "bookfileid");
            var selectedBookId = GetNullableIntArg(startupContext, "bookid");
            var includeUserSelected = GetBoolArg(
                startupContext,
                "includeuserselected",
                selectedBookFileId.HasValue || selectedBookId.HasValue);
            var ftsTop = Math.Max(1, GetIntArg(startupContext, "ftstop", 25));
            var ftsDetailsRequested = selectedBookFileId.HasValue || selectedBookId.HasValue;
            var outputPath = GetArg(startupContext, "out");

            if (mappedOnly && unmappedOnly)
            {
                System.Console.Error.WriteLine("[libraryaudit] /mappedonly and /unmappedonly cannot both be true.");
                return 2;
            }

            if (selectedBookFileId.HasValue && selectedBookId.HasValue)
            {
                System.Console.Error.WriteLine("[libraryaudit] /bookfileid and /bookid are alternative local forensic selectors; use only one.");
                return 2;
            }

            if (ftsDetailsRequested && !string.Equals(inputSource, "bookfiles", StringComparison.OrdinalIgnoreCase))
            {
                System.Console.Error.WriteLine("[libraryaudit] /bookfileid and /bookid require /source=bookfiles because they replay BookFiles.AllTags exactly.");
                return 2;
            }

            if (ftsDetailsRequested && !traceEnabled)
            {
                System.Console.Error.WriteLine("[libraryaudit] exact FTS decision reports require /trace=true.");
                return 2;
            }

            var flows = ParseAuditFlows(GetArg(startupContext, "flow") ?? "scan-local");
            var strictnessValues = ParseStrictnessValues(GetArg(startupContext, "strictness") ?? "current");
            var pathFallbackValues = ParsePathFallbackValues(GetArg(startupContext, "pathfallback") ?? "current");
            var targetBookIds = ParseTargetBookIds(GetArg(startupContext, "targetbookids"));

            if (live && !allowMutations && flows.Any(IsPotentiallyMutatingAuditFlow))
            {
                System.Console.Error.WriteLine("[libraryaudit] /live=true cannot run author-ready or direct-default without /allowmutations=true because those production contexts may import or refresh metadata. Omit /live to use the safe snapshot.");
                return 2;
            }

            if (RejectPostgresForSqliteSnapshot(startupContext, "libraryaudit"))
            {
                return 2;
            }

            if (live)
            {
                try
                {
                    ValidateLiveAuditSchema(Path.Combine(sourceAppData, "chaptarr.db"));
                }
                catch (Exception ex)
                {
                    System.Console.Error.WriteLine($"[libraryaudit] Live schema check failed: {ex.Message}. Use the isolated snapshot (omit /live).");
                    return 2;
                }
            }

            using var workspace = PrepareAuditWorkspace(startupContext, sourceAppData, live);
            var dbPath = Path.Combine(workspace.AppData, "chaptarr.db");

            var selector = selectedBookFileId.HasValue
                ? $" local-bookfile={selectedBookFileId.Value.ToString(CultureInfo.InvariantCulture)}"
                : selectedBookId.HasValue
                    ? $" local-book={selectedBookId.Value.ToString(CultureInfo.InvariantCulture)}"
                    : string.Empty;
            System.Console.WriteLine($"[libraryaudit] source={inputSource}, prefix='{prefix}', limit={limit}, includeMissing={includeMissing}, includeUserSelected={includeUserSelected}, flows={string.Join(",", flows)}, strictness={string.Join(",", strictnessValues.Select(v => v?.ToString() ?? "current"))}, pathFallback={string.Join(",", pathFallbackValues.Select(v => v?.ToString() ?? "current"))}{selector}");
            System.Console.WriteLine($"[libraryaudit] appdata='{workspace.AppData}' ({(workspace.IsTemporary ? "snapshot" : "live")})");

            using var host = BuildHost(workspace.StartupContext, blockOutboundHttp: GetBoolArg(startupContext, "offline", false));
            using var scope = host.Services.CreateScope();
            var services = scope.ServiceProvider;

            var inputs = inputSource switch
            {
                "bookfiles" => LoadBookFileAuditInputs(dbPath, prefix, limit, requireTags, includeMissing, mappedOnly, unmappedOnly, includeUserSelected, selectedBookFileId, selectedBookId),
                "staging" => LoadStagingAuditInputs(services, dbPath, prefix, limit, requireTags, includeMissing, mappedOnly, unmappedOnly),
                "disk" => LoadDiskAuditInputs(services, dbPath, prefix, limit, requireTags, includeMissing, mappedOnly, unmappedOnly),
                _ => throw new ArgumentException($"Unknown /source='{inputSource}'. Use bookfiles, staging, or disk.")
            };

            if (!includeUserSelected)
            {
                inputs = inputs
                    .Where(input => !IsUserSelectedMapping(input.SelectionSource))
                    .ToList();
            }

            if (inputs.Count == 0)
            {
                System.Console.Error.WriteLine("[libraryaudit] No input files found.");
                return 1;
            }

            var discovered = BuildDiscoveredFilesFromAuditInputs(inputs, requireTags);
            if (discovered.Count == 0)
            {
                System.Console.Error.WriteLine("[libraryaudit] No usable discovered files after tag filtering.");
                return 1;
            }

            var providerBookByLocalBookId = LoadAllBookProviderKeys(dbPath);
            var expectedProviderBookByPath = inputs
                .Where(input => !string.IsNullOrWhiteSpace(input.Path) && input.CurrentBookId.HasValue)
                .Select(input => new
                {
                    input.Path,
                    ProviderKey = providerBookByLocalBookId.TryGetValue(input.CurrentBookId!.Value, out var providerKey)
                        ? providerKey
                        : $"local-book:{input.CurrentBookId.Value.ToString(CultureInfo.InvariantCulture)}"
                })
                .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().ProviderKey, StringComparer.OrdinalIgnoreCase);

            var matcher = services.GetRequiredService<IFileMatchingService>();
            var configService = services.GetRequiredService<IConfigService>();
            var originalStrictness = configService.BookMatchingStrictness;
            var originalPathFallback = configService.UsePathAsTagsFallback;

            var report = new AuditReport
            {
                GeneratedAt = DateTimeOffset.UtcNow,
                SourceAppData = Path.GetFullPath(sourceAppData),
                WorkspaceAppData = workspace.AppData,
                UsedSnapshot = workspace.IsTemporary,
                InputSource = inputSource,
                Prefix = prefix,
                SelectedBookFileId = selectedBookFileId,
                SelectedBookId = selectedBookId,
                FtsDetailsRequested = ftsDetailsRequested,
                FtsTop = ftsTop,
                InputCount = discovered.Count,
                IncludeMissingFiles = includeMissing,
                IncludeUserSelectedMappings = includeUserSelected,
                ExistingInputCount = inputs.Count(input => input.ExistsOnDisk),
                MissingInputCount = inputs.Count(input => !input.ExistsOnDisk)
            };

            foreach (var strictness in strictnessValues)
            {
                if (strictness.HasValue)
                {
                    configService.BookMatchingStrictness = strictness.Value;
                }
                else
                {
                    configService.BookMatchingStrictness = originalStrictness;
                }

                foreach (var pathFallback in pathFallbackValues)
                {
                    if (pathFallback.HasValue)
                    {
                        configService.UsePathAsTagsFallback = pathFallback.Value;
                    }
                    else
                    {
                        configService.UsePathAsTagsFallback = originalPathFallback;
                    }

                    foreach (var flow in flows)
                    {
                        var trace = traceEnabled
                            ? new TraceCollector(traceLimit, expectedProviderBookByPath, providerBookByLocalBookId)
                            : null;
                        var context = BuildAuditMatchingContext(flow, targetBookIds, pathFallback ?? configService.UsePathAsTagsFallback, trace);
                        var sw = Stopwatch.StartNew();
                        var matchResult = matcher.MatchFilesToLibraryAsync(discovered.ToArray(), restrictToAuthorId, context)
                            .GetAwaiter()
                            .GetResult();
                        sw.Stop();

                        var run = AnalyzeAuditRun(
                            dbPath,
                            flow,
                            configService.BookMatchingStrictness,
                            configService.UsePathAsTagsFallback,
                            inputs,
                            matchResult,
                            trace,
                            sw.ElapsedMilliseconds,
                            ftsDetailsRequested);
                        report.Runs.Add(run);

                        System.Console.WriteLine($"[libraryaudit] flow={run.Flow} strictness={run.Strictness} pathFallback={run.PathFallback} matched={run.MatchedCount} unmatched={run.UnmatchedCount} changed={run.ChangedCount} mapped-now-unmatched={run.PreviouslyMappedNowUnmatchedCount} time={run.ElapsedMs}ms");
                        PrintFtsRecallSummary(run);
                        if (run.TraceTruncated)
                        {
                            System.Console.Error.WriteLine($"[libraryaudit] WARNING: matching trace truncated at {run.TraceLimit} retained events; {run.TraceDroppedEventCount} of {run.TraceTotalEventCount} events were omitted.");
                        }

                        foreach (var changed in run.Results.Where(r => r.ChangedFromCurrent || r.Flags.Count > 0).Take(10))
                        {
                            var flags = changed.Flags.Count > 0 ? $" flags=[{string.Join(",", changed.Flags)}]" : string.Empty;
                            System.Console.WriteLine($"  - {Path.GetFileName(changed.Path)} local-current-edition={changed.CurrentEditionId?.ToString(CultureInfo.InvariantCulture) ?? "-"} -> local-matched-edition={changed.MatchedEditionId?.ToString(CultureInfo.InvariantCulture) ?? "-"}{flags}");
                        }

                        if (ftsDetailsRequested)
                        {
                            PrintFtsDecisionAudit(run, ftsTop);
                        }
                    }
                }
            }

            configService.BookMatchingStrictness = originalStrictness;
            configService.UsePathAsTagsFallback = originalPathFallback;

            WriteAuditReport(report, outputPath);
            return 0;
        }

        internal static void ValidateLiveAuditSchema(string databasePath)
        {
            var expected = typeof(NzbDroneMigrationBase).Assembly.GetTypes()
                .Where(type => type.Namespace == "NzbDrone.Core.Datastore.Migration")
                .Select(type => type.GetCustomAttribute<MigrationAttribute>()?.Version)
                .Where(version => version.HasValue).Select(version => version!.Value).ToHashSet();
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
            connection.Open();
            var actual = connection.Query<long>("SELECT \"Version\" FROM \"VersionInfo\"").ToHashSet();
            if (actual.Count == 0 || actual.Max() != expected.Max() || expected.Except(actual).Any())
            {
                throw new InvalidOperationException($"database schema is {actual.DefaultIfEmpty().Max()}, executable requires {expected.Max()} with every migration applied");
            }
        }

        private static AuditWorkspace PrepareAuditWorkspace(StartupContext originalContext, string sourceAppData, bool live, string? sourceDatabasePath = null)
        {
            var sourceFull = Path.GetFullPath(sourceAppData);
            if (live)
            {
                return new AuditWorkspace
                {
                    SourceAppData = sourceFull,
                    AppData = sourceFull,
                    IsTemporary = false,
                    StartupContext = originalContext
                };
            }

            var temp = Path.Combine(Path.GetTempPath(), "chaptarr-matchbench-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);

            SnapshotSqliteDatabase(sourceDatabasePath ?? Path.Combine(sourceFull, "chaptarr.db"), Path.Combine(temp, "chaptarr.db"), required: true);
            SnapshotSqliteDatabase(Path.Combine(sourceFull, "staging.db"), Path.Combine(temp, "staging.db"), required: false);

            var configPath = Path.Combine(sourceFull, "config.xml");
            if (File.Exists(configPath))
            {
                File.Copy(configPath, Path.Combine(temp, "config.xml"), overwrite: true);
            }

            return new AuditWorkspace
            {
                SourceAppData = sourceFull,
                AppData = temp,
                IsTemporary = true,
                StartupContext = new StartupContext("/libraryaudit", $"/data={temp}")
            };
        }

        private static void SnapshotSqliteDatabase(string sourcePath, string destinationPath, bool required)
        {
            if (!File.Exists(sourcePath))
            {
                if (required)
                {
                    throw new FileNotFoundException($"Required SQLite database not found: {sourcePath}", sourcePath);
                }

                return;
            }

            try
            {
                using var source = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = sourcePath,
                    Mode = SqliteOpenMode.ReadOnly
                }.ToString());
                using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = destinationPath,
                    Mode = SqliteOpenMode.ReadWriteCreate
                }.ToString());

                source.Open();
                destination.Open();
                source.BackupDatabase(destination);
            }
            catch
            {
                File.Copy(sourcePath, destinationPath, overwrite: true);
                var wal = sourcePath + "-wal";
                if (File.Exists(wal))
                {
                    File.Copy(wal, destinationPath + "-wal", overwrite: true);
                }

                var shm = sourcePath + "-shm";
                if (File.Exists(shm))
                {
                    File.Copy(shm, destinationPath + "-shm", overwrite: true);
                }
            }
        }

        private static List<string> ParseAuditFlows(string raw)
        {
            var values = ExpandCsv(raw, "scan-local");
            if (values.Any(v => string.Equals(v, "all", StringComparison.OrdinalIgnoreCase)))
            {
                return new List<string> { "scan-local", "scan-v5", "scan-scoped-rematch", "downloaded", "manual-download", "manual-preview", "author-ready", "direct-default" };
            }

            var allowed = new HashSet<string>(new[] { "scan-local", "scan-v5", "scan-scoped-rematch", "downloaded", "manual-download", "manual-preview", "author-ready", "direct-default" }, StringComparer.OrdinalIgnoreCase);
            return values
                .Select(v => v.Trim().ToLowerInvariant())
                .Where(v => allowed.Contains(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .DefaultIfEmpty("scan-local")
                .ToList();
        }

        private static bool IsPotentiallyMutatingAuditFlow(string flow)
        {
            return string.Equals(flow, "author-ready", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(flow, "direct-default", StringComparison.OrdinalIgnoreCase);
        }

        private static List<BookMatchingStrictness?> ParseStrictnessValues(string raw)
        {
            var values = ExpandCsv(raw, "current");
            if (values.Any(v => string.Equals(v, "all", StringComparison.OrdinalIgnoreCase)))
            {
                return new List<BookMatchingStrictness?> { BookMatchingStrictness.Aggressive, BookMatchingStrictness.Balanced, BookMatchingStrictness.Strict };
            }

            var parsed = new List<BookMatchingStrictness?>();
            foreach (var value in values)
            {
                if (value.Equals("current", StringComparison.OrdinalIgnoreCase))
                {
                    parsed.Add(null);
                    continue;
                }

                if (value.Equals("loose", StringComparison.OrdinalIgnoreCase))
                {
                    parsed.Add(BookMatchingStrictness.Aggressive);
                    continue;
                }

                if (Enum.TryParse<BookMatchingStrictness>(value, ignoreCase: true, out var strictness))
                {
                    parsed.Add(strictness);
                }
            }

            return parsed.Count > 0 ? parsed : new List<BookMatchingStrictness?> { null };
        }

        private static List<bool?> ParsePathFallbackValues(string raw)
        {
            var values = ExpandCsv(raw, "current");
            if (values.Any(v => string.Equals(v, "all", StringComparison.OrdinalIgnoreCase)))
            {
                return new List<bool?> { true, false };
            }

            var parsed = new List<bool?>();
            foreach (var value in values)
            {
                if (value.Equals("current", StringComparison.OrdinalIgnoreCase))
                {
                    parsed.Add(null);
                }
                else if (TryParseBool(value, out var b))
                {
                    parsed.Add(b);
                }
            }

            return parsed.Count > 0 ? parsed : new List<bool?> { null };
        }

        private static List<string> ExpandCsv(string raw, string defaultValue)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                raw = defaultValue;
            }

            return raw.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(v => v.Trim())
                .Where(v => v.Length > 0)
                .ToList();
        }

        private static List<int> ParseTargetBookIds(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return new List<int>();
            }

            return raw.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(v => int.TryParse(v.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0)
                .Where(id => id > 0)
                .Distinct()
                .ToList();
        }

        private static MatchingContext BuildAuditMatchingContext(string flow, IReadOnlyList<int> targetBookIds, bool pathFallback, TraceCollector? trace)
        {
            var ctx = flow.ToLowerInvariant() switch
            {
                "scan-v5" => MatchingContextPresets.ForScanV5(pathFallback),
                "scan-scoped-rematch" => MatchingContextPresets.ForScanScopedRematch(),
                "downloaded" => MatchingContextPresets.ForDownloaded(false, targetBookIds, pathFallback),
                "manual-download" => MatchingContextPresets.ForDownloaded(true, targetBookIds, pathFallback),
                "manual-preview" => MatchingContextPresets.ForManualPreview(pathFallback, targetBookIds),
                "author-ready" => MatchingContextPresets.ForAuthorReady(),
                "direct-default" => MatchingContextPresets.ForDirectDefault(pathFallback),
                _ => MatchingContextPresets.ForScanLocal(pathFallback)
            };

            if (ctx.TargetBookIds == null && targetBookIds != null && targetBookIds.Count > 0)
            {
                ctx.TargetBookIds = targetBookIds.ToList();
            }

            ctx.TraceSink = trace;
            return ctx;
        }

        private static List<AuditInputFile> LoadBookFileAuditInputs(
            string dbPath,
            string prefix,
            int limit,
            bool requireTags,
            bool includeMissing,
            bool mappedOnly,
            bool unmappedOnly,
            bool includeUserSelected,
            int? selectedBookFileId,
            int? selectedBookId)
        {
            using var conn = OpenReadonlySqlite(dbPath);
            var scopePredicate = selectedBookFileId.HasValue
                ? "bf.Id = @selectedBookFileId"
                : selectedBookId.HasValue
                    ? "e.BookId = @selectedBookId"
                    : "IsPathUnderPrefix(bf.Path, @prefix, @prefixForward, @prefixBack)";
            var parameters = new DynamicParameters(PrefixParameters(prefix, limit, requireTags, mappedOnly, unmappedOnly));
            parameters.Add("selectedBookFileId", selectedBookFileId);
            parameters.Add("selectedBookId", selectedBookId);
            parameters.Add("includeUserSelected", includeUserSelected ? 1 : 0);

            var rows = conn.Query<AuditCurrentMapping>(CurrentMappingSql(@"
FROM BookFiles bf
LEFT JOIN Editions e ON e.Id = bf.EditionId
LEFT JOIN Books b ON b.Id = e.BookId
LEFT JOIN Authors a ON a.Id = b.AuthorId
WHERE " + scopePredicate + @"
  AND (@mappedOnly = 0 OR bf.EditionId > 0)
  AND (@unmappedOnly = 0 OR bf.EditionId = 0)
  AND (@requireTags = 0 OR (bf.AllTags IS NOT NULL AND trim(bf.AllTags) != '' AND trim(bf.AllTags) != '{}'))
  AND (
    @includeUserSelected = 1
    OR lower(COALESCE(
      CASE WHEN json_valid(bf.MatchProvenance) THEN json_extract(bf.MatchProvenance, '$.selectionSource') END,
      CASE WHEN json_valid(bf.MatchProvenance) THEN json_extract(bf.MatchProvenance, '$.SelectionSource') END,
      '')) NOT IN ('user_local', 'user_metadata')
  )
ORDER BY bf.Path
LIMIT @limit"), parameters).ToList();

            return rows.Select(row => ToAuditInput(row, "bookfiles", row.TagsJson, includeMissing, requireTags))
                .Where(x => x != null)
                .Select(x => x!)
                .ToList();
        }

        private static List<AuditInputFile> LoadStagingAuditInputs(IServiceProvider services, string dbPath, string prefix, int limit, bool requireTags, bool includeMissing, bool mappedOnly, bool unmappedOnly)
        {
            var rows = LoadStagedRows(services, prefix, limit, requireTags);
            var current = LoadCurrentMappingsByPath(dbPath, rows.Select(r => r.Path));
            var results = new List<AuditInputFile>();

            foreach (var row in rows)
            {
                current.TryGetValue(row.Path, out var mapping);
                if (mappedOnly && (mapping?.CurrentEditionId ?? 0) <= 0)
                {
                    continue;
                }

                if (unmappedOnly && (mapping?.CurrentEditionId ?? 0) > 0)
                {
                    continue;
                }

                var tags = SafeDeserializeTags(row.TagsJson);
                if (requireTags && tags.Count == 0)
                {
                    continue;
                }

                var exists = File.Exists(row.Path);
                if (!includeMissing && !exists)
                {
                    continue;
                }

                results.Add(new AuditInputFile
                {
                    BookFileId = mapping?.BookFileId,
                    Path = row.Path,
                    SizeBytes = row.SizeBytes,
                    ModifiedUtc = FromUnixNanoseconds(row.MtimeNs),
                    Tags = tags,
                    DurationSeconds = row.DurationSeconds,
                    CurrentEditionId = mapping?.CurrentEditionId,
                    CurrentEditionTitle = mapping?.CurrentEditionTitle,
                    CurrentBookId = mapping?.CurrentBookId,
                    CurrentBookTitle = mapping?.CurrentBookTitle,
                    CurrentAuthorId = mapping?.CurrentAuthorId,
                    CurrentAuthorName = mapping?.CurrentAuthorName,
                    SelectionSource = ReadSelectionSource(mapping?.MatchProvenanceJson),
                    Source = "staging",
                    ExistsOnDisk = exists
                });
            }

            return results;
        }

        private static List<AuditInputFile> LoadDiskAuditInputs(IServiceProvider services, string dbPath, string prefix, int limit, bool requireTags, bool includeMissing, bool mappedOnly, bool unmappedOnly)
        {
            var tagService = services.GetRequiredService<IMetadataTagService>();
            var fs = new FileSystem();
            var files = File.Exists(prefix)
                ? new List<string> { prefix }
                : Directory.EnumerateFiles(prefix, "*", SearchOption.AllDirectories)
                    .Where(path => MediaFileExtensions.AllExtensions.Contains(Path.GetExtension(path) ?? string.Empty))
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .Take(limit)
                    .ToList();

            var current = LoadCurrentMappingsByPath(dbPath, files);
            var results = new List<AuditInputFile>();
            foreach (var path in files)
            {
                current.TryGetValue(path, out var mapping);
                if (mappedOnly && (mapping?.CurrentEditionId ?? 0) <= 0)
                {
                    continue;
                }

                if (unmappedOnly && (mapping?.CurrentEditionId ?? 0) > 0)
                {
                    continue;
                }

                var info = new FileInfo(path);
                Dictionary<string, List<string>> tags;
                int? durationSeconds;
                try
                {
                    (tags, durationSeconds) = tagService.ReadAllTagsAndDuration(fs.FileInfo.FromFileName(path));
                    tags ??= new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                }
                catch (Exception ex)
                {
                    if (requireTags)
                    {
                        System.Console.Error.WriteLine($"[libraryaudit] tag read failed for '{path}': {ex.Message}");
                        continue;
                    }

                    tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                    durationSeconds = null;
                }

                if (requireTags && tags.Count == 0)
                {
                    continue;
                }

                results.Add(new AuditInputFile
                {
                    BookFileId = mapping?.BookFileId,
                    Path = path,
                    SizeBytes = info.Length,
                    ModifiedUtc = info.LastWriteTimeUtc,
                    Tags = tags,
                    DurationSeconds = durationSeconds,
                    CurrentEditionId = mapping?.CurrentEditionId,
                    CurrentEditionTitle = mapping?.CurrentEditionTitle,
                    CurrentBookId = mapping?.CurrentBookId,
                    CurrentBookTitle = mapping?.CurrentBookTitle,
                    CurrentAuthorId = mapping?.CurrentAuthorId,
                    CurrentAuthorName = mapping?.CurrentAuthorName,
                    SelectionSource = ReadSelectionSource(mapping?.MatchProvenanceJson),
                    Source = "disk",
                    ExistsOnDisk = true
                });
            }

            return results;
        }

        private static object PrefixParameters(string prefix, int limit, bool requireTags, bool mappedOnly, bool unmappedOnly)
        {
            var normalizedPrefix = prefix.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return new
            {
                prefix = normalizedPrefix,
                prefixForward = normalizedPrefix + "/",
                prefixBack = normalizedPrefix + "\\",
                limit,
                requireTags = requireTags ? 1 : 0,
                mappedOnly = mappedOnly ? 1 : 0,
                unmappedOnly = unmappedOnly ? 1 : 0
            };
        }

        private static string CurrentMappingSql(string fromAndWhere)
        {
            return @"
SELECT
  bf.Id as BookFileId,
  bf.Path as Path,
  bf.Size as SizeBytes,
  bf.Modified as ModifiedUtc,
  bf.AllTags as TagsJson,
  bf.MatchProvenance as MatchProvenanceJson,
  bf.DurationSeconds as DurationSeconds,
  CASE WHEN bf.EditionId > 0 THEN bf.EditionId ELSE NULL END as CurrentEditionId,
  e.Title as CurrentEditionTitle,
  e.BookId as CurrentBookId,
  b.Title as CurrentBookTitle,
  b.AuthorId as CurrentAuthorId,
  a.Name as CurrentAuthorName
" + fromAndWhere.Replace("IsPathUnderPrefix(bf.Path, @prefix, @prefixForward, @prefixBack)", "(bf.Path = @prefix OR substr(bf.Path, 1, length(@prefixForward)) = @prefixForward OR substr(bf.Path, 1, length(@prefixBack)) = @prefixBack)") + ";";
        }

        private static Dictionary<string, AuditCurrentMapping> LoadCurrentMappingsByPath(string dbPath, IEnumerable<string> paths)
        {
            var pathList = paths?
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();

            if (pathList.Count == 0)
            {
                return new Dictionary<string, AuditCurrentMapping>(StringComparer.OrdinalIgnoreCase);
            }

            using var conn = OpenReadonlySqlite(dbPath);
            var pathParams = new DynamicParameters();
            var pathClause = BuildInClause(pathList, "path", pathParams);
            var rows = conn.Query<AuditCurrentMapping>(@"
SELECT
  bf.Id as BookFileId,
  bf.Path as Path,
  bf.Size as SizeBytes,
  bf.Modified as ModifiedUtc,
  bf.AllTags as TagsJson,
  bf.MatchProvenance as MatchProvenanceJson,
  bf.DurationSeconds as DurationSeconds,
  CASE WHEN bf.EditionId > 0 THEN bf.EditionId ELSE NULL END as CurrentEditionId,
  e.Title as CurrentEditionTitle,
  e.BookId as CurrentBookId,
  b.Title as CurrentBookTitle,
  b.AuthorId as CurrentAuthorId,
  a.Name as CurrentAuthorName
FROM BookFiles bf
LEFT JOIN Editions e ON e.Id = bf.EditionId
LEFT JOIN Books b ON b.Id = e.BookId
LEFT JOIN Authors a ON a.Id = b.AuthorId
WHERE bf.Path IN (" + pathClause + ");", pathParams).ToList();

            return rows
                .Where(r => !string.IsNullOrWhiteSpace(r.Path))
                .GroupBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        }

        private static AuditInputFile? ToAuditInput(AuditCurrentMapping row, string source, string? tagsJson, bool includeMissing, bool requireTags)
        {
            if (row == null || string.IsNullOrWhiteSpace(row.Path))
            {
                return null;
            }

            var exists = File.Exists(row.Path);
            if (!includeMissing && !exists)
            {
                return null;
            }

            var tags = SafeDeserializeTags(tagsJson);
            if (requireTags && tags.Count == 0)
            {
                return null;
            }

            return new AuditInputFile
            {
                BookFileId = row.BookFileId,
                Path = row.Path,
                SizeBytes = row.SizeBytes,
                ModifiedUtc = row.ModifiedUtc == default ? DateTime.UtcNow : row.ModifiedUtc,
                Tags = tags,
                DurationSeconds = row.DurationSeconds,
                CurrentEditionId = row.CurrentEditionId,
                CurrentEditionTitle = row.CurrentEditionTitle,
                CurrentBookId = row.CurrentBookId,
                CurrentBookTitle = row.CurrentBookTitle,
                CurrentAuthorId = row.CurrentAuthorId,
                CurrentAuthorName = row.CurrentAuthorName,
                SelectionSource = ReadSelectionSource(row.MatchProvenanceJson),
                Source = source,
                ExistsOnDisk = exists
            };
        }

        private static string? ReadSelectionSource(string? provenanceJson)
        {
            if (string.IsNullOrWhiteSpace(provenanceJson))
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(provenanceJson);
                var root = document.RootElement;
                if ((root.TryGetProperty("selectionSource", out var source) ||
                     root.TryGetProperty("SelectionSource", out source)) &&
                    source.ValueKind == JsonValueKind.String)
                {
                    return source.GetString();
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // Historical or damaged provenance must not make an audit row unreadable.
            }

            return null;
        }

        private static bool IsUserSelectedMapping(string? selectionSource)
        {
            return string.Equals(selectionSource, "user_local", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(selectionSource, "user_metadata", StringComparison.OrdinalIgnoreCase);
        }

        private static List<DiscoveredFileWithMetadata> BuildDiscoveredFilesFromAuditInputs(IReadOnlyList<AuditInputFile> inputs, bool requireTags)
        {
            var discovered = new List<DiscoveredFileWithMetadata>();
            foreach (var input in inputs ?? Array.Empty<AuditInputFile>())
            {
                if (input == null || string.IsNullOrWhiteSpace(input.Path))
                {
                    continue;
                }

                if (requireTags && (input.Tags == null || input.Tags.Count == 0))
                {
                    continue;
                }

                var ext = Path.GetExtension(input.Path) ?? string.Empty;
                var detectedQuality = MediaFileExtensions.GetQualityForExtension(ext);
                discovered.Add(new DiscoveredFileWithMetadata
                {
                    Path = input.Path,
                    Size = input.SizeBytes,
                    Modified = input.ModifiedUtc == default ? DateTime.UtcNow : input.ModifiedUtc,
                    AllTags = CloneTags(input.Tags),
                    Quality = new QualityModel(detectedQuality),
                    DurationSeconds = input.DurationSeconds
                });
            }

            return discovered;
        }

        private static AuditRunReport AnalyzeAuditRun(
            string dbPath,
            string flow,
            BookMatchingStrictness strictness,
            bool pathFallback,
            IReadOnlyList<AuditInputFile> inputs,
            FileMatchResult matchResult,
            TraceCollector? trace,
            long elapsedMs,
            bool includeFtsDetails)
        {
            var matchedByPath = (matchResult?.MatchedFiles ?? Array.Empty<FileMatch>())
                .Where(m => !string.IsNullOrWhiteSpace(m?.File?.Path))
                .GroupBy(m => m.File.Path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var unmatchedByPath = (matchResult?.UnmatchedFiles ?? Array.Empty<UnmatchedFile>())
                .Where(u => !string.IsNullOrWhiteSpace(u?.File?.Path))
                .GroupBy(u => u.File.Path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var editionIds = new HashSet<int>();
            foreach (var input in inputs)
            {
                if (input.CurrentEditionId.HasValue)
                {
                    editionIds.Add(input.CurrentEditionId.Value);
                }
            }

            foreach (var match in matchedByPath.Values)
            {
                if (match.EditionId > 0)
                {
                    editionIds.Add(match.EditionId);
                }
            }

            foreach (var traceEditionId in trace?.Events
                         .Where(evt => evt?.EditionId > 0)
                         .Select(evt => evt.EditionId!.Value)
                         .Distinct() ?? Enumerable.Empty<int>())
            {
                editionIds.Add(traceEditionId);
            }

            var editionInfo = LoadEditionInfo(dbPath, editionIds);
            EnrichTraceWithProviderIdentity(dbPath, trace?.Events, editionInfo);
            var results = new List<AuditFileResult>();

            foreach (var input in inputs)
            {
                matchedByPath.TryGetValue(input.Path, out var match);
                unmatchedByPath.TryGetValue(input.Path, out var unmatched);
                var matchedEditionId = match?.EditionId > 0 ? match.EditionId : (int?)null;
                editionInfo.TryGetValue(input.CurrentEditionId ?? 0, out var currentEdition);
                editionInfo.TryGetValue(matchedEditionId ?? 0, out var matchedEdition);
                var currentProviderKeys = EditionProviderAnswerKeys(currentEdition);
                var matchedProviderKeys = EditionProviderAnswerKeys(matchedEdition);

                var changed = input.CurrentEditionId.HasValue &&
                              input.CurrentEditionId.Value > 0 &&
                              (!matchedEditionId.HasValue ||
                               (matchedEditionId != input.CurrentEditionId.Value &&
                                !currentProviderKeys.Intersect(matchedProviderKeys, StringComparer.OrdinalIgnoreCase).Any()));

                var tagText = FlattenTagText(input.Tags);
                var flags = BuildAuditFlags(input, match, currentEdition, matchedEdition, unmatched, changed);
                results.Add(new AuditFileResult
                {
                    BookFileId = input.BookFileId,
                    Path = input.Path,
                    Source = input.Source,
                    SizeBytes = input.SizeBytes,
                    FileDurationSeconds = input.DurationSeconds,
                    TagSummary = BuildTagSummary(input.Tags),
                    EvidenceTags = includeFtsDetails ? CloneTags(input.Tags) : null,
                    CurrentEditionId = input.CurrentEditionId,
                    CurrentEditionTitle = input.CurrentEditionTitle,
                    CurrentEdition = currentEdition,
                    CurrentEditionProviderKeys = currentProviderKeys,
                    CurrentBookProviderKey = currentEdition?.BookBaseBookId,
                    SelectionSource = input.SelectionSource,
                    MatchedEditionId = matchedEditionId,
                    MatchedBookTitle = match?.BookTitle ?? matchedEdition?.BookTitle,
                    MatchedAuthorName = match?.AuthorName ?? matchedEdition?.AuthorName,
                    MatchedEdition = matchedEdition,
                    MatchedEditionProviderKeys = matchedProviderKeys,
                    MatchedBookProviderKey = matchedEdition?.BookBaseBookId,
                    MatchedNarratorsFoundInTags = FoundNarratorsInTags(matchedEdition, tagText),
                    MatchedNarratorsMissingFromTags = MissingNarratorsInTags(matchedEdition, tagText),
                    UnmatchedReason = unmatched?.Reason,
                    ChangedFromCurrent = changed,
                    Flags = flags,
                    Signals = BuildAuditSignals(input, currentEdition, matchedEdition),
                    FtsAttempts = trace?.GetAttempts(input.Path) ?? new List<FtsAttemptSummary>()
                });
            }

            return new AuditRunReport
            {
                Flow = flow,
                Strictness = strictness.ToString(),
                PathFallback = pathFallback,
                MatchedCount = matchedByPath.Count,
                UnmatchedCount = unmatchedByPath.Count,
                ChangedCount = results.Count(r => r.ChangedFromCurrent),
                PreviouslyMappedNowUnmatchedCount = results.Count(r => (r.CurrentEditionId ?? 0) > 0 && !r.MatchedEditionId.HasValue),
                ElapsedMs = elapsedMs,
                TraceEnabled = trace != null,
                TraceLimit = trace?.Limit ?? 0,
                TraceTotalEventCount = trace?.TotalEventCount ?? 0,
                TraceDroppedEventCount = trace?.DroppedEventCount ?? 0,
                TraceTruncated = trace?.Truncated ?? false,
                Results = results,
                Trace = trace?.Events ?? new List<MatchingTraceEvent>()
            };
        }

        private static Dictionary<int, EditionAuditInfo> LoadEditionInfo(string dbPath, IEnumerable<int> editionIds)
        {
            var ids = editionIds?.Where(id => id > 0).Distinct().ToList() ?? new List<int>();
            if (ids.Count == 0)
            {
                return new Dictionary<int, EditionAuditInfo>();
            }

            using var conn = OpenReadonlySqlite(dbPath);
            var idParams = new DynamicParameters();
            var idClause = BuildInClause(ids, "id", idParams);
            var rows = conn.Query<EditionAuditInfo>(@"
SELECT
  e.Id as EditionId,
  e.Title as Title,
  e.Subtitle as Subtitle,
  e.BookId as BookId,
  b.Title as BookTitle,
  b.AuthorId as AuthorId,
  a.Name as AuthorName,
  e.ForeignEditionId as ForeignEditionId,
  e.HardcoverEditionId as HardcoverEditionId,
  e.GoodreadsEditionId as GoodreadsEditionId,
  e.OpenLibraryEditionId as OpenLibraryEditionId,
  e.GoogleBooksEditionId as GoogleBooksEditionId,
  e.Asin as Asin,
  e.AudibleASIN as AudibleASIN,
  e.Asins as AsinsJson,
  e.Isbn13 as Isbn13,
  e.Isbn10 as Isbn10,
  e.Narrator as Narrator,
  e.NarratorNames as NarratorNamesJson,
  linked.LinkedNarratorNamesJson as LinkedNarratorNamesJson,
  e.DurationSeconds as DurationSeconds,
  e.ReleaseDate as ReleaseDate,
  e.ChapterCount as ChapterCount,
  e.HasChapters as HasChapters,
  e.ReadingFormatId as ReadingFormatId,
  e.EditionFormat as EditionFormat,
  e.Format as Format,
  e.Publisher as Publisher,
  e.Disambiguation as Disambiguation,
  e.IsFallbackEdition as IsFallbackEdition,
  b.BaseBookId as BookBaseBookId,
  b.SeriesName as SeriesName,
  b.SeriesPosition as SeriesPosition
FROM Editions e
JOIN Books b ON b.Id = e.BookId
JOIN Authors a ON a.Id = b.AuthorId
LEFT JOIN (
  SELECT enl.EditionId,
         json_group_array(DISTINCT nm.Name) as LinkedNarratorNamesJson
  FROM EditionNarratorLink enl
  JOIN Narrators n ON n.Id = enl.NarratorId
  JOIN NarratorMetadata nm ON nm.Id = n.NarratorMetadataId
  WHERE nm.Name IS NOT NULL AND trim(nm.Name) != ''
  GROUP BY enl.EditionId
) linked ON linked.EditionId = e.Id
WHERE e.Id IN (" + idClause + ");", idParams).ToList();

            foreach (var row in rows)
            {
                row.Asins = EditionAsins(row).OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();
                row.Narrators = NarratorNames(row).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            }

            return rows.ToDictionary(r => r.EditionId);
        }

        private static void EnrichTraceWithProviderIdentity(
            string dbPath,
            IReadOnlyList<MatchingTraceEvent>? trace,
            IReadOnlyDictionary<int, EditionAuditInfo> editionInfo)
        {
            if (trace == null || trace.Count == 0)
            {
                return;
            }

            var bookIds = trace
                .Where(evt => evt?.BookId > 0)
                .Select(evt => evt.BookId!.Value)
                .Concat(editionInfo.Values.Where(edition => edition.BookId > 0).Select(edition => edition.BookId))
                .Distinct()
                .ToList();
            var bookInfo = LoadBookInfo(dbPath, bookIds);

            foreach (var evt in trace.Where(evt => evt != null))
            {
                evt.Data ??= new Dictionary<string, string>();

                var bookId = evt.BookId;
                if ((!bookId.HasValue || bookId.Value <= 0) &&
                    evt.EditionId.HasValue &&
                    editionInfo.TryGetValue(evt.EditionId.Value, out var eventEdition))
                {
                    bookId = eventEdition.BookId;
                }

                if (bookId.HasValue && bookInfo.TryGetValue(bookId.Value, out var book))
                {
                    evt.Data["bookProviderKey"] = !string.IsNullOrWhiteSpace(book.BaseBookId)
                        ? book.BaseBookId
                        : $"local-book:{book.BookId.ToString(CultureInfo.InvariantCulture)}";
                    evt.Data["bookTitle"] = book.Title ?? evt.Data.GetValueOrDefault("bookTitle", string.Empty);
                    evt.Data["authorName"] = book.AuthorName ?? evt.Data.GetValueOrDefault("authorName", string.Empty);
                }

                if (evt.EditionId.HasValue && editionInfo.TryGetValue(evt.EditionId.Value, out var edition))
                {
                    evt.Data["editionProviderKeys"] = string.Join(",", EditionProviderAnswerKeys(edition));
                }
            }
        }

        private static Dictionary<int, BookAuditInfo> LoadBookInfo(string dbPath, IEnumerable<int> bookIds)
        {
            var ids = bookIds?.Where(id => id > 0).Distinct().ToList() ?? new List<int>();
            if (ids.Count == 0)
            {
                return new Dictionary<int, BookAuditInfo>();
            }

            using var conn = OpenReadonlySqlite(dbPath);
            var parameters = new DynamicParameters();
            var idClause = BuildInClause(ids, "bookId", parameters);
            var rows = conn.Query<BookAuditInfo>(@"
SELECT
  b.Id as BookId,
  b.BaseBookId as BaseBookId,
  b.Title as Title,
  b.AuthorId as AuthorId,
  a.Name as AuthorName
FROM Books b
JOIN Authors a ON a.Id = b.AuthorId
WHERE b.Id IN (" + idClause + ");", parameters).ToList();

            return rows.ToDictionary(row => row.BookId);
        }

        private static Dictionary<int, string> LoadAllBookProviderKeys(string dbPath)
        {
            using var conn = OpenReadonlySqlite(dbPath);
            return conn.Query<(int BookId, string BaseBookId)>(@"
SELECT Id as BookId, BaseBookId as BaseBookId
FROM Books;")
                .ToDictionary(
                    row => row.BookId,
                    row => !string.IsNullOrWhiteSpace(row.BaseBookId)
                        ? row.BaseBookId
                        : $"local-book:{row.BookId.ToString(CultureInfo.InvariantCulture)}");
        }

        private static string BuildInClause<T>(IReadOnlyList<T> values, string prefix, DynamicParameters parameters)
        {
            var placeholders = new List<string>();
            for (var i = 0; i < values.Count; i++)
            {
                var name = $"{prefix}{i}";
                parameters.Add(name, values[i]);
                placeholders.Add("@" + name);
            }

            return string.Join(",", placeholders);
        }

        private static List<string> EditionProviderAnswerKeys(EditionAuditInfo? info)
        {
            if (info == null)
            {
                return new List<string>();
            }

            var edition = new Edition
            {
                ForeignEditionId = info.ForeignEditionId,
                HardcoverEditionId = info.HardcoverEditionId,
                GoodreadsEditionId = info.GoodreadsEditionId,
                OpenLibraryEditionId = info.OpenLibraryEditionId,
                GoogleBooksEditionId = info.GoogleBooksEditionId,
                Asin = info.Asin,
                AudibleASIN = info.AudibleASIN,
                Asins = info.Asins,
                Isbn13 = info.Isbn13,
                Isbn10 = info.Isbn10
            };

            var keys = BookEditionIdentity.GetRemoteEditionRehomeTokens(edition);
            if (!string.IsNullOrWhiteSpace(info.Isbn13))
            {
                keys.Add("isbn:" + info.Isbn13.Trim());
            }

            if (!string.IsNullOrWhiteSpace(info.Isbn10))
            {
                keys.Add("isbn:" + info.Isbn10.Trim());
            }

            return keys
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<string> BuildAuditFlags(AuditInputFile input, FileMatch? match, EditionAuditInfo? currentEdition, EditionAuditInfo? matchedEdition, UnmatchedFile? unmatched, bool changedFromCurrent)
        {
            var flags = new List<string>();
            if (IsUserSelectedMapping(input.SelectionSource))
            {
                flags.Add("user-selected-answer-key");
            }

            if ((input.CurrentEditionId ?? 0) > 0 && match == null)
            {
                flags.Add("mapped-now-unmatched");
            }

            if ((input.CurrentEditionId ?? 0) > 0 && match != null && changedFromCurrent)
            {
                flags.Add("would-remap");
            }

            if (match != null && LooksLikePartTitle(match.BookTitle) || LooksLikePartTitle(matchedEdition?.Title))
            {
                flags.Add("part-title");
            }

            if (LooksLikePartTitle(currentEdition?.Title))
            {
                flags.Add("current-part-title");
            }

            if (LooksLikePartTitle(matchedEdition?.Title))
            {
                flags.Add("matched-part-title");
            }

            if (currentEdition != null && matchedEdition != null && currentEdition.BookId != matchedEdition.BookId)
            {
                flags.Add("book-row-change");

                if (!string.IsNullOrWhiteSpace(currentEdition.BookBaseBookId) &&
                    string.Equals(currentEdition.BookBaseBookId, matchedEdition.BookBaseBookId, StringComparison.OrdinalIgnoreCase))
                {
                    flags.Add("same-base-book");
                }
            }

            if (currentEdition != null &&
                matchedEdition != null &&
                !string.IsNullOrWhiteSpace(currentEdition.ForeignEditionId) &&
                string.Equals(currentEdition.ForeignEditionId, matchedEdition.ForeignEditionId, StringComparison.OrdinalIgnoreCase) &&
                currentEdition.EditionId != matchedEdition.EditionId)
            {
                flags.Add("same-foreign-edition");
            }

            if (matchedEdition?.ReleaseDate.HasValue == true && matchedEdition.ReleaseDate.Value.Year >= 2199)
            {
                flags.Add("future-2199");
            }

            if (currentEdition?.ReleaseDate.HasValue == true && currentEdition.ReleaseDate.Value.Year >= 2199)
            {
                flags.Add("current-future-2199");
            }

            if (matchedEdition?.ReleaseDate.HasValue == true && matchedEdition.ReleaseDate.Value.Year >= 2199)
            {
                flags.Add("matched-future-2199");
            }

            if (match != null && matchedEdition?.HasChapters == true)
            {
                flags.Add("matched-has-chapters");
            }

            if (currentEdition != null && matchedEdition != null && !currentEdition.HasChapters && matchedEdition.HasChapters)
            {
                flags.Add("chapters-improved");
            }

            var tagText = FlattenTagText(input.Tags);
            if (currentEdition != null)
            {
                AddNarratorEvidenceFlags(flags, currentEdition, tagText, "current");
            }

            if (matchedEdition != null)
            {
                AddNarratorEvidenceFlags(flags, matchedEdition, tagText, "matched");
            }

            if (currentEdition != null && matchedEdition != null)
            {
                var currentNarrators = NormalizedNarratorSet(currentEdition);
                var matchedNarrators = NormalizedNarratorSet(matchedEdition);
                if (currentNarrators.Count > 0 && matchedNarrators.Count > 0 && !currentNarrators.SetEquals(matchedNarrators))
                {
                    flags.Add("narrator-change");
                }
            }

            if (input.DurationSeconds.HasValue && matchedEdition?.DurationSeconds.HasValue == true)
            {
                var diff = Math.Abs(input.DurationSeconds.Value - matchedEdition.DurationSeconds.Value);
                if (diff > Math.Max(300, input.DurationSeconds.Value * 0.05))
                {
                    flags.Add("duration-diff");
                }
            }

            if (unmatched != null && !string.IsNullOrWhiteSpace(unmatched.Reason))
            {
                flags.Add("unmatched");
            }

            return flags.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void AddNarratorEvidenceFlags(List<string> flags, EditionAuditInfo edition, string tagText, string prefix)
        {
            var narrators = NarratorNames(edition).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (narrators.Count == 0)
            {
                return;
            }

            var foundCount = narrators.Count(n => IsPersonInText(n, tagText));
            var missingCount = narrators.Count - foundCount;
            if (missingCount <= 0)
            {
                return;
            }

            flags.Add($"{prefix}-narrator-not-in-tags");

            if (foundCount == 0)
            {
                flags.Add($"{prefix}-narrator-none-in-tags");
            }
            else
            {
                flags.Add($"{prefix}-narrator-partial-in-tags");
            }
        }

        private static HashSet<string> NormalizedNarratorSet(EditionAuditInfo edition)
        {
            return NarratorNames(edition)
                .Select(NormalizeForContains)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private static List<string> BuildAuditSignals(AuditInputFile input, EditionAuditInfo? currentEdition, EditionAuditInfo? matchedEdition)
        {
            var signals = new List<string>();

            if (input.DurationSeconds.HasValue)
            {
                signals.Add($"file duration {FormatDuration(input.DurationSeconds.Value)}");
            }

            if (currentEdition != null && matchedEdition != null && currentEdition.BookId != matchedEdition.BookId)
            {
                var sameBase = !string.IsNullOrWhiteSpace(currentEdition.BookBaseBookId) &&
                               string.Equals(currentEdition.BookBaseBookId, matchedEdition.BookBaseBookId, StringComparison.OrdinalIgnoreCase);
                signals.Add(sameBase
                    ? $"local book row changes {currentEdition.BookId} -> {matchedEdition.BookId} (same provider book {currentEdition.BookBaseBookId})"
                    : $"local book row changes {currentEdition.BookId} -> {matchedEdition.BookId}");
            }

            if (currentEdition != null &&
                matchedEdition != null &&
                !string.IsNullOrWhiteSpace(currentEdition.ForeignEditionId) &&
                string.Equals(currentEdition.ForeignEditionId, matchedEdition.ForeignEditionId, StringComparison.OrdinalIgnoreCase) &&
                currentEdition.EditionId != matchedEdition.EditionId)
            {
                signals.Add($"same foreign edition id {currentEdition.ForeignEditionId}");
            }

            if (currentEdition != null && matchedEdition != null && currentEdition.HasChapters != matchedEdition.HasChapters)
            {
                signals.Add($"chapters {(currentEdition.HasChapters ? "yes" : "no")} -> {(matchedEdition.HasChapters ? "yes" : "no")}");
            }

            if (currentEdition?.DurationSeconds.HasValue == true)
            {
                signals.Add($"current duration {FormatDuration(currentEdition.DurationSeconds.Value)} (diff {FormatDurationDelta(input.DurationSeconds, currentEdition.DurationSeconds)})");
            }

            if (matchedEdition?.DurationSeconds.HasValue == true)
            {
                signals.Add($"matched duration {FormatDuration(matchedEdition.DurationSeconds.Value)} (diff {FormatDurationDelta(input.DurationSeconds, matchedEdition.DurationSeconds)})");
            }

            var tagText = FlattenTagText(input.Tags);
            if (currentEdition != null)
            {
                signals.Add(NarratorEvidenceSignal("current", currentEdition, tagText));
            }

            if (matchedEdition != null)
            {
                signals.Add(NarratorEvidenceSignal("matched", matchedEdition, tagText));
            }

            var asins = ExtractAsinsFromTags(input.Tags);
            if (asins.Count > 0)
            {
                signals.Add($"tag ASINs {string.Join(", ", asins.Take(6))}");
                if (matchedEdition != null)
                {
                    var matchedAsins = EditionAsins(matchedEdition);
                    var overlap = asins.Intersect(matchedAsins, StringComparer.OrdinalIgnoreCase).ToList();
                    signals.Add(overlap.Count > 0
                        ? $"matched ASIN overlap {string.Join(", ", overlap.Take(6))}"
                        : "matched ASIN overlap none");
                }
            }

            if (matchedEdition?.ReleaseDate.HasValue == true)
            {
                signals.Add($"matched release {matchedEdition.ReleaseDate.Value:yyyy-MM-dd}");
            }

            if (!string.IsNullOrWhiteSpace(matchedEdition?.Publisher))
            {
                signals.Add($"matched publisher {matchedEdition.Publisher}");
            }

            return signals
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<string> BuildTagSummary(Dictionary<string, List<string>>? tags)
        {
            if (tags == null || tags.Count == 0)
            {
                return new List<string>();
            }

            const int maxTagSummaryItems = 18;
            var priority = new[]
            {
                "mP4:©nam", "mP4:©alb", "mP4:©ART", "mP4:aART", "mP4:©wrt",
                "ID3:TIT2", "ID3:TALB", "ID3:TPE1", "ID3:TPE2", "ID3:TCOM",
                "title", "album", "artist", "albumartist", "author", "narrator", "publisher", "date", "year"
            };

            var output = new List<string>();
            var emittedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in priority)
            {
                if (!tags.TryGetValue(key, out var values) || values == null)
                {
                    continue;
                }

                foreach (var value in values.Where(v => !string.IsNullOrWhiteSpace(v) && !IsNoisyReportTag(key, v)).Take(3))
                {
                    output.Add($"{key}: {TruncateForReport(value.Trim(), 160)}");
                    emittedKeys.Add(key);
                }
            }

            foreach (var kv in tags.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (emittedKeys.Contains(kv.Key) || !IsHighValueReportTag(kv.Key))
                {
                    continue;
                }

                foreach (var value in (kv.Value ?? new List<string>()).Where(v => !string.IsNullOrWhiteSpace(v) && !IsNoisyReportTag(kv.Key, v)).Take(3))
                {
                    output.Add($"{kv.Key}: {TruncateForReport(value.Trim(), 160)}");
                    emittedKeys.Add(kv.Key);
                    if (output.Count >= maxTagSummaryItems)
                    {
                        return output;
                    }
                }
            }

            if (output.Count < 8)
            {
                foreach (var kv in tags.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                {
                    if (emittedKeys.Contains(kv.Key))
                    {
                        continue;
                    }

                    foreach (var value in (kv.Value ?? new List<string>()).Where(v => !string.IsNullOrWhiteSpace(v) && !IsNoisyReportTag(kv.Key, v)).Take(2))
                    {
                        output.Add($"{kv.Key}: {TruncateForReport(value.Trim(), 160)}");
                        emittedKeys.Add(kv.Key);
                        if (output.Count >= maxTagSummaryItems)
                        {
                            return output;
                        }
                    }
                }
            }

            return output.Take(maxTagSummaryItems).ToList();
        }

        private static bool IsHighValueReportTag(string? key)
        {
            var k = key ?? string.Empty;
            return k.Contains("narrat", StringComparison.OrdinalIgnoreCase) ||
                   k.Contains("composer", StringComparison.OrdinalIgnoreCase) ||
                   k.Contains("TCOM", StringComparison.OrdinalIgnoreCase) ||
                   k.Contains("wrt", StringComparison.OrdinalIgnoreCase) ||
                   k.Contains("asin", StringComparison.OrdinalIgnoreCase) ||
                   k.Contains("audible", StringComparison.OrdinalIgnoreCase) ||
                   k.Contains("product_id", StringComparison.OrdinalIgnoreCase) ||
                   k.Contains("publisher", StringComparison.OrdinalIgnoreCase) ||
                   k.Contains("TPUB", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsNoisyReportTag(string? key, string? value)
        {
            var k = key ?? string.Empty;
            var v = value ?? string.Empty;
            if (v.Contains("[Binary Data]", StringComparison.OrdinalIgnoreCase) ||
                v.Contains("[Unknown Frame]", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return k.Contains("APIC", StringComparison.OrdinalIgnoreCase) ||
                   k.Contains("CHAP", StringComparison.OrdinalIgnoreCase) ||
                   k.Contains("CTOC", StringComparison.OrdinalIgnoreCase) ||
                   k.Contains("cover", StringComparison.OrdinalIgnoreCase);
        }

        private static string FlattenTagText(Dictionary<string, List<string>>? tags)
        {
            if (tags == null || tags.Count == 0)
            {
                return string.Empty;
            }

            return string.Join(" ", tags
                .Where(kv => kv.Value != null)
                .SelectMany(kv => kv.Value)
                .Where(v => !string.IsNullOrWhiteSpace(v)));
        }

        private static bool HasAnyPersonInText(EditionAuditInfo edition, string text)
        {
            if (edition == null || string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            foreach (var narrator in NarratorNames(edition))
            {
                if (IsPersonInText(narrator, text))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsPersonInText(string? person, string? text)
        {
            var normalizedPerson = NormalizeForContains(person ?? string.Empty);
            var normalizedText = NormalizeForContains(text ?? string.Empty);
            if (normalizedPerson.Length == 0 || normalizedText.Length == 0)
            {
                return false;
            }

            if (normalizedText.Contains(normalizedPerson, StringComparison.Ordinal))
            {
                return true;
            }

            var compactPerson = RemoveSpaces(normalizedPerson);
            var compactText = RemoveSpaces(normalizedText);
            if (compactPerson.Length > 0 && compactText.Contains(compactPerson, StringComparison.Ordinal))
            {
                return true;
            }

            var personTokens = normalizedPerson.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var textTokens = normalizedText.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
            return personTokens.Length > 1 && personTokens.All(textTokens.Contains);
        }

        private static string RemoveSpaces(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Replace(" ", string.Empty, StringComparison.Ordinal);
        }

        private static List<string> MissingNarratorsInTags(EditionAuditInfo? edition, string tagText)
        {
            return NarratorNames(edition)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(n => !IsPersonInText(n, tagText))
                .ToList();
        }

        private static List<string> FoundNarratorsInTags(EditionAuditInfo? edition, string tagText)
        {
            return NarratorNames(edition)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(n => IsPersonInText(n, tagText))
                .ToList();
        }

        private static string NarratorEvidenceSignal(string label, EditionAuditInfo edition, string tagText)
        {
            var narrators = NarratorNames(edition)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (narrators.Count == 0)
            {
                return $"{label} narrator unavailable";
            }

            var missing = MissingNarratorsInTags(edition, tagText);
            if (missing.Count == 0)
            {
                return $"{label} narrator all seen in tags: {string.Join(", ", narrators.Take(6))}";
            }

            var found = FoundNarratorsInTags(edition, tagText);
            return found.Count == 0
                ? $"{label} narrator none seen in tags; missing: {string.Join(", ", missing.Take(6))}"
                : $"{label} narrator partial in tags; seen: {string.Join(", ", found.Take(6))}; missing: {string.Join(", ", missing.Take(6))}";
        }

        private static IEnumerable<string> NarratorNames(EditionAuditInfo? edition)
        {
            if (edition == null)
            {
                yield break;
            }

            foreach (var value in ParseJsonStringArray(edition.NarratorNamesJson))
            {
                yield return value;
            }

            foreach (var value in ParseJsonStringArray(edition.LinkedNarratorNamesJson))
            {
                yield return value;
            }

            if (!string.IsNullOrWhiteSpace(edition.Narrator))
            {
                foreach (var piece in edition.Narrator.Split(new[] { ',', ';', '&' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!string.IsNullOrWhiteSpace(piece))
                    {
                        yield return piece.Trim();
                    }
                }
            }
        }

        private static string? NarratorDisplay(EditionAuditInfo? edition)
        {
            var narrators = NarratorNames(edition)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .ToList();

            return narrators.Count == 0 ? edition?.Narrator : string.Join(", ", narrators);
        }

        private static HashSet<string> ExtractAsinsFromTags(Dictionary<string, List<string>>? tags)
        {
            var output = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (tags == null || tags.Count == 0)
            {
                return output;
            }

            foreach (var value in tags.Values.SelectMany(v => v ?? new List<string>()))
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(value, @"\bB[0-9A-Z]{9}\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    output.Add(match.Value.ToUpperInvariant());
                }
            }

            return output;
        }

        private static HashSet<string> EditionAsins(EditionAuditInfo edition)
        {
            var output = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (edition == null)
            {
                return output;
            }

            AddAsin(output, edition.Asin);
            AddAsin(output, edition.AudibleASIN);
            foreach (var asin in edition.Asins ?? new List<string>())
            {
                AddAsin(output, asin);
            }

            foreach (var asin in ParseJsonStringArray(edition.AsinsJson))
            {
                AddAsin(output, asin);
            }

            return output;
        }

        private static void AddAsin(HashSet<string> output, string? value)
        {
            if (output == null || string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var clean = value.Trim();
            if (clean.StartsWith("az:", StringComparison.OrdinalIgnoreCase))
            {
                clean = clean.Substring(3);
            }

            clean = clean.ToUpperInvariant();
            if (System.Text.RegularExpressions.Regex.IsMatch(clean, @"^B[0-9A-Z]{9}$"))
            {
                output.Add(clean);
            }
        }

        private static List<string> ParseJsonStringArray(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new List<string>();
            }

            try
            {
                var values = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json);
                return values?
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Select(v => v.Trim())
                    .ToList() ?? new List<string>();
            }
            catch
            {
                return new List<string>();
            }
        }

        private static string NormalizeForContains(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return System.Text.RegularExpressions.Regex
                .Replace(value.ToLowerInvariant(), @"[^\p{L}\p{Nd}]+", " ")
                .Trim();
        }

        private static string FormatDuration(int seconds)
        {
            if (seconds <= 0)
            {
                return "-";
            }

            var ts = TimeSpan.FromSeconds(seconds);
            return ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours}h {ts.Minutes:D2}m"
                : $"{ts.Minutes}m {ts.Seconds:D2}s";
        }

        private static string FormatDurationDelta(int? leftSeconds, int? rightSeconds)
        {
            if (!leftSeconds.HasValue || !rightSeconds.HasValue)
            {
                return "unknown";
            }

            var diff = Math.Abs(leftSeconds.Value - rightSeconds.Value);
            return FormatDuration(diff);
        }

        private static string Coalesce(string? value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        private static string TruncateForReport(string? value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || maxLength <= 0 || value.Length <= maxLength)
            {
                return value ?? string.Empty;
            }

            return value.Substring(0, maxLength) + "...";
        }

        private static bool LooksLikePartTitle(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return System.Text.RegularExpressions.Regex.IsMatch(value, @"\bpart\s+\d+\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        private static List<FtsAttemptTrace> BuildFtsAttempts(AuditRunReport run, string path)
        {
            var attempts = new List<FtsAttemptTrace>();
            FtsAttemptTrace? current = null;

            foreach (var evt in run.Trace.Where(evt =>
                         evt != null &&
                         string.Equals(evt.FilePath, path, StringComparison.OrdinalIgnoreCase)))
            {
                if (string.Equals(evt.EventType, "fts_input", StringComparison.OrdinalIgnoreCase))
                {
                    current = new FtsAttemptTrace
                    {
                        Phase = evt.Phase ?? string.Empty
                    };
                    current.Events.Add(evt);
                    attempts.Add(current);
                    continue;
                }

                current?.Events.Add(evt);
            }

            return attempts;
        }

        private static string TraceData(MatchingTraceEvent? evt, string key)
        {
            if (evt?.Data == null || string.IsNullOrWhiteSpace(key))
            {
                return string.Empty;
            }

            return evt.Data.TryGetValue(key, out var value) ? value ?? string.Empty : string.Empty;
        }

        private static List<(MatchingTraceEvent Event, int ProviderBookRank)> RankFtsCandidatesByProviderBook(
            IEnumerable<MatchingTraceEvent> events)
        {
            var output = new List<(MatchingTraceEvent Event, int ProviderBookRank)>();
            var providerRanks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var evt in events
                         .Where(evt => evt != null)
                         .OrderBy(evt => evt.Rank ?? int.MaxValue))
            {
                var providerKey = TraceData(evt, "bookProviderKey");
                if (string.IsNullOrWhiteSpace(providerKey))
                {
                    providerKey = evt.BookId.HasValue
                        ? $"local-book:{evt.BookId.Value.ToString(CultureInfo.InvariantCulture)}"
                        : "unknown-book";
                }

                if (!providerRanks.TryGetValue(providerKey, out var providerRank))
                {
                    providerRank = providerRanks.Count + 1;
                    providerRanks[providerKey] = providerRank;
                }

                output.Add((evt, providerRank));
            }

            return output;
        }

        private static bool ShouldDisplayFtsCandidate(MatchingTraceEvent evt, AuditFileResult result, int top)
        {
            return (evt.Rank ?? int.MaxValue) <= top ||
                   (evt.BookId.HasValue &&
                    (evt.BookId == result.CurrentEdition?.BookId || evt.BookId == result.MatchedEdition?.BookId)) ||
                   (evt.EditionId.HasValue &&
                    (evt.EditionId == result.CurrentEditionId || evt.EditionId == result.MatchedEditionId));
        }

        private static string TraceScore(MatchingTraceEvent evt)
        {
            return evt.Score?.ToString("R", CultureInfo.InvariantCulture) ?? "-";
        }

        private static string TraceCandidateName(MatchingTraceEvent evt)
        {
            var bookTitle = TraceData(evt, "bookTitle");
            var authorName = TraceData(evt, "authorName");
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(evt.Title))
            {
                parts.Add(evt.Title);
            }

            if (!string.IsNullOrWhiteSpace(bookTitle) &&
                !string.Equals(bookTitle, evt.Title, StringComparison.OrdinalIgnoreCase))
            {
                parts.Add($"book={bookTitle}");
            }

            if (!string.IsNullOrWhiteSpace(authorName))
            {
                parts.Add($"author={authorName}");
            }

            return parts.Count > 0 ? string.Join(" | ", parts) : "-";
        }

        private static void PrintFtsDecisionAudit(AuditRunReport run, int top)
        {
            foreach (var result in run.Results)
            {
                System.Console.WriteLine();
                System.Console.WriteLine($"[fts-audit] file local-bookfile={result.BookFileId?.ToString(CultureInfo.InvariantCulture) ?? "-"} path='{result.Path}'");
                System.Console.WriteLine($"[fts-audit] current local-edition={result.CurrentEditionId?.ToString(CultureInfo.InvariantCulture) ?? "-"} provider-book={result.CurrentBookProviderKey ?? "-"}; production chose local-edition={result.MatchedEditionId?.ToString(CultureInfo.InvariantCulture) ?? "-"} provider-book={result.MatchedBookProviderKey ?? "-"}");

                var attempts = BuildFtsAttempts(run, result.Path);
                if (attempts.Count == 0)
                {
                    System.Console.WriteLine("[fts-audit] book-recall FTS was not reached for this file; an earlier route decided it or matching ended before FTS.");
                    continue;
                }

                for (var attemptIndex = 0; attemptIndex < attempts.Count; attemptIndex++)
                {
                    var attempt = attempts[attemptIndex];
                    var input = attempt.Events.FirstOrDefault(evt => evt.EventType == "fts_input");
                    System.Console.WriteLine($"[fts-audit] attempt={attemptIndex + 1} phase={attempt.Phase} terms=[{string.Join(", ", input?.Terms ?? new List<string>())}]");

                    foreach (var step in new[] { "stage1_book_recall", "stage2_field_ranking", "step1_book_recall", "edition_expansion" })
                    {
                        var queries = attempt.Events.Where(evt => evt.EventType == $"fts_{step}_query").ToList();
                        var summary = attempt.Events.FirstOrDefault(evt => evt.EventType == $"fts_{step}_summary");
                        var rows = RankFtsCandidatesByProviderBook(attempt.Events.Where(evt => evt.EventType == $"fts_{step}_candidate"));
                        if (queries.Count == 0 && summary == null && rows.Count == 0)
                        {
                            continue;
                        }

                        var columns = string.Join(",", queries.Select(query => query.Columns).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase));
                        var queryPreview = string.Join(" || ", queries.Take(3).Select(query => query.Query).Where(value => !string.IsNullOrWhiteSpace(value)));
                        if (queries.Count > 3)
                        {
                            queryPreview += $" || ... {queries.Count - 3} more";
                        }

                        var displayedColumns = string.IsNullOrWhiteSpace(columns) ? "-" : columns;
                        var displayedQuery = string.IsNullOrWhiteSpace(queryPreview) ? "-" : queryPreview;
                        System.Console.WriteLine($"  {step}: queries={queries.Count} columns={displayedColumns} query='{displayedQuery}' elapsed={summary?.ElapsedMilliseconds?.ToString(CultureInfo.InvariantCulture) ?? "-"}ms rows={summary?.ResultCount?.ToString(CultureInfo.InvariantCulture) ?? "0"} distinct-local-books={summary?.DistinctBookCount?.ToString(CultureInfo.InvariantCulture) ?? "0"}");
                        foreach (var row in rows.Where(row => ShouldDisplayFtsCandidate(row.Event, result, top)))
                        {
                            var evt = row.Event;
                            System.Console.WriteLine($"    raw-rank={evt.Rank?.ToString(CultureInfo.InvariantCulture) ?? "-"} local-book-rank={evt.DistinctBookRank?.ToString(CultureInfo.InvariantCulture) ?? "-"} provider-book-rank={row.ProviderBookRank} score={TraceScore(evt)} local-book={evt.BookId?.ToString(CultureInfo.InvariantCulture) ?? "-"} local-edition={evt.EditionId?.ToString(CultureInfo.InvariantCulture) ?? "-"} provider-book={TraceData(evt, "bookProviderKey")} {TraceCandidateName(evt)}");
                        }
                    }

                    var globalRanking = attempt.Events
                        .Where(evt => evt.EventType == "candidate_ranked" &&
                                      TraceData(evt, "selectionScope").StartsWith("global-", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(evt => evt.Rank ?? int.MaxValue)
                        .ToList();
                    if (globalRanking.Count > 0)
                    {
                        System.Console.WriteLine("  production post-gate ranking:");
                        foreach (var evt in globalRanking.Where(evt => ShouldDisplayFtsCandidate(evt, result, top)))
                        {
                            System.Console.WriteLine($"    production-rank={evt.Rank?.ToString(CultureInfo.InvariantCulture) ?? "-"} local-edition={evt.EditionId?.ToString(CultureInfo.InvariantCulture) ?? "-"} provider-book={TraceData(evt, "bookProviderKey")} {TraceCandidateName(evt)} :: {evt.Detail}");
                        }
                    }

                    var selected = attempt.Events.LastOrDefault(evt => evt.EventType == "match_selected");
                    if (selected != null)
                    {
                        System.Console.WriteLine($"  CHOSEN: local-edition={selected.EditionId?.ToString(CultureInfo.InvariantCulture) ?? "-"} provider-edition=[{TraceData(selected, "editionProviderKeys")}] reason={selected.Reason ?? "-"}; it survived every gate and ranked first under the production comparison order above.");
                    }

                    var rejected = attempt.Events.Count(evt => evt.EventType == "candidate_rejected");
                    if (rejected > 0)
                    {
                        System.Console.WriteLine($"  rejected-before-ranking={rejected}; exact reasons are in the JSON/Markdown report.");
                    }
                }
            }
        }

        private static void PrintFtsRecallSummary(AuditRunReport run)
        {
            var firstAttempts = run.Results
                .Select(result => result.FtsAttempts.FirstOrDefault())
                .Where(attempt => attempt != null)
                .ToList();
            var referenced = firstAttempts
                .Where(attempt => !string.IsNullOrWhiteSpace(attempt!.ExpectedProviderBookKey))
                .ToList();
            if (referenced.Count == 0)
            {
                return;
            }

            var step1Found = referenced.Count(attempt => attempt!.Step1ExpectedProviderBookRank.HasValue);
            var step1Top = referenced.Count(attempt => attempt!.Step1ExpectedProviderBookRank == 1);
            var expansionFound = referenced.Count(attempt => attempt!.ExpansionExpectedProviderBookRank.HasValue);
            var step1Ranks = referenced
                .Where(attempt => attempt!.Step1ExpectedProviderBookRank.HasValue)
                .Select(attempt => (long)attempt!.Step1ExpectedProviderBookRank!.Value)
                .OrderBy(rank => rank)
                .ToList();
            var rankStats = step1Ranks.Count == 0
                ? "rank-p50=- rank-p95=-"
                : $"rank-p50={Percentile(step1Ranks, 0.50):F0} rank-p95={Percentile(step1Ranks, 0.95):F0}";
            System.Console.WriteLine($"[libraryaudit][fts] first-attempt current-provider-book reference: recall-rank1={step1Top}/{referenced.Count} recall-found={step1Found}/{referenced.Count} {rankStats}; expansion-contained={expansionFound}/{referenced.Count}");
        }

        private static void WriteAuditReport(AuditReport report, string? outputPath)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "test-logs"));
                Directory.CreateDirectory(root);
                outputPath = Path.Combine(root, $"matchbench-libraryaudit-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json");
            }

            var fullPath = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? ".");
            File.WriteAllText(fullPath, System.Text.Json.JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true
            }));

            var mdPath = Path.ChangeExtension(fullPath, ".md");
            File.WriteAllText(mdPath, BuildAuditMarkdown(report));

            System.Console.WriteLine($"[libraryaudit] wrote JSON report: {fullPath}");
            System.Console.WriteLine($"[libraryaudit] wrote markdown report: {mdPath}");
        }

        private static void AppendFtsDecisionMarkdown(
            List<string> lines,
            AuditRunReport run,
            AuditFileResult result,
            int top)
        {
            lines.Add($"#### {EscapeMd(Path.GetFileName(result.Path))}");
            lines.Add(string.Empty);
            lines.Add($"- path: `{EscapeBackticks(result.Path)}`");
            lines.Add($"- local BookFiles row (forensic locator): `{result.BookFileId?.ToString(CultureInfo.InvariantCulture) ?? "-"}`");
            lines.Add($"- current mapping: {EditionDetails(result.CurrentEdition, result.CurrentEditionId, result.CurrentEditionTitle)}");
            lines.Add($"- production result: {EditionDetails(result.MatchedEdition, result.MatchedEditionId, result.MatchedBookTitle)}");
            lines.Add($"- current provider book: `{EscapeBackticks(result.CurrentBookProviderKey ?? "-")}`");
            lines.Add($"- selected provider book: `{EscapeBackticks(result.MatchedBookProviderKey ?? "-")}`");
            lines.Add(string.Empty);

            lines.Add("##### Exact stored tags replayed");
            lines.Add(string.Empty);
            if (result.EvidenceTags == null || result.EvidenceTags.Count == 0)
            {
                lines.Add("- none");
            }
            else
            {
                foreach (var tag in result.EvidenceTags.OrderBy(tag => tag.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var values = tag.Value ?? new List<string>();
                    if (values.Count == 0)
                    {
                        lines.Add($"- `{EscapeBackticks(tag.Key)}`: _(empty)_");
                        continue;
                    }

                    foreach (var value in values)
                    {
                        lines.Add($"- `{EscapeBackticks(tag.Key)}`: `{EscapeBackticks((value ?? string.Empty).Replace("\r", " ").Replace("\n", " "))}`");
                    }
                }
            }

            lines.Add(string.Empty);
            var attempts = BuildFtsAttempts(run, result.Path);
            if (attempts.Count == 0)
            {
                lines.Add("> Book-recall FTS was not reached for this file. An earlier identifier/route decided it, or matching ended before FTS.");
                lines.Add(string.Empty);
                return;
            }

            for (var attemptIndex = 0; attemptIndex < attempts.Count; attemptIndex++)
            {
                var attempt = attempts[attemptIndex];
                var input = attempt.Events.FirstOrDefault(evt => evt.EventType == "fts_input");
                var completed = attempt.Events.LastOrDefault(evt => evt.EventType == "fts_completed");
                lines.Add($"##### FTS attempt {attemptIndex + 1}: `{EscapeBackticks(attempt.Phase)}`");
                lines.Add(string.Empty);
                lines.Add($"- normalized search terms: `{EscapeBackticks(string.Join(" | ", input?.Terms ?? new List<string>()))}`");
                lines.Add($"- returned candidate source: `{EscapeBackticks(TraceData(completed, "resultSource"))}`");
                lines.Add($"- total book-recall plus edition-expansion time: `{completed?.TotalElapsedMilliseconds?.ToString(CultureInfo.InvariantCulture) ?? "-"} ms`");
                lines.Add(string.Empty);

                foreach (var step in new[] { "stage1_book_recall", "stage2_field_ranking", "step1_book_recall", "edition_expansion" })
                {
                    var stepLabel = step switch
                    {
                        "stage1_book_recall" or "step1_book_recall" => "Book recall (broad FTS)",
                        "stage2_field_ranking" => "Edition ranking (residual fields, accumulated per Edition)",
                        _ => "Edition expansion (unranked siblings)"
                    };
                    var queries = attempt.Events.Where(evt => evt.EventType == $"fts_{step}_query").ToList();
                    var summary = attempt.Events.FirstOrDefault(evt => evt.EventType == $"fts_{step}_summary");
                    var rows = RankFtsCandidatesByProviderBook(attempt.Events.Where(evt => evt.EventType == $"fts_{step}_candidate"));
                    var displayedRows = rows.Where(row => ShouldDisplayFtsCandidate(row.Event, result, top)).ToList();
                    if (queries.Count == 0 && summary == null && rows.Count == 0)
                    {
                        continue;
                    }

                    lines.Add($"###### {stepLabel}");
                    lines.Add(string.Empty);
                    var searchedColumns = string.Join(", ", queries.Select(query => query.Columns).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase));
                    lines.Add($"- residual queries: `{queries.Count.ToString(CultureInfo.InvariantCulture)}`; searched columns: `{EscapeBackticks(string.IsNullOrWhiteSpace(searchedColumns) ? "-" : searchedColumns)}`");
                    foreach (var query in queries)
                    {
                        var fieldKey = TraceData(query, "fieldKey");
                        lines.Add($"  - field `{EscapeBackticks(string.IsNullOrWhiteSpace(fieldKey) ? "-" : fieldKey)}` -> `{EscapeBackticks(query.Columns ?? "-")}`: `{EscapeBackticks(query.Query ?? "-")}`");
                    }
                    lines.Add($"- elapsed: `{summary?.ElapsedMilliseconds?.ToString(CultureInfo.InvariantCulture) ?? "-"} ms`; raw rows: `{summary?.ResultCount?.ToString(CultureInfo.InvariantCulture) ?? "0"}`; distinct local books: `{summary?.DistinctBookCount?.ToString(CultureInfo.InvariantCulture) ?? "0"}`");
                    lines.Add(string.Empty);
                    if (displayedRows.Count == 0)
                    {
                        lines.Add("- no rows");
                        lines.Add(string.Empty);
                        continue;
                    }

                    lines.Add("| Raw FTS rank | Distinct local-book rank | Distinct provider-book rank | Exact score | Provider book | Local book | Local edition | Candidate |");
                    lines.Add("| ---: | ---: | ---: | ---: | --- | ---: | ---: | --- |");
                    foreach (var row in displayedRows)
                    {
                        var evt = row.Event;
                        lines.Add($"| {evt.Rank?.ToString(CultureInfo.InvariantCulture) ?? "-"} | {evt.DistinctBookRank?.ToString(CultureInfo.InvariantCulture) ?? "-"} | {row.ProviderBookRank.ToString(CultureInfo.InvariantCulture)} | {EscapeMd(TraceScore(evt))} | {EscapeMd(TraceData(evt, "bookProviderKey"))} | {evt.BookId?.ToString(CultureInfo.InvariantCulture) ?? "-"} | {evt.EditionId?.ToString(CultureInfo.InvariantCulture) ?? "-"} | {EscapeMd(TraceCandidateName(evt))} |");
                    }

                    var omitted = rows.Count - displayedRows.Count;
                    if (omitted > 0)
                    {
                        lines.Add($"| ... | ... | ... | ... | ... | ... | ... | `{omitted}` other raw rows omitted from Markdown; JSON retains them |");
                    }

                    lines.Add(string.Empty);
                }

                var globalRanking = attempt.Events
                    .Where(evt => evt.EventType == "candidate_ranked" &&
                                  TraceData(evt, "selectionScope").StartsWith("global-", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(evt => evt.Rank ?? int.MaxValue)
                    .ToList();
                var selected = attempt.Events.LastOrDefault(evt => evt.EventType == "match_selected");

                if (globalRanking.Count > 0)
                {
                    lines.Add("###### Production post-gate order");
                    lines.Add(string.Empty);
                    lines.Add($"This is the matcher's actual sorted order after title proof, author, narrator, duration/year, strict leftover eligibility, same-occurrence provider-Book selection, and other eligibility gates. Priority order: `{EscapeBackticks(TraceData(globalRanking[0], "rankingPriority"))}`.");
                    lines.Add(string.Empty);
                    lines.Add("| Production rank | Local edition | Provider edition key(s) | Provider book | Candidate | Exact comparison signals |");
                    lines.Add("| ---: | ---: | --- | --- | --- | --- |");
                    foreach (var evt in globalRanking.Where(evt => ShouldDisplayFtsCandidate(evt, result, top)))
                    {
                        lines.Add($"| {evt.Rank?.ToString(CultureInfo.InvariantCulture) ?? "-"} | {evt.EditionId?.ToString(CultureInfo.InvariantCulture) ?? "-"} | {EscapeMd(TraceData(evt, "editionProviderKeys"))} | {EscapeMd(TraceData(evt, "bookProviderKey"))} | {EscapeMd(TraceCandidateName(evt))} | {EscapeMd(evt.Detail ?? string.Empty)} |");
                    }

                    lines.Add(string.Empty);

                    var globalWinner = globalRanking.FirstOrDefault(evt => evt.Rank == 1);
                    var winningWorkKey = globalWinner == null
                        ? string.Empty
                        : attempt.Events
                            .Where(evt => evt.EventType == "candidate_ranked" &&
                                          TraceData(evt, "selectionScope") == "within-logical-work" &&
                                          evt.EditionId == globalWinner.EditionId)
                            .Select(evt => TraceData(evt, "logicalWorkKey"))
                            .FirstOrDefault(key => !string.IsNullOrWhiteSpace(key)) ?? string.Empty;
                    var withinWinningWork = attempt.Events
                        .Where(evt => evt.EventType == "candidate_ranked" &&
                                      TraceData(evt, "selectionScope") == "within-logical-work" &&
                                      string.Equals(TraceData(evt, "logicalWorkKey"), winningWorkKey, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(evt => evt.Rank ?? int.MaxValue)
                        .ToList();
                    if (withinWinningWork.Count > 1)
                    {
                        lines.Add("###### Edition order inside the winning logical book");
                        lines.Add(string.Empty);
                        lines.Add("| Rank | Local edition | Provider edition key(s) | Candidate | Exact comparison signals |");
                        lines.Add("| ---: | ---: | --- | --- | --- |");
                        foreach (var evt in withinWinningWork.Where(evt => (evt.Rank ?? int.MaxValue) <= top || evt.EditionId == result.MatchedEditionId))
                        {
                            lines.Add($"| {evt.Rank?.ToString(CultureInfo.InvariantCulture) ?? "-"} | {evt.EditionId?.ToString(CultureInfo.InvariantCulture) ?? "-"} | {EscapeMd(TraceData(evt, "editionProviderKeys"))} | {EscapeMd(TraceCandidateName(evt))} | {EscapeMd(evt.Detail ?? string.Empty)} |");
                        }

                        lines.Add(string.Empty);
                    }
                }

                var expansionRows = attempt.Events
                    .Where(evt => evt.EventType == "fts_edition_expansion_candidate" && evt.EditionId.HasValue)
                    .GroupBy(evt => evt.EditionId!.Value)
                    .ToDictionary(group => group.Key, group => group.Min(evt => evt.Rank ?? int.MaxValue));
                var rejections = attempt.Events
                    .Where(evt => evt.EventType == "candidate_rejected")
                    .OrderBy(evt => evt.EditionId.HasValue && expansionRows.TryGetValue(evt.EditionId.Value, out var rank) ? rank : int.MaxValue)
                    .ToList();
                if (rejections.Count > 0)
                {
                    lines.Add("###### Rejected before production ranking");
                    lines.Add(string.Empty);
                    lines.Add("| Expansion row | Local edition | Provider edition key(s) | Candidate | Gate reason | Detail |");
                    lines.Add("| ---: | ---: | --- | --- | --- | --- |");
                    foreach (var evt in rejections.Take(top))
                    {
                        var rawRank = evt.EditionId.HasValue && expansionRows.TryGetValue(evt.EditionId.Value, out var rank) && rank != int.MaxValue
                            ? rank.ToString(CultureInfo.InvariantCulture)
                            : "-";
                        lines.Add($"| {rawRank} | {evt.EditionId?.ToString(CultureInfo.InvariantCulture) ?? "-"} | {EscapeMd(TraceData(evt, "editionProviderKeys"))} | {EscapeMd(TraceCandidateName(evt))} | {EscapeMd(evt.Reason ?? string.Empty)} | {EscapeMd(evt.Detail ?? string.Empty)} |");
                    }

                    if (rejections.Count > top)
                    {
                        lines.Add($"| ... | ... | ... | ... | ... | `{rejections.Count - top}` more rejections retained in JSON |");
                    }

                    lines.Add(string.Empty);
                }

                if (selected != null)
                {
                    var selectedRanking = globalRanking.FirstOrDefault(evt => evt.EditionId == selected.EditionId);
                    lines.Add($"> **Production chose local edition `{selected.EditionId?.ToString(CultureInfo.InvariantCulture) ?? "-"}` (`{EscapeBackticks(TraceData(selected, "editionProviderKeys"))}`) because it survived every rejection gate and ranked `{selectedRanking?.Rank?.ToString(CultureInfo.InvariantCulture) ?? "1"}` in the actual production order. Selection disposition: `{EscapeBackticks(selected.Reason ?? "-")}`.**");
                    lines.Add(string.Empty);
                }
                else
                {
                    lines.Add("> This FTS attempt selected no candidate. Review the rejection table and any later fallback attempt below.");
                    lines.Add(string.Empty);
                }
            }
        }

        private static string BuildAuditMarkdown(AuditReport report)
        {
            const int compactTableLimit = 500;
            const int detailLimit = 250;
            const int narratorWarningLimit = 100;

            var lines = new List<string>
            {
                "# MatchBench Library Audit",
                string.Empty,
                $"- generated: `{report.GeneratedAt:O}`",
                $"- source appdata: `{report.SourceAppData}`",
                $"- workspace appdata: `{report.WorkspaceAppData}`",
                $"- snapshot: `{report.UsedSnapshot}`",
                $"- input source: `{report.InputSource}`",
                $"- prefix: `{report.Prefix}`",
                $"- selected local BookFiles row: `{report.SelectedBookFileId?.ToString(CultureInfo.InvariantCulture) ?? "none"}`",
                $"- selected local Books row: `{report.SelectedBookId?.ToString(CultureInfo.InvariantCulture) ?? "none"}`",
                $"- inputs: `{report.InputCount}`",
                $"- include missing files: `{report.IncludeMissingFiles}`",
                $"- include user-selected mappings: `{report.IncludeUserSelectedMappings}`",
                $"- existing inputs: `{report.ExistingInputCount}`",
                $"- missing inputs: `{report.MissingInputCount}`",
                string.Empty
            };

            foreach (var run in report.Runs)
            {
                var interesting = run.Results
                    .Where(r => r.ChangedFromCurrent || r.Flags.Count > 0)
                    .ToList();
                var narratorWarnings = run.Results
                    .Where(IsMatchedNarratorMissing)
                    .ToList();
                var flagCounts = run.Results
                    .SelectMany(r => r.Flags)
                    .GroupBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(g => g.Count())
                    .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                lines.Add($"## {run.Flow} / {run.Strictness} / pathFallback={run.PathFallback}");
                lines.Add(string.Empty);
                lines.Add($"matched={run.MatchedCount}, unmatched={run.UnmatchedCount}, changed={run.ChangedCount}, mapped-now-unmatched={run.PreviouslyMappedNowUnmatchedCount}, elapsed={run.ElapsedMs}ms");
                lines.Add(string.Empty);

                var firstFtsAttempts = run.Results
                    .Select(result => result.FtsAttempts.FirstOrDefault())
                    .Where(attempt => attempt != null)
                    .ToList();
                var referencedFtsAttempts = firstFtsAttempts
                    .Where(attempt => !string.IsNullOrWhiteSpace(attempt!.ExpectedProviderBookKey))
                    .ToList();
                if (referencedFtsAttempts.Count > 0)
                {
                    var step1Ranks = referencedFtsAttempts
                        .Where(attempt => attempt!.Step1ExpectedProviderBookRank.HasValue)
                        .Select(attempt => (long)attempt!.Step1ExpectedProviderBookRank!.Value)
                        .OrderBy(rank => rank)
                        .ToList();
                    var expansionContained = referencedFtsAttempts.Count(attempt => attempt!.ExpansionExpectedProviderBookRank.HasValue);
                    var ftsTimes = firstFtsAttempts
                        .Where(attempt => attempt!.TotalFtsMilliseconds.HasValue)
                        .Select(attempt => attempt!.TotalFtsMilliseconds!.Value)
                        .OrderBy(value => value)
                        .ToList();

                    lines.Add("### FTS Recall Summary (first attempt per file)");
                    lines.Add(string.Empty);
                    lines.Add("> The current provider-book mapping is a comparison reference, not assumed truth. Copies sharing one provider book count as one semantic answer; local row IDs do not.");
                    lines.Add(string.Empty);
                    lines.Add($"- files with a current provider-book reference: `{referencedFtsAttempts.Count}`");
                    lines.Add($"- Book recall current provider book at rank 1: `{referencedFtsAttempts.Count(attempt => attempt!.Step1ExpectedProviderBookRank == 1)}/{referencedFtsAttempts.Count}`");
                    lines.Add($"- Book recall current provider book found anywhere: `{step1Ranks.Count}/{referencedFtsAttempts.Count}`");
                    lines.Add($"- Book recall rank distribution when found: `p50={(step1Ranks.Count > 0 ? Percentile(step1Ranks, 0.50).ToString("F0", CultureInfo.InvariantCulture) : "-")}, p95={(step1Ranks.Count > 0 ? Percentile(step1Ranks, 0.95).ToString("F0", CultureInfo.InvariantCulture) : "-")}, max={(step1Ranks.Count > 0 ? step1Ranks[^1].ToString(CultureInfo.InvariantCulture) : "-")}`");
                    lines.Add($"- Recalled provider books present after sibling-edition expansion: `{expansionContained}/{referencedFtsAttempts.Count}`");
                    lines.Add($"- book-recall plus edition-expansion time: `p50={(ftsTimes.Count > 0 ? Percentile(ftsTimes, 0.50).ToString("F0", CultureInfo.InvariantCulture) : "-")} ms, p95={(ftsTimes.Count > 0 ? Percentile(ftsTimes, 0.95).ToString("F0", CultureInfo.InvariantCulture) : "-")} ms, max={(ftsTimes.Count > 0 ? ftsTimes[^1].ToString(CultureInfo.InvariantCulture) : "-")} ms`");
                    lines.Add(string.Empty);
                }

                if (run.TraceTruncated)
                {
                    lines.Add($"> WARNING: Matching trace was truncated at `{run.TraceLimit}` retained events; `{run.TraceDroppedEventCount}` of `{run.TraceTotalEventCount}` events were omitted. Re-run with a larger `/tracelimit` before treating absent rejection evidence as meaningful.");
                    lines.Add(string.Empty);
                }

                if (report.FtsDetailsRequested)
                {
                    lines.Add("### Exact FTS and Production Decisions");
                    lines.Add(string.Empty);
                    lines.Add($"Raw FTS tables show the first `{report.FtsTop}` rows plus the current/selected provider identities when they fall lower. The JSON retains every captured row. Local row IDs are labeled only as forensic locators; provider identities remain the semantic answer keys.");
                    lines.Add(string.Empty);
                    foreach (var result in run.Results)
                    {
                        AppendFtsDecisionMarkdown(lines, run, result, report.FtsTop);
                    }
                }

                lines.Add("### Flag Counts");
                lines.Add(string.Empty);
                if (flagCounts.Count == 0)
                {
                    lines.Add("- none");
                }
                else
                {
                    foreach (var flag in flagCounts)
                    {
                        lines.Add($"- `{flag.Key}`: `{flag.Count()}`");
                    }
                }

                lines.Add(string.Empty);

                if (narratorWarnings.Count > 0)
                {
                    lines.Add("### Narrator Evidence Warnings");
                    lines.Add(string.Empty);
                    lines.Add($"> WARNING: MatchBench matched `{narratorWarnings.Count}` file(s) to an edition where at least one DB narrator name was NOT found anywhere in that file's tags. This can be legitimate when files omit narrator tags, but every row here deserves edition-level review.");
                    lines.Add(string.Empty);
                    lines.Add("| File | Would Match | Missing DB Narrator(s) | Found DB Narrator(s) | Tag Evidence | Flags |");
                    lines.Add("| --- | --- | --- | --- | --- | --- |");

                    foreach (var result in narratorWarnings.Take(narratorWarningLimit))
                    {
                        lines.Add($"| {EscapeMd(Path.GetFileName(result.Path))} | {EscapeMd(EditionBrief(result.MatchedEdition, result.MatchedEditionId, result.MatchedBookTitle))} | {EscapeMd(NarratorListDisplay(result.MatchedNarratorsMissingFromTags))} | {EscapeMd(NarratorListDisplay(result.MatchedNarratorsFoundInTags))} | {EscapeMd(NarratorEvidenceSummary(result))} | {EscapeMd(string.Join(", ", result.Flags))} |");
                    }

                    if (narratorWarnings.Count > narratorWarningLimit)
                    {
                        lines.Add($"| ... | ... | ... | ... | ... | `{narratorWarnings.Count - narratorWarningLimit}` more narrator warning rows omitted; JSON contains all rows |");
                    }

                    lines.Add(string.Empty);
                }

                lines.Add("### Review Table");
                lines.Add(string.Empty);
                lines.Add("| File | Current | Would Match | Key Signals | Flags | Reason |");
                lines.Add("| --- | --- | --- | --- | --- | --- |");

                foreach (var result in interesting.Take(compactTableLimit))
                {
                    lines.Add($"| {EscapeMd(Path.GetFileName(result.Path))} | {EscapeMd(EditionBrief(result.CurrentEdition, result.CurrentEditionId, result.CurrentEditionTitle))} | {EscapeMd(EditionBrief(result.MatchedEdition, result.MatchedEditionId, result.MatchedBookTitle))} | {EscapeMd(string.Join("; ", result.Signals.Take(4)))} | {EscapeMd(string.Join(", ", result.Flags))} | {EscapeMd(result.UnmatchedReason ?? string.Empty)} |");
                }

                if (interesting.Count > compactTableLimit)
                {
                    lines.Add($"| ... | ... | ... | ... | ... | `{interesting.Count - compactTableLimit}` more rows omitted from compact table |");
                }

                lines.Add(string.Empty);
                lines.Add("### Review Details");
                lines.Add(string.Empty);

                foreach (var result in interesting.Take(detailLimit))
                {
                    lines.Add($"#### {EscapeMd(Path.GetFileName(result.Path))}");
                    lines.Add(string.Empty);
                    lines.Add($"- path: `{result.Path}`");
                    lines.Add($"- source: `{result.Source}`");
                    lines.Add($"- file duration: `{(result.FileDurationSeconds.HasValue ? FormatDuration(result.FileDurationSeconds.Value) : "unknown")}`");
                    lines.Add($"- size: `{FormatBytes(result.SizeBytes)}`");
                    lines.Add($"- flags: `{(result.Flags.Count > 0 ? string.Join(", ", result.Flags) : "none")}`");
                    if (!string.IsNullOrWhiteSpace(result.UnmatchedReason))
                    {
                        lines.Add($"- unmatched reason: `{result.UnmatchedReason}`");
                    }

                    lines.Add($"- current: {EditionDetails(result.CurrentEdition, result.CurrentEditionId, result.CurrentEditionTitle)}");
                    lines.Add($"- would match: {EditionDetails(result.MatchedEdition, result.MatchedEditionId, result.MatchedBookTitle)}");
                    lines.Add($"- current provider answer keys: `{EscapeBackticks(result.CurrentEditionProviderKeys.Count > 0 ? string.Join(", ", result.CurrentEditionProviderKeys) : "none")}`");
                    lines.Add($"- matched provider answer keys: `{EscapeBackticks(result.MatchedEditionProviderKeys.Count > 0 ? string.Join(", ", result.MatchedEditionProviderKeys) : "none")}`");

                    if (IsMatchedNarratorMissing(result))
                    {
                        lines.Add($"- NARRATOR WARNING: matched edition has DB narrator name(s) NOT FOUND in file tags: `{EscapeBackticks(NarratorListDisplay(result.MatchedNarratorsMissingFromTags))}`");
                        if (result.MatchedNarratorsFoundInTags.Count > 0)
                        {
                            lines.Add($"- narrator(s) found in tags: `{EscapeBackticks(NarratorListDisplay(result.MatchedNarratorsFoundInTags))}`");
                        }

                        lines.Add($"- narrator tag evidence: {EscapeMd(NarratorEvidenceSummary(result))}");
                    }

                    if (result.Signals.Count > 0)
                    {
                        lines.Add($"- signals: {EscapeMd(string.Join("; ", result.Signals))}");
                    }

                    if (result.TagSummary.Count > 0)
                    {
                        lines.Add($"- tags: {EscapeMd(string.Join("; ", result.TagSummary))}");
                    }

                    lines.Add(string.Empty);
                }

                if (interesting.Count > detailLimit)
                {
                    lines.Add($"_Detail section truncated after {detailLimit} rows; JSON contains all {interesting.Count} changed/flagged rows._");
                    lines.Add(string.Empty);
                }

                lines.Add(string.Empty);
            }

            return string.Join(Environment.NewLine, lines);
        }

        private static bool IsMatchedNarratorMissing(AuditFileResult result)
        {
            return result.MatchedNarratorsMissingFromTags.Count > 0 ||
                   result.Flags.Any(f => string.Equals(f, "matched-narrator-not-in-tags", StringComparison.OrdinalIgnoreCase));
        }

        private static string NarratorEvidenceSummary(AuditFileResult result)
        {
            var narratorTags = result.TagSummary
                .Where(IsNarratorEvidenceSummaryTag)
                .Take(6)
                .ToList();

            if (narratorTags.Count > 0)
            {
                return "narrator-ish tags present, but missing DB narrator(s) were not found: " + string.Join("; ", narratorTags);
            }

            if (result.TagSummary.Count == 0)
            {
                return "no tags captured";
            }

            return "no narrator-ish tags shown; key tags: " + string.Join("; ", result.TagSummary.Take(8));
        }

        private static bool IsNarratorEvidenceSummaryTag(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return value.Contains("narrat", StringComparison.OrdinalIgnoreCase) ||
                   value.Contains("composer", StringComparison.OrdinalIgnoreCase) ||
                   value.Contains("TCOM", StringComparison.OrdinalIgnoreCase) ||
                   value.Contains("©wrt", StringComparison.OrdinalIgnoreCase) ||
                   value.Contains(":wrt", StringComparison.OrdinalIgnoreCase) ||
                   value.Contains("writer", StringComparison.OrdinalIgnoreCase);
        }

        private static string NarratorListDisplay(List<string>? narrators)
        {
            if (narrators == null || narrators.Count == 0)
            {
                return "-";
            }

            var displayed = string.Join(", ", narrators.Take(8));
            return narrators.Count > 8
                ? $"{displayed}, +{narrators.Count - 8} more"
                : displayed;
        }

        private static string EditionBrief(EditionAuditInfo? edition, int? fallbackId, string? fallbackTitle)
        {
            if (edition == null)
            {
                return fallbackId.HasValue ? $"local-edition={fallbackId}: {fallbackTitle}" : "-";
            }

            var title = !string.IsNullOrWhiteSpace(edition.Title) ? edition.Title : fallbackTitle;
            var bits = new List<string>
            {
                $"local-edition={edition.EditionId}: {title}"
            };

            if (!string.IsNullOrWhiteSpace(edition.BookTitle) && !string.Equals(edition.BookTitle, title, StringComparison.OrdinalIgnoreCase))
            {
                bits.Add($"book={edition.BookTitle}");
            }

            if (!string.IsNullOrWhiteSpace(edition.AuthorName))
            {
                bits.Add($"author={edition.AuthorName}");
            }

            return string.Join(" | ", bits);
        }

        private static string EditionDetails(EditionAuditInfo? edition, int? fallbackId, string? fallbackTitle)
        {
            if (edition == null)
            {
                return fallbackId.HasValue ? EscapeMd($"local-edition={fallbackId}: {fallbackTitle}") : "-";
            }

            var bits = new List<string>
            {
                $"local-edition=`{edition.EditionId}` {EscapeMd(edition.Title ?? fallbackTitle ?? "-")}",
                $"local-book=`{edition.BookId}` {EscapeMd(edition.BookTitle ?? "-")}",
                $"local-author=`{edition.AuthorId}` {EscapeMd(edition.AuthorName ?? "-")}"
            };

            AddDetail(bits, "base", edition.BookBaseBookId);
            AddDetail(bits, "foreign", edition.ForeignEditionId);
            AddDetail(bits, "asin", edition.Asin);
            AddDetail(bits, "audible", edition.AudibleASIN);
            var asins = EditionAsins(edition);
            if (asins.Count > 0)
            {
                bits.Add($"asins=`{string.Join(", ", asins.Take(10))}`");
            }

            AddDetail(bits, "narrator", NarratorDisplay(edition));
            if (edition.DurationSeconds.HasValue)
            {
                bits.Add($"duration=`{FormatDuration(edition.DurationSeconds.Value)}`");
            }

            if (edition.ReleaseDate.HasValue)
            {
                bits.Add($"release=`{edition.ReleaseDate.Value:yyyy-MM-dd}`");
            }

            bits.Add($"chapters=`{(edition.HasChapters ? "yes" : "no")}{(edition.ChapterCount.HasValue ? $"/{edition.ChapterCount.Value}" : string.Empty)}`");
            if (edition.ReadingFormatId.HasValue)
            {
                bits.Add($"rf=`{edition.ReadingFormatId}`");
            }

            AddDetail(bits, "format", edition.EditionFormat ?? edition.Format);
            AddDetail(bits, "publisher", edition.Publisher);
            AddDetail(bits, "series", SeriesDisplay(edition));
            if (edition.IsFallbackEdition)
            {
                bits.Add("fallback=`true`");
            }

            return string.Join(" ; ", bits);
        }

        private static string? SeriesDisplay(EditionAuditInfo edition)
        {
            if (edition == null || string.IsNullOrWhiteSpace(edition.SeriesName))
            {
                return null;
            }

            return string.IsNullOrWhiteSpace(edition.SeriesPosition)
                ? edition.SeriesName
                : $"{edition.SeriesName} #{edition.SeriesPosition}";
        }

        private static void AddDetail(List<string> bits, string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                bits.Add($"{label}=`{EscapeBackticks(value)}`");
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0)
            {
                return "-";
            }

            string[] units = { "B", "KB", "MB", "GB", "TB" };
            var value = (double)bytes;
            var unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return $"{value:0.##} {units[unit]}";
        }

        private static string EscapeMd(string? value)
        {
            return (value ?? string.Empty).Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
        }

        private static string EscapeBackticks(string? value)
        {
            return (value ?? string.Empty).Replace("`", "'");
        }

        private static SqliteConnection OpenReadonlySqlite(string dbPath)
        {
            var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString());
            conn.Open();
            return conn;
        }

        private static Dictionary<string, List<string>> CloneTags(Dictionary<string, List<string>>? tags)
        {
            if (tags == null || tags.Count == 0)
            {
                return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            }

            return tags
                .Where(kv => !string.IsNullOrWhiteSpace(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value?.Where(v => !string.IsNullOrWhiteSpace(v)).ToList() ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        }

        private static DateTime FromUnixNanoseconds(long ns)
        {
            if (ns <= 0)
            {
                return DateTime.UtcNow;
            }

            try
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(ns / 1000000L).UtcDateTime;
            }
            catch
            {
                return DateTime.UtcNow;
            }
        }

        private static int RunProwlarrBench(StartupContext startupContext)
        {
            var prowlarrUrl = GetArg(startupContext, "prowlarr") ??
                              Environment.GetEnvironmentVariable("PROWLARR_URL");
            var apiKey = GetArg(startupContext, "apikey") ??
                         Environment.GetEnvironmentVariable("PROWLARR_API_KEY");

            var dbPath = GetArg(startupContext, "db") ??
                         Environment.GetEnvironmentVariable("CHAPTARR_DB") ??
                         "/config/chaptarr.db";

            if (string.IsNullOrWhiteSpace(prowlarrUrl))
            {
                System.Console.Error.WriteLine("Missing required Prowlarr URL. Provide /prowlarr=<url> or set PROWLARR_URL.");
                return 2;
            }

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                System.Console.Error.WriteLine("Missing required Prowlarr API key. Provide /apikey=<key> or set PROWLARR_API_KEY.");
                return 2;
            }

            var sample = Math.Max(1, GetIntArg(startupContext, "sample", 10));
            var perBookMax = Math.Max(10, GetIntArg(startupContext, "max", 60));
            var queryMode = (GetArg(startupContext, "querymode") ?? "both").Trim().ToLowerInvariant();
            var worstCase = GetBoolArg(startupContext, "worstcase", false);
            var indexerIds = (GetArg(startupContext, "indexerids") ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(value => int.TryParse(value, out var id) ? id : 0)
                .Where(id => id > 0)
                .Distinct()
                .ToList();

            var useAuthorTitle = queryMode == "both" || queryMode == "authortitle";
            var useTitleOnly = queryMode == "both" || queryMode == "titleonly";

            if (!useAuthorTitle && !useTitleOnly)
            {
                System.Console.Error.WriteLine("Invalid /querymode. Use 'both', 'authorTitle', or 'titleOnly'.");
                return 2;
            }

            var sourceAppData = GetArg(startupContext, "data") ?? Path.GetDirectoryName(Path.GetFullPath(dbPath)) ?? "/config";
            if (RejectPostgresForSqliteSnapshot(new StartupContext("/prowlarrbench", $"/data={sourceAppData}"), "prowlarrbench"))
            {
                return 2;
            }

            using var workspace = PrepareAuditWorkspace(startupContext, sourceAppData, live: false, sourceDatabasePath: dbPath);
            dbPath = Path.Combine(workspace.AppData, "chaptarr.db");
            using var host = BuildHost(workspace.StartupContext, blockOutboundHttp: true);
            using var scope = host.Services.CreateScope();
            var services = scope.ServiceProvider;
            var inputLoader = new ProductionSearchInputLoader(services);
            System.Console.WriteLine($"[prowlarrbench] db snapshot='{dbPath}', sample={sample}, max={perBookMax}, queryMode='{queryMode}', worstCase={worstCase}");
            System.Console.WriteLine("[prowlarrbench] Prowlarr responses are recall-only because the adapter does not preserve full production release metadata");
            System.Console.WriteLine("[prowlarrbench] categories are scoped per target: 3030 (audiobook) or 7000-7999 (ebook)");
            System.Console.WriteLine($"[prowlarrbench] indexers: {(indexerIds.Count == 0 ? "all" : string.Join(",", indexerIds))}");

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            using var http = new HttpClient
            {
                BaseAddress = new Uri(prowlarrUrl.TrimEnd('/')),
                Timeout = TimeSpan.FromSeconds(60)
            };

            http.DefaultRequestHeaders.Add("X-Api-Key", apiKey);

            var overrideAuthor = GetArg(startupContext, "author");
            var overrideTitle = GetArg(startupContext, "title");
            var casesArg = GetArg(startupContext, "cases");
            var bookIds = ParseTargetBookIds(GetArg(startupContext, "bookids"));

            List<LibraryBookRow> sampleBooks;
            if (bookIds.Count > 0)
            {
                sampleBooks = LoadBooksByIds(dbPath, bookIds);
            }
            else if (!string.IsNullOrWhiteSpace(casesArg))
            {
                sampleBooks = ParseCasesArg(casesArg);
            }
            else if (!string.IsNullOrWhiteSpace(overrideAuthor) && !string.IsNullOrWhiteSpace(overrideTitle))
            {
                sampleBooks = new List<LibraryBookRow>
                {
                    new()
                    {
                        AuthorId = 0,
                        AuthorName = overrideAuthor.Trim(),
                        BookId = 0,
                        BookTitle = overrideTitle.Trim()
                    }
                };
            }
            else
            {
                try
                {
                    sampleBooks = worstCase
                        ? LoadWorstCaseBooks(dbPath, sample)
                        : LoadRandomBooks(dbPath, sample);
                }
                catch (Exception ex)
                {
                    System.Console.Error.WriteLine($"[prowlarrbench] Failed to read '{dbPath}': {ex.Message}");
                    return 1;
                }
            }

            if (sampleBooks.Count == 0)
            {
                System.Console.Error.WriteLine("[prowlarrbench] No books found to sample.");
                return 1;
            }

            var incompleteQueryBookCount = 0;
            foreach (var row in sampleBooks)
            {
                if (row == null || row.BookTitle.IsNullOrWhiteSpace() || row.AuthorName.IsNullOrWhiteSpace())
                {
                    continue;
                }

                var input = inputLoader.Load(row, userInvokedSearch: false, interactiveSearch: true);
                var author = input.Author;
                var book = input.TargetBook;
                var criteria = input.Criteria;
                var searchTitle = criteria.BookTitle;
                if (input.IsSynthetic)
                {
                    System.Console.WriteLine("[prowlarrbench] input=synthetic author/title probe; catalog comes from the supplied case, not the database");
                }

                var queries = new List<(string Label, string Query)>();
                if (useAuthorTitle)
                {
                    queries.Add(("title+author", $"{searchTitle} {author.Name}".Trim()));
                    queries.Add(("author+title", $"{author.Name} {searchTitle}".Trim()));
                }

                if (useTitleOnly)
                {
                    queries.Add(("title-only", searchTitle.Trim()));
                }

                var rawResults = new Dictionary<string, (ProwlarrSearchResult Result, HashSet<string> Sources)>(StringComparer.OrdinalIgnoreCase);
                var queryResultCounts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var incompleteQueries = new List<string>();

                foreach (var (label, query) in queries)
                {
                    var fetch = FetchProwlarrSearch(http, options, query, indexerIds, row.MediaType);
                    queryResultCounts[label] = fetch.Error == null ? fetch.Results.Count.ToString(CultureInfo.InvariantCulture) : $"INCOMPLETE ({fetch.Error})";
                    if (fetch.Error != null)
                    {
                        incompleteQueries.Add(label);
                    }

                    foreach (var r in fetch.Results)
                    {
                        var guid = r.Guid;
                        var key = !string.IsNullOrWhiteSpace(guid)
                            ? guid
                            : $"{r.IndexerId}:{r.Title}";

                        if (!rawResults.TryGetValue(key, out var existing))
                        {
                            rawResults[key] = (r, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { label });
                        }
                        else
                        {
                            existing.Sources.Add(label);
                            rawResults[key] = (existing.Result, existing.Sources);
                        }
                    }
                }

                var all = rawResults.Values.Where(value => value.Result != null).ToList();
                var allowed = all.Where(value => IsAllowedBookCategory(value.Result, book.MediaType)).ToList();
                var categoryMatchCount = allowed.Count;
                if (allowed.Count > perBookMax)
                {
                    allowed = allowed.Take(perBookMax).ToList();
                }

                if (incompleteQueries.Count > 0)
                {
                    incompleteQueryBookCount++;
                }

                System.Console.WriteLine();
                System.Console.WriteLine($"[prowlarrbench] {author.Name} — {searchTitle} (book='{book.Title}', localBookId={book.Id}, media={book.MediaType})");
                System.Console.WriteLine($"  recall-only: Prowlarr's result DTO omits production release fields, so no production verdict or ranking is reported; matching catalog={(input.IsSynthetic ? "synthetic" : "hydrated snapshot")}");
                System.Console.WriteLine($"  returned={all.Count} bookCategories={categoryMatchCount} shown={allowed.Count}{(categoryMatchCount > allowed.Count ? " (display-limited)" : string.Empty)} queryAudit={(incompleteQueries.Count == 0 ? "complete" : "INCOMPLETE")}");
                if (input.IsSynthetic)
                {
                    System.Console.WriteLine("  INCOMPLETE: synthetic target has no hydrated database catalog/profile; this is a recall-only probe");
                }
                System.Console.WriteLine($"  query results: {string.Join(", ", queryResultCounts.Select(pair => $"{pair.Key}={pair.Value}"))}");

                if (allowed.Count == 0 && all.Count > 0)
                {
                    System.Console.WriteLine("  no results in the target book media categories");
                }

                if (allowed.Count > 0)
                {
                    System.Console.WriteLine("  observed candidates:");
                    foreach (var entry in allowed)
                    {
                        var result = entry.Result;
                        var cats = string.Join(",", (result.Categories ?? new List<ProwlarrCategory>()).Where(c => c != null).Select(c => c.Id));
                        System.Console.WriteLine($"    - {result.Title} (idx='{result.Indexer}', queries=[{string.Join(",", entry.Sources)}], seeders={result.Seeders}, cats=[{cats}])");
                    }
                }
            }

            System.Console.WriteLine($"[prowlarrbench] incomplete query books={incompleteQueryBookCount}; all Prowlarr output is recall-only");
            return incompleteQueryBookCount == 0 ? 0 : 1;
        }

        private static int RunReleaseReplay(StartupContext startupContext)
        {
            var dbPath = GetArg(startupContext, "db") ??
                         Environment.GetEnvironmentVariable("CHAPTARR_DB") ??
                         "/config/chaptarr.db";
            var releasePath = GetArg(startupContext, "releases");
            var bookIds = ParseTargetBookIds(GetArg(startupContext, "bookids"));
            var interactiveSearch = GetBoolArg(startupContext, "interactive", true);
            var rss = GetBoolArg(startupContext, "rss", false);

            if (string.IsNullOrWhiteSpace(releasePath) || (!rss && bookIds.Count == 0))
            {
                System.Console.Error.WriteLine("[releasereplay] Provide /releases=<serialized-release-array.json> and either /bookids=1,2,... from the SQLite snapshot or /rss=true for an unscoped RSS batch.");
                return 2;
            }

            if (rss && bookIds.Count > 0)
            {
                System.Console.Error.WriteLine("[releasereplay] RSS is unscoped; omit /bookids when using /rss=true.");
                return 2;
            }

            var sourceAppData = GetArg(startupContext, "data") ?? Path.GetDirectoryName(Path.GetFullPath(dbPath)) ?? "/config";
            var snapshotContext = new StartupContext("/releasereplay", $"/data={sourceAppData}");
            if (RejectPostgresForSqliteSnapshot(snapshotContext, "releasereplay"))
            {
                return 2;
            }

            List<ReleaseInfo> reports;
            try
            {
                var json = File.ReadAllText(releasePath);
                reports = DeserializeTypedReleaseReports(json);
            }
            catch (Exception ex)
            {
                System.Console.Error.WriteLine($"[releasereplay] Could not read typed release reports: {ex.Message}");
                return 2;
            }

            var missingByReport = reports.Select((release, index) =>
            {
                var missing = GetMissingReleaseSourceFields(release);
                return (Index: index + 1, Release: release, Missing: missing);
            }).ToList();
            var completeSourceData = missingByReport.All(item => item.Missing.Count == 0);

            using var workspace = PrepareAuditWorkspace(startupContext, sourceAppData, live: false, sourceDatabasePath: dbPath);
            var snapshotDbPath = Path.Combine(workspace.AppData, "chaptarr.db");
            var sampleBooks = rss ? new List<LibraryBookRow>() : LoadBooksByIds(snapshotDbPath, bookIds);
            if (sampleBooks.Count != bookIds.Count)
            {
                var loadedIds = sampleBooks.Select(row => row.BookId).ToHashSet();
                var missingIds = bookIds.Where(id => !loadedIds.Contains(id));
                System.Console.Error.WriteLine($"[releasereplay] Missing local book id(s) in the SQLite snapshot: {string.Join(",", missingIds)}");
                return 1;
            }

            using var host = BuildHost(workspace.StartupContext, blockOutboundHttp: true, recordDownloads: rss);
            using var scope = host.Services.CreateScope();
            var services = scope.ServiceProvider;
            var inputLoader = new ProductionSearchInputLoader(services);
            var searchService = services.GetRequiredService<ReleaseSearchService>();
            var prioritizer = services.GetRequiredService<IPrioritizeDownloadDecision>();
            var networkGuard = (OfflineReplayHttpDispatcher)services.GetRequiredService<IHttpDispatcher>();
            var incompleteBookCount = 0;

            System.Console.WriteLine(rss
                ? $"[releasereplay] db snapshot='{snapshotDbPath}', mode=RSS, typedReports={reports.Count}, unscoped=true"
                : $"[releasereplay] db snapshot='{snapshotDbPath}', books={sampleBooks.Count}, typedReports={reports.Count}, interactive={interactiveSearch}");
            System.Console.WriteLine("[releasereplay] replay uses the production decision maker and prioritizer; outbound HTTP from decision processing is blocked");
            System.Console.WriteLine("[releasereplay] SQLite snapshot replay cannot restore transient live queue, cache, or wall-clock state");

            foreach (var item in missingByReport.Where(item => item.Missing.Count > 0))
            {
                System.Console.WriteLine($"[releasereplay] report #{item.Index} '{item.Release.Title ?? "<untitled>"}' is incomplete: missing {string.Join(", ", item.Missing)}");
            }

            if (rss)
            {
                // The offline snapshot has no live download-client queue to refresh.
                ((NzbDrone.Core.Queue.QueueService)services.GetRequiredService<NzbDrone.Core.Queue.IQueueService>())
                    .Handle(new NzbDrone.Core.Download.TrackedDownloads.TrackedDownloadRefreshedEvent(
                        new List<NzbDrone.Core.Download.TrackedDownloads.TrackedDownload>()));

                var replay = RunProductionRssDecision(
                    services.GetRequiredService<IMakeDownloadDecision>(), prioritizer, networkGuard, reports,
                    services.GetRequiredService<IPendingReleaseService>(), services.GetRequiredService<IProcessDownloadDecisions>(), completeSourceData);
                System.Console.WriteLine();
                System.Console.WriteLine("[releasereplay] RSS batch");
                WriteReleaseReplayResult(replay, includeTargets: true);
                System.Console.WriteLine();
                System.Console.WriteLine($"[releasereplay] completed RSS batches={(replay.Complete ? 1 : 0)}, incomplete RSS batches={(replay.Complete ? 0 : 1)}, typedReports={reports.Count}, decisions={replay.Decisions.Count}, plannedGrabs={replay.Processed?.Grabbed.Count}, pending={replay.Processed?.Pending.Count}, downloads=0");
                return replay.Complete ? 0 : 1;
            }

            foreach (var row in sampleBooks)
            {
                var input = inputLoader.Load(row, userInvokedSearch: true, interactiveSearch: interactiveSearch);
                var replay = RunProductionSearchDecision(
                    searchService,
                    prioritizer,
                    networkGuard,
                    reports,
                    input.Criteria,
                    input.IsSynthetic,
                    completeSourceData);
                if (!replay.Complete)
                {
                    incompleteBookCount++;
                }

                System.Console.WriteLine();
                System.Console.WriteLine($"[releasereplay] {input.Author.Name} — {input.Criteria.BookTitle} (book='{input.TargetBook.Title}', localBookId={input.TargetBook.Id}, media={input.TargetBook.MediaType})");
                WriteReleaseReplayResult(replay);
            }

            System.Console.WriteLine();
            System.Console.WriteLine($"[releasereplay] completed books={sampleBooks.Count - incompleteBookCount}, incomplete books={incompleteBookCount}, downloads=0");
            return incompleteBookCount == 0 ? 0 : 1;
        }

        private static void WriteReleaseReplayResult(ProductionSearchDecisionReplay replay, bool includeTargets = false)
        {
            System.Console.WriteLine($"  {DescribeReplayOutcome(replay)}");
            if (replay.BlockedExternalRequestCount > 0)
            {
                System.Console.WriteLine($"  offline guard blocked {replay.BlockedExternalRequestCount} decision-time HTTP request(s): {string.Join(", ", replay.BlockedRequests.Distinct())}");
            }
            if (replay.UnaccountedReportCount > 0)
            {
                System.Console.WriteLine($"  {replay.UnaccountedReportCount} input report(s) have no recorded decision; RSS completeness is not established");
            }
            if (!replay.HasCompleteSourceData)
            {
                System.Console.WriteLine("  source reports are incomplete; per-release outcomes are diagnostic only and accepted verdicts are withheld");
            }

            var rank = 0;
            foreach (var decision in replay.Decisions)
            {
                var remoteBook = decision.RemoteBook;
                var release = remoteBook?.Release;
                if (release == null)
                {
                    continue;
                }

                var match = remoteBook?.SearchCriteriaMatch;
                var leftovers = match?.MeaningfulLeftovers?.Count > 0 ? $" leftovers=[{string.Join(", ", match.MeaningfulLeftovers)}]" : string.Empty;
                var targets = includeTargets
                    ? $" targets=[{string.Join(", ", (remoteBook?.Books ?? new List<Book>()).Select(book => $"{remoteBook?.Author?.Name} — {book.Title} (localBookId={book.Id}, media={book.MediaType})"))}]"
                    : string.Empty;
                var reasons = string.Join("; ", decision.Rejections.Select(rejection => $"{rejection.Category}: {rejection.Reason}"));
                if (!replay.Complete)
                {
                    System.Console.WriteLine($"    INCOMPLETE - {release.Title}{leftovers}{targets} recordedRejections=[{reasons}]");
                    continue;
                }

                var status = decision.Approved ? "APPROVED" : "REJECTED";
                if (decision.Approved)
                {
                    rank++;
                    System.Console.WriteLine($"    #{rank} {status} - {release.Title}{leftovers}{targets} (indexer='{release.Indexer}', seeders={TorrentInfo.GetSeeders(release) ?? 0}, size={release.Size})");
                }
                else
                {
                    System.Console.WriteLine($"    {status} - {release.Title}{leftovers}{targets} rejections=[{reasons}]");
                }
            }
        }

        private static List<ReleaseInfo> DeserializeTypedReleaseReports(string json)
        {
            var root = JToken.Parse(json) as JArray
                ?? throw new InvalidDataException("The release file must contain a JSON array.");
            var serializer = Newtonsoft.Json.JsonSerializer.Create(NzbDrone.Common.Serializer.Json.GetSerializerSettings());
            var reports = new List<ReleaseInfo>(root.Count);

            foreach (var token in root)
            {
                if (token is not JObject reportObject)
                {
                    throw new InvalidDataException("Each release entry must be a JSON object.");
                }

                var protocolText = reportObject.Properties()
                    .FirstOrDefault(property => property.Name.Equals("downloadProtocol", StringComparison.OrdinalIgnoreCase))?
                    .Value?.ToString();
                Enum.TryParse(protocolText, ignoreCase: true, out DownloadProtocol protocol);
                var hasTorrentFields = reportObject.Properties().Any(property =>
                    property.Name.Equals("magnetUrl", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Equals("infoHash", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Equals("seeders", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Equals("peers", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Equals("fileType", StringComparison.OrdinalIgnoreCase));
                var reportType = protocol == DownloadProtocol.Torrent || hasTorrentFields
                    ? typeof(TorrentInfo)
                    : typeof(ReleaseInfo);
                var report = token.ToObject(reportType, serializer) as ReleaseInfo
                    ?? throw new InvalidDataException("A release entry could not be deserialized as ReleaseInfo or TorrentInfo.");
                reports.Add(report);
            }

            return reports;
        }

        private static List<string> GetMissingReleaseSourceFields(ReleaseInfo release)
        {
            var missing = new List<string>();
            if (release == null || string.IsNullOrWhiteSpace(release.Title)) missing.Add("title");
            if (release == null || string.IsNullOrWhiteSpace(release.Guid)) missing.Add("guid");
            if (release == null || string.IsNullOrWhiteSpace(release.Indexer)) missing.Add("indexer");
            if (release == null || release.IndexerId <= 0) missing.Add("indexerId");
            if (release == null || release.PublishDate == default) missing.Add("publishDate");
            if (release == null || release.DownloadProtocol == DownloadProtocol.Unknown) missing.Add("downloadProtocol");
            if (release == null || release.Size <= 0) missing.Add("size");
            if (release == null || (string.IsNullOrWhiteSpace(release.DownloadUrl) && string.IsNullOrWhiteSpace((release as TorrentInfo)?.MagnetUrl))) missing.Add("downloadUrl");
            return missing;
        }

        private static int RunMamBench(StartupContext startupContext)
        {
            var dbPath = GetArg(startupContext, "db") ??
                         Environment.GetEnvironmentVariable("CHAPTARR_DB") ??
                         "/config/chaptarr.db";
            var proxyUrl = GetArg(startupContext, "proxy");
            var bookIds = ParseTargetBookIds(GetArg(startupContext, "bookids"));
            var interactiveSearch = GetBoolArg(startupContext, "interactive", true);
            var indexerId = GetNullableIntArg(startupContext, "indexerid");
            _mamRawDumpDir = GetArg(startupContext, "dumpraw");
            _mamRawDumpSeq = 0;

            if (string.IsNullOrWhiteSpace(proxyUrl))
            {
                System.Console.Error.WriteLine("[mambench] Refusing direct MAM traffic. Provide /proxy=<http-proxy-url>.");
                return 2;
            }

            if (bookIds.Count == 0)
            {
                System.Console.Error.WriteLine("[mambench] Provide a deliberate read-only cohort with /bookids=1,2,...");
                return 2;
            }

            var sourceAppData = GetArg(startupContext, "data") ?? Path.GetDirectoryName(Path.GetFullPath(dbPath)) ?? "/config";
            if (RejectPostgresForSqliteSnapshot(new StartupContext("/mambench", $"/data={sourceAppData}"), "mambench"))
            {
                return 2;
            }

            using var workspace = PrepareAuditWorkspace(startupContext, sourceAppData, live: false, sourceDatabasePath: dbPath);
            dbPath = Path.Combine(workspace.AppData, "chaptarr.db");
            using var host = BuildHost(workspace.StartupContext, blockOutboundHttp: true);
            using var scope = host.Services.CreateScope();
            var services = scope.ServiceProvider;
            var inputLoader = new ProductionSearchInputLoader(services);
            var searchService = services.GetRequiredService<ReleaseSearchService>();
            var prioritizer = services.GetRequiredService<IPrioritizeDownloadDecision>();
            var networkGuard = (OfflineReplayHttpDispatcher)services.GetRequiredService<IHttpDispatcher>();

            StoredIndexerRow? storedIndexer;
            MyAnonaMouseSettings? settings;
            IndexerDefinition? indexerDefinition;
            List<LibraryBookRow> sampleBooks;

            try
            {
                using var conn = OpenReadonlySqlite(dbPath);
                storedIndexer = indexerId.HasValue
                    ? conn.QuerySingleOrDefault<StoredIndexerRow>(
                        "SELECT Id, Name, Settings FROM Indexers WHERE Id = @id AND Implementation = 'MyAnonaMouse'",
                        new { id = indexerId.Value })
                    : conn.QueryFirstOrDefault<StoredIndexerRow>(
                        "SELECT Id, Name, Settings FROM Indexers WHERE Implementation = 'MyAnonaMouse' AND EnableInteractiveSearch = 1 ORDER BY Id");

                if (storedIndexer == null)
                {
                    System.Console.Error.WriteLine("[mambench] No enabled native MyAnonaMouse indexer was found.");
                    return 1;
                }

                settings = JsonConvert.DeserializeObject<MyAnonaMouseSettings>(storedIndexer.Settings);
                indexerDefinition = services.GetRequiredService<IIndexerFactory>().Get(storedIndexer.Id);
                sampleBooks = LoadBooksByIds(dbPath, bookIds);
            }
            catch (Exception ex)
            {
                System.Console.Error.WriteLine($"[mambench] Failed to load the read-only audit inputs: {ex.Message}");
                return 1;
            }

            if (settings == null || string.IsNullOrWhiteSpace(settings.MamId))
            {
                System.Console.Error.WriteLine("[mambench] The selected native MAM indexer has no mam_id.");
                return 1;
            }

            if (indexerDefinition == null)
            {
                System.Console.Error.WriteLine("[mambench] The selected native MAM indexer definition could not be loaded from the snapshot.");
                return 1;
            }

            if (sampleBooks.Count != bookIds.Count)
            {
                var loadedIds = sampleBooks.Select(row => row.BookId).ToHashSet();
                var missing = bookIds.Where(id => !loadedIds.Contains(id));
                System.Console.Error.WriteLine($"[mambench] Missing local book id(s): {string.Join(",", missing)}");
                return 1;
            }

            using var handler = new HttpClientHandler
            {
                Proxy = new System.Net.WebProxy(proxyUrl),
                UseProxy = true,
                UseCookies = false,
                AllowAutoRedirect = false
            };
            using var http = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(60)
            };

            var parser = new MyAnonaMouseJsonParser(settings);
            var lastRequestUtc = DateTime.MinValue;
            var incompleteBookCount = 0;

            System.Console.WriteLine($"[mambench] db snapshot='{dbPath}', indexer='{storedIndexer.Name}' ({storedIndexer.Id}), books={sampleBooks.Count}, interactive={interactiveSearch}");
            System.Console.WriteLine("[mambench] matching=production report evaluation + production prioritizer using snapshot data; transient live queue/cache/time state is not restored");
            System.Console.WriteLine("[mambench] transport=explicit-proxy, request spacing=2s (native indexer rate), downloads=disabled; decision replay blocks secondary outbound HTTP");

            // Raw-text probe: send one deliberately hand-written MAM query instead of the generated
            // per-book tiers, so upstream text operators can be evaluated against live data. The request is
            // still built by the production generator, so URL, payload shape, headers and cookies are identical.
            var rawTextProbe = GetArg(startupContext, "text");
            if (!string.IsNullOrWhiteSpace(rawTextProbe))
            {
                var probeRow = sampleBooks[0];
                var probeInput = inputLoader.Load(probeRow, userInvokedSearch: true, interactiveSearch: interactiveSearch);
                var probeCriteria = probeInput.Criteria;

                var probeGenerator = new MyAnonaMouseRequestGenerator
                {
                    Settings = settings,
                    Logger = Logger
                };

                var probeRequest = probeGenerator.GetJsonApiRequests(rawTextProbe, probeCriteria).First();

                System.Console.WriteLine($"[mambench] RAW TEXT PROBE - one request only, text='{rawTextProbe}'");
                System.Console.WriteLine($"[mambench] payload={System.Text.Encoding.UTF8.GetString(probeRequest.HttpRequest.ContentData ?? Array.Empty<byte>())}");

                List<ReleaseInfo> probeReleases;
                try
                {
                    probeReleases = FetchMamPage(http, parser, probeRequest);
                }
                catch (Exception ex)
                {
                    System.Console.Error.WriteLine($"[mambench] raw text probe failed: {ex.Message}");
                    return 1;
                }

                System.Console.WriteLine($"[mambench] returned {probeReleases.Count} release(s)");
                foreach (var release in probeReleases)
                {
                    var probeTorrent = release as TorrentInfo;
                    var probeSeries = release.SeriesInfo == null
                        ? string.Empty
                        : string.Join(" | ", release.SeriesInfo.Select(entry => $"{entry.Name}#{entry.Position}"));
                    System.Console.WriteLine($"    {release.Title}");
                    System.Console.WriteLine($"        author='{release.Author ?? string.Empty}' narrator='{release.Narrator ?? string.Empty}' filetype='{probeTorrent?.FileType ?? string.Empty}' seeders={TorrentInfo.GetSeeders(release) ?? 0} series=[{probeSeries}]");
                }

                return 0;
            }

            foreach (var row in sampleBooks)
            {
                var input = inputLoader.Load(row, userInvokedSearch: true, interactiveSearch: interactiveSearch);
                var criteria = input.Criteria;
                var book = input.TargetBook;
                var searchTitle = criteria.BookTitle;

                var generator = new MyAnonaMouseRequestGenerator
                {
                    Settings = settings,
                    Logger = Logger
                };
                var chain = generator.GetSearchRequests(criteria);
                var releases = new List<ReleaseInfo>();
                var tierCounts = new List<int>();
                string? failure = null;
                var truncatedTier = false;

                for (var tierIndex = 0; tierIndex < chain.Tiers && failure == null; tierIndex++)
                {
                    var tierReleases = new List<ReleaseInfo>();
                    foreach (var pageable in chain.GetTier(tierIndex))
                    {
                        foreach (var request in pageable)
                        {
                            var elapsed = DateTime.UtcNow - lastRequestUtc;
                            var requiredWait = TimeSpan.FromSeconds(2) - elapsed;
                            if (requiredWait > TimeSpan.Zero)
                            {
                                System.Threading.Tasks.Task.Delay(requiredWait).GetAwaiter().GetResult();
                            }

                            try
                            {
                                var page = FetchMamPage(http, parser, request);
                                lastRequestUtc = DateTime.UtcNow;
                                tierReleases.AddRange(page);

                                foreach (var release in page)
                                {
                                    release.IndexerId = storedIndexer.Id;
                                    release.Indexer = storedIndexer.Name;
                                    release.DownloadProtocol = indexerDefinition.Protocol;
                                    release.IndexerPriority = indexerDefinition.Priority;
                                }

                                if (page.Count < 100)
                                {
                                    break;
                                }

                                if (tierReleases.Count >= 1000)
                                {
                                    truncatedTier = true;
                                    break;
                                }
                            }
                            catch (Exception ex)
                            {
                                lastRequestUtc = DateTime.UtcNow;
                                failure = ex is TaskCanceledException ? "timeout" : ex.Message;
                                break;
                            }
                        }
                    }

                    tierCounts.Add(tierReleases.Count);
                    releases.AddRange(tierReleases);
                    if (tierReleases.Count > 0)
                    {
                        break;
                    }
                }

                var replay = RunProductionSearchDecision(
                    searchService,
                    prioritizer,
                    networkGuard,
                    releases,
                    criteria,
                    input.IsSynthetic,
                    hasCompleteSourceData: failure == null && !truncatedTier);
                var accepted = replay.Decisions.Where(decision => decision.Approved).ToList();
                var stoppedOnUnusableTier = failure == null && releases.Count > 0 && accepted.Count == 0 && tierCounts.Count < chain.Tiers;

                if (!replay.Complete)
                {
                    incompleteBookCount++;
                }

                System.Console.WriteLine();
                System.Console.WriteLine($"[mambench] {input.Author.Name} — {searchTitle} (book='{book.Title}', localBookId={book.Id}, media={book.MediaType})");
                System.Console.WriteLine($"  tiers: configured={chain.Tiers}, executed={tierCounts.Count}, raw=[{string.Join(",", tierCounts)}], {DescribeReplayOutcome(replay)}");
                if (failure != null)
                {
                    System.Console.WriteLine($"  request failure: {failure}");
                }
                if (truncatedTier)
                {
                    System.Console.WriteLine("  source results were truncated at the existing per-tier limit; production verdicts are withheld");
                }
                if (replay.BlockedExternalRequestCount > 0)
                {
                    System.Console.WriteLine($"  offline guard blocked {replay.BlockedExternalRequestCount} decision-time HTTP request(s): {string.Join(", ", replay.BlockedRequests.Distinct())}");
                }
                if (stoppedOnUnusableTier)
                {
                    System.Console.WriteLine("  warning: raw results stopped later native MAM tiers, but none passed the production decision pipeline");
                }

                var rank = 0;
                foreach (var decision in replay.Decisions)
                {
                    var release = decision.RemoteBook?.Release;
                    if (release == null)
                    {
                        continue;
                    }

                    var match = decision.RemoteBook?.SearchCriteriaMatch;
                    var leftovers = match?.MeaningfulLeftovers?.Count > 0 ? $" leftovers=[{string.Join(", ", match.MeaningfulLeftovers)}]" : string.Empty;
                    var torrent = release as TorrentInfo;
                    var reasons = string.Join("; ", decision.Rejections.Select(rejection => $"{rejection.Category}: {rejection.Reason}"));
                    if (!replay.Complete)
                    {
                        System.Console.WriteLine($"    INCOMPLETE - {release.Title}{leftovers} recordedRejections=[{reasons}]");
                        continue;
                    }

                    var status = decision.Approved ? "APPROVED" : "REJECTED";
                    if (decision.Approved)
                    {
                        rank++;
                        System.Console.WriteLine($"    #{rank} {status} - {release.Title} (seeders={TorrentInfo.GetSeeders(release) ?? 0}, author='{release.Author ?? ""}', narrator='{release.Narrator ?? ""}', graphicAudio={release.IsGraphicAudio}, fileType='{torrent?.FileType ?? ""}'){leftovers}");
                    }
                    else
                    {
                        System.Console.WriteLine($"    {status} - {release.Title}{leftovers} rejections=[{reasons}]");
                    }
                }
            }

            System.Console.WriteLine();
            System.Console.WriteLine($"[mambench] completed books={sampleBooks.Count - incompleteBookCount}, incomplete books={incompleteBookCount}, downloads=0");
            return incompleteBookCount == 0 ? 0 : 1;
        }

        private static string? _mamRawDumpDir;
        private static int _mamRawDumpSeq;

        private static List<ReleaseInfo> FetchMamPage(HttpClient http, MyAnonaMouseJsonParser parser, IndexerRequest request)
        {
            using var message = new HttpRequestMessage(request.HttpRequest.Method, request.Url.FullUri);
            if (request.HttpRequest.ContentData != null)
            {
                message.Content = new ByteArrayContent(request.HttpRequest.ContentData);
                if (!string.IsNullOrWhiteSpace(request.HttpRequest.Headers.ContentType))
                {
                    message.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(request.HttpRequest.Headers.ContentType);
                }
            }

            foreach (var header in request.HttpRequest.Headers)
            {
                if (header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                message.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (request.HttpRequest.Cookies.Count > 0)
            {
                message.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", request.HttpRequest.Cookies.Select(pair => $"{pair.Key}={pair.Value}")));
            }

            using var response = http.Send(message);
            var bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            var responseHeaders = new NzbDrone.Common.Http.HttpHeader();
            foreach (var header in response.Headers)
            {
                responseHeaders.Add(header.Key, string.Join(",", header.Value));
            }

            foreach (var header in response.Content.Headers)
            {
                responseHeaders.Add(header.Key, string.Join(",", header.Value));
            }

            if (!string.IsNullOrWhiteSpace(_mamRawDumpDir))
            {
                // Dev-only capture: the request we sent and the body we got back, verbatim.
                var seq = (++_mamRawDumpSeq).ToString("D3");
                System.IO.Directory.CreateDirectory(_mamRawDumpDir);
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(_mamRawDumpDir, $"{seq}-request.txt"),
                    $"{request.HttpRequest.Method} {request.Url.FullUri}\n" +
                    string.Join("\n", request.HttpRequest.Headers
                        .Where(h => !h.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
                        .Select(h => $"{h.Key}: {h.Value}")) +
                    $"\nCookie: [{string.Join(", ", request.HttpRequest.Cookies.Select(c => c.Key))} redacted]\n\n" +
                    (request.HttpRequest.ContentData != null
                        ? System.Text.Encoding.UTF8.GetString(request.HttpRequest.ContentData)
                        : "<no body>"));
                System.IO.File.WriteAllBytes(
                    System.IO.Path.Combine(_mamRawDumpDir, $"{seq}-response-{(int)response.StatusCode}.json"),
                    bytes);
                System.Console.WriteLine($"[mambench] dumped raw request/response pair {seq} ({bytes.Length} bytes) to {_mamRawDumpDir}");
            }

            var nativeResponse = new NzbDrone.Common.Http.HttpResponse(request.HttpRequest, responseHeaders, bytes, response.StatusCode, response.Version);
            return parser.ParseResponse(new IndexerResponse(request, nativeResponse)).ToList();
        }

        private static List<LibraryBookRow> LoadRandomBooks(string dbPath, int sample)
        {
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();

            const string sql = @"
SELECT
  b.Id as BookId,
  b.Title as BookTitle,
  b.Subtitle as BookSubtitle,
  b.OriginalTitle as OriginalTitle,
  (SELECT e.Title FROM Editions e WHERE e.BookId = b.Id AND e.Monitored = 1 ORDER BY e.Id LIMIT 1) as EditionTitle,
  b.SeriesName as SeriesName,
  b.SeriesPosition as SeriesPosition,
  b.PublicationYear as PublicationYear,
  b.MediaType as MediaType,
  b.AnyEditionOk as AnyEditionOk,
  b.GoodreadsBookId as GoodreadsBookId,
  b.GoodreadsWorkId as GoodreadsWorkId,
  b.HardcoverBookId as HardcoverBookId,
  b.OpenLibraryEditionId as OpenLibraryEditionId,
  b.OpenLibraryWorkId as OpenLibraryWorkId,
  b.GoogleBooksId as GoogleBooksId,
  b.ASIN as Asin,
  b.AudibleASIN as AudibleAsin,
  a.Id as AuthorId,
  a.Name as AuthorName,
  a.MetadataProfileId as MetadataProfileId,
  a.AudiobookMetadataProfileId as AudiobookMetadataProfileId,
  a.EbookMetadataProfileId as EbookMetadataProfileId
FROM Books b
JOIN Authors a ON a.Id = b.AuthorId
WHERE b.Title IS NOT NULL AND trim(b.Title) != ''
ORDER BY RANDOM()
LIMIT @sample;
";

            return conn.Query<LibraryBookRow>(sql, new { sample }).ToList();
        }

        private static List<LibraryBookRow> LoadBooksByIds(string dbPath, IReadOnlyCollection<int> bookIds)
        {
            using var conn = OpenReadonlySqlite(dbPath);

            const string sql = @"
SELECT
  b.Id as BookId,
  b.Title as BookTitle,
  b.Subtitle as BookSubtitle,
  b.OriginalTitle as OriginalTitle,
  (SELECT e.Title FROM Editions e WHERE e.BookId = b.Id AND e.Monitored = 1 ORDER BY e.Id LIMIT 1) as EditionTitle,
  b.SeriesName as SeriesName,
  b.SeriesPosition as SeriesPosition,
  b.PublicationYear as PublicationYear,
  b.MediaType as MediaType,
  b.AnyEditionOk as AnyEditionOk,
  b.GoodreadsBookId as GoodreadsBookId,
  b.GoodreadsWorkId as GoodreadsWorkId,
  b.HardcoverBookId as HardcoverBookId,
  b.OpenLibraryEditionId as OpenLibraryEditionId,
  b.OpenLibraryWorkId as OpenLibraryWorkId,
  b.GoogleBooksId as GoogleBooksId,
  b.ASIN as Asin,
  b.AudibleASIN as AudibleAsin,
  a.Id as AuthorId,
  a.Name as AuthorName,
  a.MetadataProfileId as MetadataProfileId,
  a.AudiobookMetadataProfileId as AudiobookMetadataProfileId,
  a.EbookMetadataProfileId as EbookMetadataProfileId
FROM Books b
JOIN Authors a ON a.Id = b.AuthorId
WHERE b.Id IN @bookIds
  AND b.Title IS NOT NULL
  AND trim(b.Title) != '';
";

            var rows = conn.Query<LibraryBookRow>(sql, new { bookIds = bookIds.ToArray() })
                .ToDictionary(row => row.BookId);

            return bookIds.Where(rows.ContainsKey).Select(id => rows[id]).ToList();
        }

        private static Book BuildBook(LibraryBookRow row, Author author)
        {
            return new Book
            {
                Id = row.BookId,
                Title = row.BookTitle,
                Subtitle = row.BookSubtitle,
                OriginalTitle = row.OriginalTitle,
                SeriesName = row.SeriesName,
                SeriesPosition = row.SeriesPosition,
                PublicationYear = row.PublicationYear,
                MediaType = row.MediaType,
                AnyEditionOk = row.AnyEditionOk,
                GoodreadsBookId = row.GoodreadsBookId,
                GoodreadsWorkId = row.GoodreadsWorkId,
                HardcoverBookId = row.HardcoverBookId,
                OpenLibraryEditionId = row.OpenLibraryEditionId,
                OpenLibraryWorkId = row.OpenLibraryWorkId,
                GoogleBooksId = row.GoogleBooksId,
                ASIN = row.Asin,
                AudibleASIN = row.AudibleAsin,
                Author = author,
                Editions = new List<Edition>
                {
                    new Edition
                    {
                        Title = row.EditionTitle ?? row.BookTitle,
                        Monitored = true
                    }
                }
            };
        }

        private static List<LibraryBookRow> LoadWorstCaseBooks(string dbPath, int sample)
        {
            // Pull a larger random window then pick the riskiest titles locally.
            const int candidatePool = 1500;

            var candidates = LoadRandomBooks(dbPath, candidatePool);
            if (candidates.Count <= sample)
            {
                return candidates;
            }

            return candidates
                .Select(r => new { Row = r, Score = WorstCaseScore(r) })
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Row.BookTitle.Length)
                .Take(sample)
                .Select(x => x.Row)
                .ToList();
        }

        private static int WorstCaseScore(LibraryBookRow row)
        {
            if (row == null || string.IsNullOrWhiteSpace(row.BookTitle))
            {
                return 0;
            }

            var title = row.BookTitle;
            var lower = title.ToLowerInvariant();

            var score = 0;

            // Very short / generic titles
            var words = lower.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length <= 2)
            {
                score += 6;
            }
            else if (words.Length <= 4)
            {
                score += 3;
            }

            // Roman numerals and digits (series markers)
            if (System.Text.RegularExpressions.Regex.IsMatch(title, @"\b[IVX]{2,6}\b"))
            {
                score += 5;
            }

            if (System.Text.RegularExpressions.Regex.IsMatch(title, @"\b\d{1,4}\b"))
            {
                score += 4;
            }

            // Common noisy genres/labels that often appear in titles/releases
            if (lower.Contains("litrpg") || lower.Contains("lit rpg") || lower.Contains("gamelit"))
            {
                score += 4;
            }

            if (lower.Contains("book ") || lower.Contains("volume") || lower.Contains("vol "))
            {
                score += 2;
            }

            // Titles with punctuation/subtitles are more likely to be inconsistent across releases.
            if (title.Contains(':') || title.Contains('-') || title.Contains('—') || title.Contains('–'))
            {
                score += 1;
            }

            // De-prioritize large box sets/collections: they're noisy but not great for validating single-book matching.
            if (lower.Contains("boxed set") || lower.Contains("box set") || lower.Contains("collection") || lower.Contains("book set") || lower.Contains("books set") || lower.Contains("series"))
            {
                score -= 4;
            }

            if (title.Length > 80)
            {
                score -= 2;
            }

            return score;
        }

        private static List<LibraryBookRow> ParseCasesArg(string casesArg)
        {
            var results = new List<LibraryBookRow>();

            if (string.IsNullOrWhiteSpace(casesArg))
            {
                return results;
            }

            foreach (var raw in casesArg.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var entry = raw.Trim();
                if (entry.Length == 0)
                {
                    continue;
                }

                var parts = entry.Split('|', StringSplitOptions.None);
                if (parts.Length < 2)
                {
                    continue;
                }

                var author = parts[0].Trim();
                var title = parts[1].Trim();
                var seriesName = (string?)null;
                var seriesPosition = (string?)null;

                if (author.Length == 0 || title.Length == 0)
                {
                    continue;
                }

                if (parts.Length == 3)
                {
                    var third = parts[2].Trim();
                    if (!string.IsNullOrWhiteSpace(third))
                    {
                        if (LooksLikeSeriesPosition(third))
                        {
                            seriesPosition = third;
                        }
                        else
                        {
                            seriesName = third;
                        }
                    }
                }
                else if (parts.Length >= 4)
                {
                    seriesName = parts[2].Trim();
                    seriesPosition = parts[3].Trim();

                    if (string.IsNullOrWhiteSpace(seriesName))
                    {
                        seriesName = null;
                    }

                    if (string.IsNullOrWhiteSpace(seriesPosition))
                    {
                        seriesPosition = null;
                    }
                }

                results.Add(new LibraryBookRow
                {
                    AuthorId = 0,
                    AuthorName = author,
                    BookId = 0,
                    BookTitle = title,
                    SeriesName = seriesName,
                    SeriesPosition = seriesPosition
                });
            }

            return results;
        }

        private static bool LooksLikeSeriesPosition(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out _);
        }

        private static ProwlarrSearchFetchResult FetchProwlarrSearch(HttpClient http, JsonSerializerOptions options, string query, IReadOnlyCollection<int> indexerIds, BookMediaType mediaType)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return new ProwlarrSearchFetchResult();
            }

            // Restrict server-side to book-related categories (avoid TV/movies/etc).
            // Prowlarr expects repeated 'categories' keys, not a comma-separated list.
            var category = mediaType == BookMediaType.Ebook ? 7000 : 3030;
            var url = $"/api/v1/search?query={Uri.EscapeDataString(query)}&type=book&categories={category}";
            if (indexerIds?.Count > 0)
            {
                url += string.Concat(indexerIds.Select(id => $"&indexerIds={id}"));
            }

            try
            {
                using var resp = http.GetAsync(url).GetAwaiter().GetResult();

                if (!resp.IsSuccessStatusCode)
                {
                    var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    System.Console.Error.WriteLine($"[prowlarrbench] search failed ({(int)resp.StatusCode}) for query '{query}': {body}");
                    return new ProwlarrSearchFetchResult { Error = $"HTTP {(int)resp.StatusCode}" };
                }

                using var stream = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
                var results = System.Text.Json.JsonSerializer.Deserialize(stream, typeof(List<ProwlarrSearchResult>), options) as List<ProwlarrSearchResult>;
                return new ProwlarrSearchFetchResult { Results = results ?? new List<ProwlarrSearchResult>() };
            }
            catch (TaskCanceledException)
            {
                System.Console.Error.WriteLine($"[prowlarrbench] search timed out for query '{query}'");
                return new ProwlarrSearchFetchResult { Error = "timeout" };
            }
            catch (HttpRequestException ex)
            {
                System.Console.Error.WriteLine($"[prowlarrbench] search failed for query '{query}': {ex.Message}");
                return new ProwlarrSearchFetchResult { Error = "request failure" };
            }
        }

        private static bool IsAllowedBookCategory(ProwlarrSearchResult result, BookMediaType mediaType)
        {
            if (result?.Categories == null || result.Categories.Count == 0)
            {
                return false;
            }

            foreach (var c in result.Categories)
            {
                if (c == null)
                {
                    continue;
                }

                var id = c.Id;
                var matchesMediaType = mediaType == BookMediaType.Ebook
                    ? id >= 7000 && id < 8000
                    : id >= 3000 && id < 4000;

                if (matchesMediaType)
                {
                    return true;
                }
            }

            return false;
        }

        private static IReadOnlyList<StagedRow> LoadStagedRows(IServiceProvider services, string prefix, int limit, bool requireTags)
        {
            var staging = services.GetRequiredService<IStagingDbContext>();
            staging.InitializeDatabase();

            var normalizedPrefix = prefix.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var prefixForward = normalizedPrefix + "/";
            var prefixBack = normalizedPrefix + "\\";

            using var conn = staging.OpenConnection();
            var sql = @"
                SELECT path as Path,
                       size_bytes as SizeBytes,
                       mtime_ns as MtimeNs,
                       tags_json as TagsJson,
                       duration_seconds as DurationSeconds,
                       status as Status
                FROM ingest_queue
                WHERE (
                        path = @prefix
                        OR substr(path, 1, length(@prefixForward)) = @prefixForward
                        OR substr(path, 1, length(@prefixBack)) = @prefixBack
                      )
                  AND (@requireTags = 0 OR (tags_json IS NOT NULL AND trim(tags_json) != '{}' AND trim(tags_json) != ''))
                ORDER BY id
                LIMIT @limit;
            ";

            var rows = conn.Query<StagedRow>(sql, new
            {
                prefix = normalizedPrefix,
                prefixForward,
                prefixBack,
                limit,
                requireTags = requireTags ? 1 : 0
            }).ToList();

            System.Console.WriteLine($"[matchbench] loaded staged rows: {rows.Count}");
            return rows;
        }

        private static List<DiscoveredFileWithMetadata> BuildDiscoveredFiles(IReadOnlyList<StagedRow> rows, bool requireTags)
        {
            var discovered = new List<DiscoveredFileWithMetadata>();
            foreach (var row in rows ?? Array.Empty<StagedRow>())
            {
                if (row == null || string.IsNullOrWhiteSpace(row.Path))
                {
                    continue;
                }

                var tags = SafeDeserializeTags(row.TagsJson);
                if (requireTags && (tags == null || tags.Count == 0))
                {
                    continue;
                }

                var ext = Path.GetExtension(row.Path) ?? string.Empty;
                var detectedQuality = MediaFileExtensions.GetQualityForExtension(ext);
                var qualityModel = new QualityModel(detectedQuality);

                discovered.Add(new DiscoveredFileWithMetadata
                {
                    Path = row.Path,
                    Size = row.SizeBytes,
                    Modified = DateTime.UtcNow,
                    AllTags = tags ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase),
                    Quality = qualityModel,
                    DurationSeconds = row.DurationSeconds
                });
            }

            System.Console.WriteLine($"[matchbench] built discovered files: {discovered.Count}");
            return discovered;
        }

        private static Dictionary<string, List<string>> SafeDeserializeTags(string? tagsJson)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(tagsJson))
                {
                    return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                }

                var trimmed = tagsJson.Trim();
                if (trimmed == "{}")
                {
                    return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                }

                var raw = JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(trimmed);
                if (raw == null || raw.Count == 0)
                {
                    return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                }

                var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in raw)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key))
                    {
                        continue;
                    }

                    tags[kv.Key] = kv.Value ?? new List<string>();
                }

                return tags;
            }
            catch
            {
                return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static IHost BuildHost(StartupContext startupContext, bool blockOutboundHttp = false, bool recordDownloads = false)
        {
            var config = BuildConfiguration(startupContext);

            return new HostBuilder()
                .UseServiceProviderFactory(new DryIocServiceProviderFactory(new Container(rules => rules.WithNzbDroneRules())))
                .ConfigureContainer<IContainer>(c =>
                {
                    c.AutoAddServices(new List<string>
                        {
                            "Chaptarr.Host",
                            "Chaptarr.Core",
                            "Chaptarr.SignalR",
                            "Chaptarr.Api.V1",
                            "Chaptarr.Http"
                        })
                        .RegisterHttpClients()
                        .AddIndexerProxyProvider()
                        .AddNzbDroneLogger()
                        .AddDatabase()
                        .AddFuzzyMatchingServices()
                        .AddImportServices()
                        .AddNamingPatternServices()
                        .AddStartupContext(startupContext);

                    c.Register<IBroadcastSignalRMessage, NoOpSignalRBroadcaster>(Reuse.Singleton);
                    if (recordDownloads)
                    {
                        c.Register<IDownloadService, ReplayDownloadService>(Reuse.Singleton, ifAlreadyRegistered: IfAlreadyRegistered.Replace);
                    }
                    if (blockOutboundHttp)
                    {
                        c.Register<IHttpDispatcher, OfflineReplayHttpDispatcher>(Reuse.Singleton, ifAlreadyRegistered: IfAlreadyRegistered.Replace);
                        c.Register<IIndexerHttpClientFactory, OfflineReplayIndexerHttpClient>(Reuse.Singleton, ifAlreadyRegistered: IfAlreadyRegistered.Replace);
                    }
                })
                .ConfigureServices(services =>
                {
                    services.AddLogging(b =>
                    {
                        b.ClearProviders();
                        b.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Trace);
                        b.AddFilter("Microsoft", Microsoft.Extensions.Logging.LogLevel.Warning);
                        b.AddNLog();
                    });
                    services.AddSingleton<IBroadcastSignalRMessage, NoOpSignalRBroadcaster>();
                    services.Configure<PostgresOptions>(config.GetSection("Chaptarr:Postgres"));
                    services.PostConfigure<PostgresOptions>(o =>
                    {
                        if (string.IsNullOrWhiteSpace(o.Host))
                        {
                            config.GetSection("Readarr:Postgres").Bind(o);
                            config.GetSection("Audioarr:Postgres").Bind(o);
                        }
                    });
                    services.Configure<AppOptions>(config.GetSection("Chaptarr:App"));
                    services.Configure<AuthOptions>(config.GetSection("Chaptarr:Auth"));
                    services.Configure<ServerOptions>(config.GetSection("Chaptarr:Server"));
                    services.Configure<LogOptions>(config.GetSection("Chaptarr:Log"));
                    services.Configure<UpdateOptions>(config.GetSection("Chaptarr:Update"));
                })
                .Build();
        }

        private static IConfiguration BuildConfiguration(StartupContext startupContext)
        {
            try
            {
                var appFolder = new AppFolderInfo(startupContext);
                var configPath = appFolder.GetConfigPath();

                return new ConfigurationBuilder()
                    .AddXmlFile(configPath, optional: true, reloadOnChange: false)
                    .AddInMemoryCollection(new List<KeyValuePair<string, string?>> { new("dataProtectionFolder", appFolder.GetDataProtectionPath()) })
                    .AddEnvironmentVariables()
                    .Build();
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "[matchbench] Failed to load config.xml; falling back to env-only config");
                return new ConfigurationBuilder().AddEnvironmentVariables().Build();
            }
        }

        private static bool RejectPostgresForSqliteSnapshot(StartupContext startupContext, string mode)
        {
            var config = BuildConfiguration(startupContext);
            var options = new PostgresOptions();
            config.GetSection("Chaptarr:Postgres").Bind(options);
            if (string.IsNullOrWhiteSpace(options.Host))
            {
                config.GetSection("Readarr:Postgres").Bind(options);
                config.GetSection("Audioarr:Postgres").Bind(options);
            }

            if (!string.IsNullOrWhiteSpace(options.Host) || !string.IsNullOrWhiteSpace(config["PostgresHost"]))
            {
                System.Console.Error.WriteLine($"[{mode}] Refusing SQLite snapshot replay while PostgreSQL is configured; this mode only snapshots SQLite and cannot isolate PostgreSQL state.");
                return true;
            }

            return false;
        }

        private static string? GetArg(StartupContext ctx, string key)
        {
            if (ctx?.Args == null || string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            return ctx.Args.TryGetValue(key.Trim().ToLowerInvariant(), out var value) ? value : null;
        }

        private static int GetIntArg(StartupContext ctx, string key, int defaultValue)
        {
            var raw = GetArg(ctx, key);
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : defaultValue;
        }

        private static int? GetNullableIntArg(StartupContext ctx, string key)
        {
            var raw = GetArg(ctx, key);
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : (int?)null;
        }

        private static bool GetBoolArg(StartupContext ctx, string key, bool defaultValue)
        {
            var raw = GetArg(ctx, key);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return defaultValue;
            }

            if (raw.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                raw.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                raw.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                raw.Equals("y", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (raw.Equals("0", StringComparison.OrdinalIgnoreCase) ||
                raw.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                raw.Equals("no", StringComparison.OrdinalIgnoreCase) ||
                raw.Equals("n", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return defaultValue;
        }

        private static bool TryParseBool(string raw, out bool value)
        {
            value = false;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            if (raw.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                raw.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                raw.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                raw.Equals("y", StringComparison.OrdinalIgnoreCase))
            {
                value = true;
                return true;
            }

            if (raw.Equals("0", StringComparison.OrdinalIgnoreCase) ||
                raw.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                raw.Equals("no", StringComparison.OrdinalIgnoreCase) ||
                raw.Equals("n", StringComparison.OrdinalIgnoreCase))
            {
                value = false;
                return true;
            }

            return false;
        }

        private static double Percentile(IReadOnlyList<long> sortedMs, double p)
        {
            if (sortedMs == null || sortedMs.Count == 0)
            {
                return 0;
            }

            if (p <= 0)
            {
                return sortedMs[0];
            }

            if (p >= 1)
            {
                return sortedMs[^1];
            }

            var idx = (sortedMs.Count - 1) * p;
            var lo = (int)Math.Floor(idx);
            var hi = (int)Math.Ceiling(idx);
            if (lo == hi)
            {
                return sortedMs[lo];
            }

            var w = idx - lo;
            return sortedMs[lo] * (1.0 - w) + sortedMs[hi] * w;
        }

        private static void PrintUsage()
        {
            System.Console.WriteLine("Bench tools:");
            System.Console.WriteLine();
            System.Console.WriteLine("1) File matching replay (staging.db):");
            System.Console.WriteLine("  Required: /matchbench /prefix=<path>");
            System.Console.WriteLine();
            System.Console.WriteLine("  Optional:");
            System.Console.WriteLine("  /data=<appdata>          (where staging.db + chaptarr.db live)");
            System.Console.WriteLine("  /limit=250               (max staged rows)");
            System.Console.WriteLine("  /repeat=1                (repeat runs; good for warm-cache)");
            System.Console.WriteLine("  /authorid=<id>           (restrictToAuthorId)");
            System.Console.WriteLine("  /perfile=true|false      (PerFileMatching mode; default false)");
            System.Console.WriteLine("  /allowv5=true|false      (default false)");
            System.Console.WriteLine("  /allowimport=true|false  (default false)");
            System.Console.WriteLine("  /defer=true|false        (default false)");
            System.Console.WriteLine("  /unscopedfallback=true|false (default false)");
            System.Console.WriteLine("  /requiretags=true|false  (default true)");
            System.Console.WriteLine();
            System.Console.WriteLine("Example:");
            System.Console.WriteLine("  dotnet run --project src/NzbDrone.MatchBench/Chaptarr.MatchBench.csproj -- /matchbench /data=/config /prefix=/audiobooks/SomeAuthor /limit=300 /repeat=3 /authorid=12");
            System.Console.WriteLine();
            System.Console.WriteLine("2) Library matching audit (real matcher, snapshot by default):");
            System.Console.WriteLine("  Required: /libraryaudit");
            System.Console.WriteLine("  Optional: /data=<appdata>, /prefix=<path>, /source=bookfiles|staging|disk, /limit=250");
            System.Console.WriteLine("  Optional: /flow=scan-local|scan-v5|scan-scoped-rematch|downloaded|manual-download|author-ready|direct-default|all");
            System.Console.WriteLine("  Optional: /strictness=current|strict|balanced|loose|all, /pathfallback=current|true|false|all");
            System.Console.WriteLine("  Optional: /authorid=<id>, /targetbookids=1,2, /mappedonly=true, /unmappedonly=true, /requiretags=true|false, /includemissing=true|false");
            System.Console.WriteLine("  Answer keys: bulk audits exclude user-selected mappings by default; /includeuserselected=true includes them. Exact /bookfileid or /bookid audits include them by default.");
            System.Console.WriteLine("  Exact-row audit: /source=bookfiles /bookfileid=<local BookFiles.Id> or /bookid=<local Books.Id>; /ftstop=25 controls Markdown/console rows (JSON keeps the full captured trace).");
            System.Console.WriteLine("  Optional: /trace=true|false, /tracelimit=5000, /out=<file.json>, /live=true, /allowmutations=true");
            System.Console.WriteLine("  Safety: /live=true refuses author-ready/direct-default unless /allowmutations=true because those production flows can import or refresh metadata.");
            System.Console.WriteLine("  Sources: bookfiles replays exactly stored BookFiles tags/durations; staging replays ingest_queue; disk re-extracts tags from real files.");
            System.Console.WriteLine();
            System.Console.WriteLine("Example:");
            System.Console.WriteLine("  dotnet run --project src/NzbDrone.MatchBench/Chaptarr.MatchBench.csproj -- /libraryaudit /data=/config /source=bookfiles /prefix=/audiobooks/Example\\ Author /limit=50 /flow=all /strictness=all /pathfallback=all");
            System.Console.WriteLine("  dotnet run --project src/NzbDrone.MatchBench/Chaptarr.MatchBench.csproj -- /libraryaudit /data=/config /source=bookfiles /bookfileid=3081 /includemissing=true /flow=scan-local /ftstop=50");
            System.Console.WriteLine();
            System.Console.WriteLine("3) Prowlarr recall audit (no matching verdicts or grabs):");
            System.Console.WriteLine("  Required: /prowlarrbench and PROWLARR_API_KEY set (or /apikey=...)");
            System.Console.WriteLine("  Optional: /prowlarr=<url> (or PROWLARR_URL), /db=<path>, /sample=10, /max=60, /querymode=both|authorTitle|titleOnly, /indexerids=2,14, /worstcase=true|false, /bookids=1,2, /author=<name> /title=<title>, /cases='Author|Title[|SeriesPos];Author|Title|SeriesName|SeriesPos;...'");
            System.Console.WriteLine();
            System.Console.WriteLine("Example:");
            System.Console.WriteLine("  PROWLARR_URL=http://192.0.2.10:9696 PROWLARR_API_KEY=*** dotnet run --project src/NzbDrone.MatchBench/Chaptarr.MatchBench.csproj -- /prowlarrbench /db=/config/chaptarr.db /sample=10 /querymode=both");
            System.Console.WriteLine();
            System.Console.WriteLine("4) Native MyAnonaMouse request/parser + production decision audit (search-only):");
            System.Console.WriteLine("  Required: /mambench /db=<chaptarr.db> /bookids=1,2 /proxy=<http-proxy-url>");
            System.Console.WriteLine("  Optional: /indexerid=<native-MAM-indexer-id>, /interactive=true|false");
            System.Console.WriteLine("  Safety: refuses direct MAM traffic, uses native 2-second request spacing, and never calls a download URL.");
            System.Console.WriteLine();
            System.Console.WriteLine("5) Offline production release-decision replay:");
            System.Console.WriteLine("  Required: /releasereplay /data=<appdata> /db=<chaptarr.db> /releases=<reports.json>");
            System.Console.WriteLine("  Mode: /bookids=<local-book-ids> for targeted search (default), or /rss=true for one unscoped automatic RSS batch.");
            System.Console.WriteLine("  Input: JSON array of serialized ReleaseInfo/TorrentInfo reports, including downloadProtocol and source metadata; optional /interactive=true|false.");
            System.Console.WriteLine("  Safety: SQLite snapshot only; no outbound decision-time HTTP or grabs. Missing source fields or blocked enrichment make the replay incomplete.");
        }
    }
}
