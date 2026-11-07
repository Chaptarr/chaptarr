using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Queue;
using NzbDrone.Core.SystemStats;

namespace NzbDrone.Core.Notifications.MqttGateway
{
    public sealed record MqttGatewaySnapshot
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; } = 1;

        [JsonPropertyName("generatedAt")]
        public DateTime GeneratedAt { get; init; }

        [JsonPropertyName("books_total")]
        public int BooksTotal { get; init; }

        [JsonPropertyName("audiobooks_total")]
        public int AudiobooksTotal { get; init; }

        [JsonPropertyName("missing_books")]
        public int MissingBooks { get; init; }

        [JsonPropertyName("wanted_books")]
        public int WantedBooks { get; init; }

        [JsonPropertyName("queue")]
        public int Queue { get; init; }

        [JsonPropertyName("upcoming_releases")]
        public int UpcomingReleases { get; init; }

        [JsonPropertyName("library_size")]
        public long LibrarySize { get; init; }

        [JsonPropertyName("last_import")]
        public DateTime? LastImport { get; init; }

        [JsonPropertyName("health")]
        public string Health { get; init; }

        public string StateFingerprint()
        {
            return string.Join("|", BooksTotal, AudiobooksTotal, MissingBooks, WantedBooks, Queue,
                UpcomingReleases, LibrarySize, LastImport?.Ticks ?? 0, Health ?? "ok");
        }
    }

    public sealed class MqttGatewaySnapshotProvider
    {
        private const int CutoffPageSize = 500;

        private readonly IBookService _bookService;
        private readonly IBookCutoffService _bookCutoffService;
        private readonly ISystemStatisticsService _systemStatisticsService;
        private readonly IQueueService _queueService;
        private readonly IHealthCheckService _healthCheckService;
        private readonly IMediaFileRepository _mediaFileRepository;

        public MqttGatewaySnapshotProvider(IBookService bookService,
                                           IBookCutoffService bookCutoffService,
                                           ISystemStatisticsService systemStatisticsService,
                                           IQueueService queueService,
                                           IHealthCheckService healthCheckService,
                                           IMediaFileRepository mediaFileRepository)
        {
            _bookService = bookService;
            _bookCutoffService = bookCutoffService;
            _systemStatisticsService = systemStatisticsService;
            _queueService = queueService;
            _healthCheckService = healthCheckService;
            _mediaFileRepository = mediaFileRepository;
        }

        public MqttGatewaySnapshot Create(MqttGatewaySettings settings)
        {
            var now = DateTime.UtcNow;
            var statistics = _systemStatisticsService.GetSystemStatistics("all");
            var audiobookStatistics = _systemStatisticsService.GetSystemStatistics("audiobook");
            var missingBookIds = new HashSet<int>(_bookService.GetBookIds(wanted: true));
            var wantedBookIds = new HashSet<int>(missingBookIds);

            foreach (var book in GetCutoffUnmetBooks())
            {
                if (book.Id > 0 && IsMonitored(book) && book.ReleaseDate.HasValue && book.ReleaseDate.Value <= now)
                {
                    wantedBookIds.Add(book.Id);
                }
            }

            var upcomingReleases = _bookService.BooksBetweenDates(now, now.AddDays(settings.UpcomingReleaseWindowDays), false).Count;
            var lastImport = _mediaFileRepository.GetLatestImportDate();
            if (lastImport.HasValue && lastImport.Value.Kind == DateTimeKind.Unspecified)
            {
                lastImport = DateTime.SpecifyKind(lastImport.Value, DateTimeKind.Utc);
            }
            else if (lastImport.HasValue)
            {
                lastImport = lastImport.Value.ToUniversalTime();
            }

            return new MqttGatewaySnapshot
            {
                GeneratedAt = now,
                BooksTotal = statistics.TotalBooks,
                AudiobooksTotal = audiobookStatistics.AudiobookCount,
                MissingBooks = missingBookIds.Count,
                WantedBooks = wantedBookIds.Count,
                Queue = _queueService.GetQueue().Count,
                UpcomingReleases = upcomingReleases,
                LibrarySize = statistics.TotalFileSize,
                LastImport = lastImport,
                Health = GetHealthLevel()
            };
        }

        private IEnumerable<Book> GetCutoffUnmetBooks()
        {
            var page = 1;
            var totalRecords = 0;

            do
            {
                var result = _bookCutoffService.BooksWhereCutoffUnmet(new PagingSpec<Book>
                {
                    Page = page,
                    PageSize = CutoffPageSize,
                    SortKey = "id",
                    SortDirection = SortDirection.Ascending
                });

                totalRecords = result.TotalRecords;
                foreach (var book in result.Records ?? new List<Book>())
                {
                    yield return book;
                }

                page++;
            }
            while ((page - 1) * CutoffPageSize < totalRecords);
        }

        private static bool IsMonitored(Book book)
        {
            return book.MediaType == BookMediaType.Audiobook ? book.AudiobookMonitored : book.EbookMonitored;
        }

        private string GetHealthLevel()
        {
            var results = _healthCheckService.Results();
            if (results.Any(result => result.Type == HealthCheckResult.Error))
            {
                return "error";
            }

            return results.Any(result => result.Type == HealthCheckResult.Warning || result.Type == HealthCheckResult.Notice)
                ? "warning"
                : "ok";
        }
    }
}
