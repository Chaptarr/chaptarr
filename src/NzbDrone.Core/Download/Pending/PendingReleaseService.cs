using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Crypto;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download.Aggregation;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Jobs;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Delay;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.Download.Pending
{
    public interface IPendingReleaseService
    {
        void Add(DownloadDecision decision, PendingReleaseReason reason);
        void AddMany(List<Tuple<DownloadDecision, PendingReleaseReason>> decisions);
        List<ReleaseInfo> GetPending();
        List<ReleaseInfo> GetPending(out IReadOnlyDictionary<(int AuthorId, int BookId), DateTime> oldestPublishDates);
        List<RemoteBook> GetPendingRemoteBooks(int authorId);
        List<Queue.Queue> GetPendingQueue();
        Queue.Queue FindPendingQueueItem(int queueId);
        void RemovePendingQueueItems(IEnumerable<Queue.Queue> queueItems);
        RemoteBook OldestPendingRelease(int authorId, int[] bookIds);
    }

    public class PendingReleaseService : IPendingReleaseService,
                                         IHandle<AuthorDeletedEvent>,
                                         IHandle<BookDeletedEvent>,
                                         IHandle<BookGrabbedEvent>,
                                         IHandle<RssSyncCompleteEvent>
    {
        private readonly IIndexerStatusService _indexerStatusService;
        private readonly IPendingReleaseRepository _repository;
        private readonly IAuthorService _authorService;
        private readonly IParsingService _parsingService;
        private readonly IBookService _bookService;
        private readonly IDelayProfileService _delayProfileService;
        private readonly ITaskManager _taskManager;
        private readonly IConfigService _configService;
        private readonly ICustomFormatCalculationService _formatCalculator;
        private readonly IRemoteBookAggregationService _aggregationService;
        private readonly IDownloadClientFactory _downloadClientFactory;
        private readonly IIndexerFactory _indexerFactory;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;

        public PendingReleaseService(IIndexerStatusService indexerStatusService,
                                    IPendingReleaseRepository repository,
                                    IAuthorService authorService,
                                    IParsingService parsingService,
                                    IDelayProfileService delayProfileService,
                                    ITaskManager taskManager,
                                    IConfigService configService,
                                    ICustomFormatCalculationService formatCalculator,
                                    IRemoteBookAggregationService aggregationService,
                                    IDownloadClientFactory downloadClientFactory,
                                    IIndexerFactory indexerFactory,
                                    IEventAggregator eventAggregator,
                                    Logger logger,
                                    IBookService bookService)
        {
            _indexerStatusService = indexerStatusService;
            _repository = repository;
            _authorService = authorService;
            _parsingService = parsingService;
            _bookService = bookService;
            _delayProfileService = delayProfileService;
            _taskManager = taskManager;
            _configService = configService;
            _formatCalculator = formatCalculator;
            _aggregationService = aggregationService;
            _downloadClientFactory = downloadClientFactory;
            _indexerFactory = indexerFactory;
            _eventAggregator = eventAggregator;
            _logger = logger;
        }

        public void Add(DownloadDecision decision, PendingReleaseReason reason)
        {
            AddMany(new List<Tuple<DownloadDecision, PendingReleaseReason>> { Tuple.Create(decision, reason) });
        }

        public void AddMany(List<Tuple<DownloadDecision, PendingReleaseReason>> decisions)
        {
            foreach (var authorDecisions in decisions.GroupBy(v => v.Item1.RemoteBook.Author.Id))
            {
                var author = authorDecisions.First().Item1.RemoteBook.Author;
                var alreadyPending = _repository.AllByAuthorId(author.Id);

                alreadyPending = IncludeRemoteBooks(alreadyPending, authorDecisions.ToDictionaryIgnoreDuplicates(v => v.Item1.RemoteBook.Release.Title, v => v.Item1.RemoteBook));
                var alreadyPendingByBook = CreateBookLookup(alreadyPending);

                foreach (var pair in authorDecisions)
                {
                    var decision = pair.Item1;
                    var reason = pair.Item2;

                    var bookIds = decision.RemoteBook.Books.Where(book => book != null && book.Id > 0)
                        .Select(book => book.Id).Distinct().ToList();

                    var existingReports = bookIds.Where(alreadyPendingByBook.ContainsKey)
                                                    .SelectMany(v => alreadyPendingByBook[v])
                                                    .Distinct().ToList();

                    var matchingReports = existingReports.Where(MatchingReleasePredicate(decision.RemoteBook.Release))
                        .Where(row => (row.AdditionalInfo?.AlternativeCopies ?? false) == decision.RemoteBook.AlternativeCopies &&
                            (!decision.RemoteBook.AlternativeCopies || (row.AdditionalInfo?.BookIds ?? new List<int>()).ToHashSet().SetEquals(bookIds)))
                        .ToList();

                    if (matchingReports.Any())
                    {
                        var matchingReport = matchingReports.First();
                        var changed = false;

                        if (matchingReport.Reason != reason)
                        {
                            if (matchingReport.Reason == PendingReleaseReason.DownloadClientUnavailable)
                            {
                                _logger.Debug("The release {0} is already pending with reason {1}, not changing reason", decision.RemoteBook, matchingReport.Reason);
                            }
                            else
                            {
                                _logger.Debug("The release {0} is already pending with reason {1}, changing to {2}", decision.RemoteBook, matchingReport.Reason, reason);
                                matchingReport.Reason = reason;
                                changed = true;
                            }
                        }
                        else
                        {
                            _logger.Debug("The release {0} is already pending with reason {1}, not adding again", decision.RemoteBook, reason);
                        }

                        // Repair saved IDs from this row's restored context without adopting another request's targets.
                        var restoredBookIds = matchingReport.RemoteBook.Books
                            .Where(book => book != null && book.Id > 0).Select(book => book.Id).Distinct().ToList();
                        if (matchingReport.AdditionalInfo?.AlternativeCopies == true &&
                            matchingReport.AdditionalInfo.BookIds?.SequenceEqual(restoredBookIds) != true)
                        {
                            matchingReport.AdditionalInfo ??= new PendingReleaseAdditionalInfo();
                            matchingReport.AdditionalInfo.BookIds = restoredBookIds;
                            changed = true;
                        }

                        if (changed)
                        {
                            _repository.Update(matchingReport);
                        }

                        if (matchingReports.Count() > 1)
                        {
                            _logger.Debug("The release {0} had {1} duplicate pending, removing duplicates.", decision.RemoteBook, matchingReports.Count() - 1);

                            foreach (var duplicate in matchingReports.Skip(1))
                            {
                                _repository.Delete(duplicate.Id);
                                alreadyPending.Remove(duplicate);
                                alreadyPendingByBook = CreateBookLookup(alreadyPending);
                            }
                        }

                        continue;
                    }

                    _logger.Debug("Adding release {0} to pending releases with reason {1}", decision.RemoteBook, reason);
                    var inserted = Insert(decision, reason, bookIds);
                    // Subsequent decisions must see rows already inserted for this book-scoped batch.
                    alreadyPending.Add(inserted);
                    foreach (var bookId in bookIds)
                    {
                        if (!alreadyPendingByBook.TryGetValue(bookId, out var reports))
                        {
                            reports = new List<PendingRelease>();
                            alreadyPendingByBook.Add(bookId, reports);
                        }

                        reports.Add(inserted);
                    }
                }
            }
        }

        public List<ReleaseInfo> GetPending() => GetPending(out _);

        public List<ReleaseInfo> GetPending(out IReadOnlyDictionary<(int AuthorId, int BookId), DateTime> oldestPublishDates)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var pending = _repository.All().ToList();
            var loadedCount = pending.Count;
            var legacy = pending.Where(row => row.AdditionalInfo?.BookIds?.Any(id => id > 0) != true).ToList();
            // Decision making prepares aggregates and custom formats; this snapshot only hydrates target identity.
            if (legacy.Count > 0)
            {
                IncludeRemoteBooks(legacy, prepareForDownload: false);
            }

            var savedIds = pending.SelectMany(row => row.AdditionalInfo?.BookIds ?? new List<int>())
                .Where(id => id > 0).Distinct().ToList();
            var savedBooks = savedIds.Count > 0
                ? _bookService.GetExistingBooks(savedIds).ToDictionary(book => book.Id)
                : new Dictionary<int, Book>();
            var canceled = savedIds.Where(id => !savedBooks.TryGetValue(id, out var book) || !book.IsMonitored()).ToHashSet();
            RemovePendingTargets(pending, canceled, new HashSet<int>(), "deleted or unmonitored captured target");
            var knownLegacy = legacy.Where(row => row.RemoteBook != null)
                .ToDictionaryIgnoreDuplicates(row => row.Release.Title, row => row.RemoteBook);
            IncludeRemoteBooks(pending.Where(row => row.AdditionalInfo?.BookIds?.Any(id => id > 0) == true).ToList(), knownLegacy, savedBooks, prepareForDownload: false);

            oldestPublishDates = pending
                .Select(row => (Row: row, Media: QualityMediaTypeHelper.DetectMediaType(row.ParsedBookInfo?.Quality?.Quality, row.Release)))
                .SelectMany(item => (item.Row.AdditionalInfo?.BookIds ?? new List<int>())
                    .Where(id => savedBooks.TryGetValue(id, out var book) && book.AuthorId == item.Row.AuthorId &&
                        (!item.Media.HasValue || book.MediaType == item.Media.Value))
                    .Select(id => (Key: (item.Row.AuthorId, id), item.Row.Release.PublishDate)))
                .GroupBy(value => value.Key).ToDictionary(group => group.Key, group => group.Min(value => value.PublishDate));

            var releases = pending.Select(row =>
            {
                row.Release.PendingReleaseReason = row.Reason;
                row.AdditionalInfo ??= new PendingReleaseAdditionalInfo();
                row.Release.PendingContext = row;
                return row.Release;
            }).ToList();
            var result = releases.Count > 0 ? FilterBlockedIndexers(releases) : releases;
            _logger.Debug("Pending snapshot: loaded={0}, legacy={1}, distinctCapturedTargets={2}, returnedReports={3}, elapsedMs={4}",
                loadedCount, legacy.Count, savedIds.Count, result.Count, stopwatch.ElapsedMilliseconds);
            return result;
        }

        public List<RemoteBook> GetPendingRemoteBooks(int authorId)
        {
            return IncludeRemoteBooks(_repository.AllByAuthorId(authorId)).Select(v => v.RemoteBook).ToList();
        }

        public List<Queue.Queue> GetPendingQueue()
        {
            var queued = new List<Queue.Queue>();

            var nextRssSync = new Lazy<DateTime>(() => _taskManager.GetNextExecution(typeof(RssSyncCommand)));

            var pendingReleases = IncludeRemoteBooks(_repository.WithoutFallback());
            foreach (var pendingRelease in pendingReleases)
            {
                DateTime? ect = null;
                TimeSpan? timeleft = null;
                string downloadClientName = null;
                if (pendingRelease.RemoteBook.Books.Any())
                {
                    ect = pendingRelease.Release.PublishDate.AddMinutes(GetDelay(pendingRelease.RemoteBook));
                    ect = ect < nextRssSync.Value ? nextRssSync.Value : ect.Value.AddMinutes(_configService.RssSyncInterval);
                    timeleft = ect.Value.Subtract(DateTime.UtcNow);
                    if (timeleft.Value.TotalSeconds < 0)
                    {
                        timeleft = TimeSpan.Zero;
                    }

                    var indexer = _indexerFactory.Find(pendingRelease.Release.IndexerId);
                    if (indexer is { DownloadClientId: > 0 })
                    {
                        var downloadClient = _downloadClientFactory.Find(indexer.DownloadClientId);
                        downloadClientName = downloadClient?.Name;
                    }
                }

                foreach (var book in pendingRelease.RemoteBook.Books.DefaultIfEmpty())
                {
                    var remoteBook = pendingRelease.RemoteBook;
                    if (remoteBook.AlternativeCopies && book != null)
                    {
                        remoteBook = remoteBook.ForBooks(new[] { book });
                        remoteBook.CustomFormats = _formatCalculator.ParseCustomFormat(remoteBook, remoteBook.Release.Size);
                        remoteBook.CustomFormatScore = remoteBook.GetQualityProfile()?.CalculateCustomFormatScore(remoteBook.CustomFormats) ?? 0;
                        pendingRelease.RemoteBook.TargetCustomFormatScores[book.Id] = remoteBook.CustomFormatScore;
                    }

                    var queue = new Queue.Queue
                    {
                        Id = GetQueueId(pendingRelease, book),
                        PendingReleaseId = pendingRelease.Id,
                        TargetBookIds = (pendingRelease.AdditionalInfo?.BookIds ?? new List<int>()).ToList(),
                        Author = pendingRelease.RemoteBook.Author,
                        Book = book,
                        Quality = pendingRelease.RemoteBook.ParsedBookInfo.Quality,
                        Title = pendingRelease.Title,
                        Size = pendingRelease.RemoteBook.Release.Size,
                        Sizeleft = pendingRelease.RemoteBook.Release.Size,
                        RemoteBook = remoteBook,
                        Timeleft = timeleft,
                        EstimatedCompletionTime = ect,
                        Added = pendingRelease.Added,
                        Status = book == null ? "warning" : pendingRelease.Reason.ToString(),
                        ErrorMessage = book == null ? "Cannot uniquely identify a library book for this pending release." : null,
                        Protocol = pendingRelease.RemoteBook.Release.DownloadProtocol,
                        Indexer = pendingRelease.RemoteBook.Release.Indexer,
                        DownloadClient = downloadClientName
                    };

                    queued.Add(queue);
                }
            }

            //Return best quality release for each book
            var deduped = queued.Where(q => q.Book != null).GroupBy(q => q.Book.Id).Select(g =>
            {
                var author = g.First().Author;

                var qualityProfile = author?.GetQualityProfileForMediaType(g.First().Book.MediaType);

                var ordered = qualityProfile?.Items != null && qualityProfile.Items.Any()
                    ? g.OrderByDescending(
                        e => e,
                        Comparer<Queue.Queue>.Create((left, right) =>
                            QualityProfilePreferenceComparer.CompareReleases(
                                qualityProfile,
                                left.Quality,
                                left.RemoteBook?.CustomFormatScore ?? 0,
                                right.Quality,
                                right.RemoteBook?.CustomFormatScore ?? 0,
                                _configService?.DownloadPropersAndRepacks ?? ProperDownloadTypes.PreferAndUpgrade)
                            .Result))
                    : g.OrderByDescending(e => e.Size);

                return ordered
                    .ThenBy(q => PrioritizeDownloadProtocol(q.Author, q.Protocol, q.Book.MediaType))
                    .First();
            });

            return deduped.Concat(queued.Where(q => q.Book == null)).ToList();
        }

        public Queue.Queue FindPendingQueueItem(int queueId)
        {
            var matches = GetPendingQueue().Where(item => item.Id == queueId).Take(2).ToList();
            if (matches.Count > 1)
            {
                throw new ModelConflictException(typeof(Queue.Queue), queueId, "matches multiple pending items; no item was selected");
            }

            return matches.FirstOrDefault();
        }

        public void RemovePendingQueueItems(IEnumerable<Queue.Queue> queueItems)
        {
            var selected = (queueItems ?? Enumerable.Empty<Queue.Queue>()).ToList();
            if (selected.Count == 0)
            {
                return;
            }

            var selectedBookIds = selected.Where(item => item.Book?.Id > 0).Select(item => item.Book.Id).ToHashSet();
            var unresolvedReleaseIds = selected.Where(item => (item.Book?.Id ?? 0) <= 0 && item.PendingReleaseId > 0)
                .Select(item => item.PendingReleaseId).ToHashSet();
            RemovePendingTargets(_repository.All().ToList(), selectedBookIds, unresolvedReleaseIds, "selected queue targets");
        }

        private void RemovePendingTargets(List<PendingRelease> pending, HashSet<int> bookIds, HashSet<int> releaseIds, string reason)
        {
            if (bookIds.Count == 0 && releaseIds.Count == 0)
            {
                return;
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var updated = new List<PendingRelease>();
            var deleted = new HashSet<int>();
            foreach (var row in pending)
            {
                var savedIds = row.AdditionalInfo?.BookIds ?? new List<int>();
                if (!releaseIds.Contains(row.Id) && !savedIds.Any(bookIds.Contains))
                {
                    continue;
                }

                var retained = savedIds.Where(id => !bookIds.Contains(id)).ToList();
                if (!releaseIds.Contains(row.Id) && row.AdditionalInfo?.AlternativeCopies == true && retained.Count > 0)
                {
                    RetainAlternativeTargets(row, retained);
                    updated.Add(row);
                }
                else
                {
                    deleted.Add(row.Id);
                }
            }

            if (updated.Count > 0) _repository.UpdateMany(updated);
            if (deleted.Count > 0) _repository.DeleteMany(deleted);
            pending.RemoveAll(row => deleted.Contains(row.Id));
            if (updated.Count > 0 || deleted.Count > 0)
            {
                _eventAggregator.PublishEvent(new PendingReleasesUpdatedEvent());
            }

            _logger.Debug("Pending target cleanup: reason={0}, bookIds=[{1}], updated={2}, deleted=[{3}], elapsedMs={4}",
                reason, string.Join(",", bookIds), updated.Count, string.Join(",", deleted), stopwatch.ElapsedMilliseconds);
        }

        public RemoteBook OldestPendingRelease(int authorId, int[] bookIds)
        {
            var authorReleases = GetPendingReleases(authorId);

            return authorReleases.Select(r => r.RemoteBook)
                                 .Where(r => r.Books.Select(e => e.Id).Intersect(bookIds).Any())
                                 .MaxBy(p => p.Release.AgeHours);
        }

        private Dictionary<int, List<PendingRelease>> CreateBookLookup(IEnumerable<PendingRelease> alreadyPending)
        {
            return alreadyPending.SelectMany(v => v.RemoteBook.Books.Where(book => book != null && book.Id > 0)
                                                   .Select(d => new { Book = d, PendingRelease = v }))
                                 .GroupBy(v => v.Book.Id)
                                 .ToDictionary(group => group.Key, group => group.Select(v => v.PendingRelease).ToList());
        }

        private List<ReleaseInfo> FilterBlockedIndexers(List<ReleaseInfo> releases)
        {
            var blockedIndexers = new HashSet<int>(_indexerStatusService.GetBlockedProviders().Select(v => v.ProviderId));

            return releases.Where(release => !blockedIndexers.Contains(release.IndexerId)).ToList();
        }

        private List<PendingRelease> GetPendingReleases()
        {
            return IncludeRemoteBooks(_repository.All().ToList());
        }

        private List<PendingRelease> GetPendingReleases(int authorId)
        {
            return IncludeRemoteBooks(_repository.AllByAuthorId(authorId).ToList());
        }

        private List<PendingRelease> IncludeRemoteBooks(List<PendingRelease> releases, Dictionary<string, RemoteBook> knownRemoteBooks = null, Dictionary<int, Book> savedBooks = null, bool prepareForDownload = true)
        {
            var result = new List<PendingRelease>();

            var authorMap = new Dictionary<int, Author>();

            if (knownRemoteBooks != null)
            {
                foreach (var author in knownRemoteBooks.Values.Select(v => v.Author))
                {
                    if (!authorMap.ContainsKey(author.Id))
                    {
                        authorMap[author.Id] = author;
                    }
                }
            }

            var missingAuthorIds = releases.Select(release => release.AuthorId).Distinct()
                .Where(authorId => !authorMap.ContainsKey(authorId)).ToList();
            if (missingAuthorIds.Count > 0)
            {
                foreach (var author in _authorService.GetExistingAuthors(missingAuthorIds))
                {
                    authorMap[author.Id] = author;
                }
            }

            var booksByRelease = new Dictionary<PendingRelease, List<Book>>();
            var savedBookIds = releases.Where(release => authorMap.ContainsKey(release.AuthorId))
                .SelectMany(release => release.AdditionalInfo?.BookIds ?? new List<int>())
                .Where(id => id > 0).Distinct().ToList();
            savedBooks ??= savedBookIds.Count > 0
                ? _bookService.GetExistingBooks(savedBookIds).ToDictionary(book => book.Id)
                : new Dictionary<int, Book>();
            foreach (var release in releases)
            {
                var mediaType = QualityMediaTypeHelper.DetectMediaType(release.ParsedBookInfo?.Quality?.Quality, release.Release);
                var savedTargets = (release.AdditionalInfo?.BookIds ?? new List<int>()).Distinct()
                    .Where(savedBooks.ContainsKey).Select(id => savedBooks[id]).ToList();
                if (release.AdditionalInfo?.BookIds?.Any(id => id > 0) == true)
                {
                    // An extant target that moved outside this author or media side is not
                    // evidence for a different book with a similar release title.
                    booksByRelease[release] = savedTargets.Where(book => book.AuthorId == release.AuthorId &&
                        (!mediaType.HasValue || book.MediaType == mediaType.Value)).ToList();
                    continue;
                }

                if (knownRemoteBooks != null && knownRemoteBooks.TryGetValue(release.Release.Title, out var known) &&
                    known.Author?.Id == release.AuthorId &&
                    (!mediaType.HasValue || known.Books.All(book => book.MediaType == mediaType.Value)))
                {
                    booksByRelease[release] = known.Books;
                }
            }

            foreach (var authorReleases in releases.Where(release => !booksByRelease.ContainsKey(release)).GroupBy(release => release.AuthorId))
            {
                if (!authorMap.TryGetValue(authorReleases.Key, out var author))
                {
                    continue;
                }

                var pending = authorReleases.ToList();
                var books = _parsingService.GetBooks(pending.Select(release => release.ParsedBookInfo).ToList(), author);
                for (var index = 0; index < pending.Count; index++)
                {
                    booksByRelease[pending[index]] = books[index];
                }
            }

            var capturedLegacy = new List<PendingRelease>();
            foreach (var release in releases)
            {
                var author = authorMap.GetValueOrDefault(release.AuthorId);

                // Just in case the author was removed, but wasn't cleaned up yet (housekeeper will clean it up)
                if (author == null)
                {
                    _logger.Debug("Skipping pending release {0}: author {1} no longer exists", release.Id, release.AuthorId);
                    continue;
                }

                if (release.AdditionalInfo?.BookIds?.Any(id => id > 0) != true && booksByRelease[release].Count > 0)
                {
                    release.AdditionalInfo ??= new PendingReleaseAdditionalInfo();
                    release.AdditionalInfo.BookIds = booksByRelease[release].Select(book => book.Id).Distinct().ToList();
                    capturedLegacy.Add(release);
                }

                release.RemoteBook = new RemoteBook
                {
                    Author = author,
                    Books = booksByRelease[release],
                    ReleaseSource = release.AdditionalInfo?.ReleaseSource ?? ReleaseSourceType.Unknown,
                    ParsedBookInfo = release.ParsedBookInfo,
                    Release = release.Release,
                    AlternativeCopies = release.AdditionalInfo?.AlternativeCopies ?? false,
                    TargetEditionIds = release.AdditionalInfo?.TargetEditionIds ?? new Dictionary<int, int>(),
                    TargetCustomFormatScores = release.AdditionalInfo?.TargetCustomFormatScores ?? new Dictionary<int, int>()
                };

                if (prepareForDownload)
                {
                    _aggregationService.Augment(release.RemoteBook);
                    release.RemoteBook.CustomFormats = _formatCalculator.ParseCustomFormat(release.RemoteBook, release.Release.Size);
                    var qualityProfile = release.RemoteBook.GetQualityProfile();
                    release.RemoteBook.CustomFormatScore = qualityProfile?.CalculateCustomFormatScore(release.RemoteBook.CustomFormats) ?? 0;
                }
                release.Release.PendingContext = release;

                result.Add(release);
            }

            if (capturedLegacy.Count > 0)
            {
                _repository.UpdateMany(capturedLegacy);
                _logger.Debug("Captured targets for {0} legacy pending rows [{1}]", capturedLegacy.Count, string.Join(",", capturedLegacy.Select(row => row.Id)));
                _eventAggregator.PublishEvent(new PendingReleasesUpdatedEvent());
            }

            return result;
        }

        private PendingRelease Insert(DownloadDecision decision, PendingReleaseReason reason, List<int> bookIds)
        {
            var inserted = _repository.Insert(new PendingRelease
            {
                AuthorId = decision.RemoteBook.Author.Id,
                ParsedBookInfo = decision.RemoteBook.ParsedBookInfo,
                Release = decision.RemoteBook.Release,
                Title = decision.RemoteBook.Release.Title,
                Added = DateTime.UtcNow,
                Reason = reason,
                RemoteBook = decision.RemoteBook,
                AdditionalInfo = new PendingReleaseAdditionalInfo
                {
                    ReleaseSource = decision.RemoteBook.ReleaseSource,
                    BookIds = bookIds,
                    AlternativeCopies = decision.RemoteBook.AlternativeCopies,
                    TargetEditionIds = new Dictionary<int, int>(decision.RemoteBook.TargetEditionIds),
                    TargetCustomFormatScores = new Dictionary<int, int>(decision.RemoteBook.TargetCustomFormatScores)
                }
            });

            _eventAggregator.PublishEvent(new PendingReleasesUpdatedEvent());
            return inserted;
        }

        private void Delete(PendingRelease pendingRelease)
        {
            _repository.Delete(pendingRelease);
            _eventAggregator.PublishEvent(new PendingReleasesUpdatedEvent());
        }

        private int GetDelay(RemoteBook remoteBook)
        {
            var mediaType = remoteBook.GetPreferredMediaType();
            var tags = remoteBook.Author.GetTagsForMediaType(mediaType);
            var delayProfile = _delayProfileService.AllForTags(tags).OrderBy(d => d.Order).First();
            var delay = delayProfile.GetProtocolDelay(remoteBook.Release.DownloadProtocol);
            var minimumAge = _configService.MinimumAge;

            return new[] { delay, minimumAge }.Max();
        }

        private void RemoveGrabbed(RemoteBook remoteBook)
        {
            var pendingReleases = GetPendingReleases(remoteBook.Author.Id);
            var bookIds = remoteBook.Books.Select(e => e.Id);

            var existingReports = pendingReleases.Where(r => r.RemoteBook.Books.Select(e => e.Id)
                                                             .Intersect(bookIds)
                                                             .Any())
                                                             .ToList();

            if (existingReports.Empty())
            {
                return;
            }

            var profile = remoteBook.GetQualityProfile();
            if (profile == null)
            {
                return; // Skip if no quality profile configured
            }

            foreach (var existingReport in existingReports)
            {
                if (existingReport.AdditionalInfo?.AlternativeCopies == true)
                {
                    var satisfiedIds = existingReport.AdditionalInfo.BookIds.Where(bookIds.Contains).Where(id =>
                        QualityProfilePreferenceComparer.CompareReleases(profile, remoteBook.ParsedBookInfo.Quality,
                            remoteBook.TargetCustomFormatScores.GetValueOrDefault(id, remoteBook.CustomFormatScore),
                            existingReport.RemoteBook.ParsedBookInfo.Quality,
                            existingReport.AdditionalInfo.TargetCustomFormatScores.GetValueOrDefault(id, existingReport.RemoteBook.CustomFormatScore),
                            _configService?.DownloadPropersAndRepacks ?? ProperDownloadTypes.PreferAndUpgrade).Result >= 0).ToHashSet();
                    if (satisfiedIds.Count > 0)
                    {
                        var retained = existingReport.AdditionalInfo.BookIds.Where(id => !satisfiedIds.Contains(id)).ToList();
                        if (retained.Count == 0)
                        {
                            Delete(existingReport);
                        }
                        else
                        {
                            RetainAlternativeTargets(existingReport, retained);
                            _repository.Update(existingReport);
                            _eventAggregator.PublishEvent(new PendingReleasesUpdatedEvent());
                        }
                    }

                    continue;
                }

                var compare = QualityProfilePreferenceComparer.CompareReleases(
                    profile,
                    remoteBook.ParsedBookInfo.Quality,
                    remoteBook.CustomFormatScore,
                    existingReport.RemoteBook.ParsedBookInfo.Quality,
                    existingReport.RemoteBook.CustomFormatScore,
                    _configService?.DownloadPropersAndRepacks ?? ProperDownloadTypes.PreferAndUpgrade)
                    .Result;

                //Only remove lower/equal quality pending releases
                //It is safer to retry these releases on the next round than remove it and try to re-add it (if its still in the feed)
                if (compare >= 0)
                {
                    _logger.Debug("Removing previously pending release, as it was grabbed.");
                    Delete(existingReport);
                }
            }
        }

        private void RemoveRejected(List<DownloadDecision> rejected)
        {
            if (rejected.Count == 0)
            {
                return;
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var updated = new List<int>();
            var deleted = new List<int>();
            var pending = _repository.All().ToList();

            foreach (var row in pending)
            {
                var rejectedIds = rejected.Where(decision => MatchingReleasePredicate(decision.RemoteBook.Release)(row))
                    .SelectMany(decision => decision.RemoteBook.Books ?? new List<Book>())
                    .Where(book => book?.Id > 0).Select(book => book.Id).ToHashSet();
                var savedIds = row.AdditionalInfo?.BookIds ?? new List<int>();
                if (!savedIds.Any(rejectedIds.Contains))
                {
                    continue;
                }

                var retained = savedIds.Where(id => !rejectedIds.Contains(id)).ToList();
                if (row.AdditionalInfo?.AlternativeCopies == true && retained.Count > 0)
                {
                    RetainAlternativeTargets(row, retained);
                    _repository.Update(row);
                    updated.Add(row.Id);
                    _eventAggregator.PublishEvent(new PendingReleasesUpdatedEvent());
                }
                else
                {
                    Delete(row);
                    deleted.Add(row.Id);
                }
            }

            _logger.Debug("Pending rejection cleanup: reason=rejected target, updated={0}, updatedIdsSample=[{1}], deleted={2}, deletedIdsSample=[{3}], elapsedMs={4}",
                updated.Count, string.Join(",", updated.Take(20)), deleted.Count, string.Join(",", deleted.Take(20)), stopwatch.ElapsedMilliseconds);
        }

        private static void RetainAlternativeTargets(PendingRelease row, List<int> retained)
        {
            var ids = retained.ToHashSet();
            row.AdditionalInfo.BookIds = retained;
            row.AdditionalInfo.TargetEditionIds = row.AdditionalInfo.TargetEditionIds
                .Where(pair => ids.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value);
            row.AdditionalInfo.TargetCustomFormatScores = row.AdditionalInfo.TargetCustomFormatScores
                .Where(pair => ids.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value);
        }

        private int GetQueueId(PendingRelease pendingRelease, Book book)
        {
            return HashConverter.GetHashInt31(string.Format("pending-{0}-book{1}", pendingRelease.Id, book?.Id ?? 0));
        }

        private int PrioritizeDownloadProtocol(Author author, DownloadProtocol downloadProtocol, BookMediaType mediaType)
        {
            var tags = author.GetTagsForMediaType(mediaType);
            var delayProfile = _delayProfileService.BestForTags(tags);

            if (downloadProtocol == delayProfile.PreferredProtocol)
            {
                return 0;
            }

            return 1;
        }

        public void Handle(AuthorDeletedEvent message)
        {
            _repository.DeleteByAuthorId(message.Author.Id);
        }

        public void Handle(BookDeletedEvent message)
        {
            var deletedIds = message.DeletedBooks.Select(book => book.Id).Where(id => id > 0).ToHashSet();
            RemovePendingTargets(_repository.All().ToList(), deletedIds, new HashSet<int>(), "captured book deleted");
        }

        public void Handle(BookGrabbedEvent message)
        {
            RemoveGrabbed(message.Book);
        }

        public void Handle(RssSyncCompleteEvent message)
        {
            RemoveRejected(message.ProcessedDecisions.Rejected);
        }

        private static Func<PendingRelease, bool> MatchingReleasePredicate(ReleaseInfo release)
        {
            return p => p.Title == release.Title &&
                        p.Release.PublishDate == release.PublishDate &&
                        p.Release.Indexer == release.Indexer;
        }
    }
}
