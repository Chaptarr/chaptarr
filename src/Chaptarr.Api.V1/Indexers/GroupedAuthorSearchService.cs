using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Datastore.Events;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.SignalR;

namespace Chaptarr.Api.V1.Indexers
{
    public interface IGroupedAuthorSearch
    {
        AuthorReleaseSearchResponse Start(int authorId, BookMediaType mediaType, string searchId);
        AuthorReleaseSearchStopResponse Stop(Guid searchId);
        AuthorReleaseSearchResponse GetSnapshot(int authorId, BookMediaType mediaType);
        DownloadDecision GetDecision(ReleaseResource release);
        void MarkGrabbed(ReleaseResource release);
    }

    public class GroupedAuthorSearchService : IGroupedAuthorSearch, IExecute<InteractiveAuthorSearchCommand>, IHandleAsync<ModelEvent<CommandModel>>, IHandleAsync<CommandExecutedEvent>, IHandleAsync<AuthorDeletedEvent>
    {
        private sealed class ActiveSearch
        {
            public InteractiveAuthorSearchCommand Command { get; init; }
            public CancellationTokenSource Cancellation { get; } = new();
            public TaskCompletionSource<CommandModel> Admission { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public Dictionary<string, (DownloadDecision Decision, ReleaseResource Resource)> Decisions { get; } = new();
            public AuthorReleaseSearchResponse Snapshot { get; set; }
        }

        private readonly ISearchForReleases _releaseSearchService;
        private readonly IPrioritizeDownloadDecision _prioritizeDownloadDecision;
        private readonly IManageCommandQueue _commandQueue;
        private readonly InteractiveReleaseMapper _mapper;
        private readonly IBroadcastSignalRMessage _signalRBroadcaster;
        private readonly ICached<ActiveSearch> _sessions;
        private readonly ICached<ActiveSearch> _searches;
        private readonly Logger _logger;

        public GroupedAuthorSearchService(ISearchForReleases releaseSearchService,
                                         IPrioritizeDownloadDecision prioritizeDownloadDecision,
                                         IManageCommandQueue commandQueue,
                                         InteractiveReleaseMapper mapper,
                                         IBroadcastSignalRMessage signalRBroadcaster,
                                         ICacheManager cacheManager,
                                         Logger logger)
        {
            _releaseSearchService = releaseSearchService;
            _prioritizeDownloadDecision = prioritizeDownloadDecision;
            _commandQueue = commandQueue;
            _mapper = mapper;
            _signalRBroadcaster = signalRBroadcaster;
            _sessions = cacheManager.GetCache<ActiveSearch>(typeof(GroupedAuthorSearchService), "authorSearchSessions");
            _searches = cacheManager.GetCache<ActiveSearch>(typeof(GroupedAuthorSearchService), "authorSearchSnapshots");
            _logger = logger;
        }

        public AuthorReleaseSearchResponse Start(int authorId, BookMediaType mediaType, string searchId)
        {
            var key = Guid.Parse(searchId).ToString();
            ActiveSearch active;
            ActiveSearch previous;
            var scope = Scope(authorId, mediaType);
            var created = false;
            lock (_sessions)
            {
                previous = _searches.Find(scope);
                active = previous;
                if (previous == null || previous.Snapshot.IsComplete)
                {
                    if (_sessions.Find(key) != null)
                    {
                        throw new NzbDroneClientException(HttpStatusCode.Conflict, "This searchId is already in use");
                    }

                    active = new ActiveSearch
                    {
                        Command = new InteractiveAuthorSearchCommand
                        {
                            SearchId = searchId, AuthorId = authorId, MediaType = mediaType,
                            SendUpdatesToClient = true
                        },
                        Snapshot = new AuthorReleaseSearchResponse { SearchId = searchId, AuthorId = authorId, MediaType = ToMediaTypeName(mediaType) }
                    };
                    _sessions.Set(key, active);
                    _searches.Set(scope, active);
                    created = true;
                }
            }

            if (created)
            {
                try
                {
                    var queued = _commandQueue.Push(active.Command, CommandPriority.Normal, CommandTrigger.Manual);
                    if (!ReferenceEquals(queued.Body, active.Command))
                    {
                        throw new NzbDroneClientException(HttpStatusCode.Conflict, "This searchId is already active");
                    }
                    lock (_sessions)
                    {
                        active.Snapshot.CommandId = queued.Id;
                        if (previous != null) _sessions.Remove(Guid.Parse(previous.Command.SearchId).ToString());
                        _logger.Debug("Retained author searches: {0} snapshots after starting {1}", _searches.Count, searchId);
                    }
                    active.Admission.SetResult(queued);
                }
                catch (Exception ex)
                {
                    lock (_sessions)
                    {
                        _sessions.Remove(key);
                        _searches.Remove(scope);
                        if (previous?.Snapshot.ExpiresAt > DateTime.UtcNow)
                        {
                            _searches.Set(scope, previous, previous.Snapshot.ExpiresAt - DateTime.UtcNow);
                        }
                        active.Cancellation.Dispose();
                    }
                    active.Admission.SetException(ex);
                }
            }

            active.Admission.Task.GetAwaiter().GetResult();
            lock (_sessions) return Copy(active.Snapshot, active.Snapshot.BookResults);
        }

        private static string Scope(int authorId, BookMediaType mediaType) => $"{authorId}:{mediaType}";

        public AuthorReleaseSearchResponse GetSnapshot(int authorId, BookMediaType mediaType)
        {
            lock (_sessions)
            {
                var snapshot = _searches.Find(Scope(authorId, mediaType))?.Snapshot;
                return snapshot == null ? null : Copy(snapshot, snapshot.BookResults);
            }
        }

        private ActiveSearch FindSession(ReleaseResource release)
        {
            var session = _sessions.Find(Guid.Parse(release.SearchId).ToString());
            return session != null && ReferenceEquals(_searches.Find(Scope(session.Command.AuthorId, session.Command.MediaType)), session)
                ? session : null;
        }

        public DownloadDecision GetDecision(ReleaseResource release)
        {
            lock (_sessions)
            {
                var session = FindSession(release);
                return session != null && session.Decisions.TryGetValue(InteractiveReleaseMapper.GetCacheKey(release), out var entry) ? entry.Decision : null;
            }
        }

        public void MarkGrabbed(ReleaseResource release)
        {
            lock (_sessions)
            {
                var session = FindSession(release);
                if (session != null && session.Decisions.TryGetValue(InteractiveReleaseMapper.GetCacheKey(release), out var entry))
                {
                    entry.Resource.IsGrabbed = true;
                }
            }
        }

        public AuthorReleaseSearchStopResponse Stop(Guid searchId)
        {
            ActiveSearch active;
            lock (_sessions)
            {
                active = _sessions.Find(searchId.ToString());
                if (active == null || active.Snapshot.IsComplete) return new AuthorReleaseSearchStopResponse();
            }

            var command = active.Admission.Task.GetAwaiter().GetResult();
            if (!active.Snapshot.StartedAt.HasValue && _commandQueue.CancelIfQueued(command.Id)) return new AuthorReleaseSearchStopResponse { IsRemoved = true };

            lock (_sessions)
            {
                if (active.Snapshot.IsComplete) return new AuthorReleaseSearchStopResponse();
                active.Cancellation.Cancel();
                return new AuthorReleaseSearchStopResponse { IsStopping = true };
            }
        }

        public void HandleAsync(AuthorDeletedEvent message)
        {
            foreach (var mediaType in new[] { BookMediaType.Audiobook, BookMediaType.Ebook })
            {
                var scope = Scope(message.Author.Id, mediaType);
                ActiveSearch session;
                lock (_sessions) session = _searches.Find(scope);
                if (session == null) continue;
                Stop(Guid.Parse(session.Command.SearchId));
                lock (_sessions)
                {
                    if (!ReferenceEquals(_searches.Find(scope), session)) continue;
                    _searches.Remove(scope);
                    _sessions.Remove(Guid.Parse(session.Command.SearchId).ToString());
                }
            }
        }

        public void HandleAsync(ModelEvent<CommandModel> message) => CompleteUnstartedSearch(message.Model);

        public void HandleAsync(CommandExecutedEvent message) => CompleteUnstartedSearch(message.Command);

        private void CompleteUnstartedSearch(CommandModel model)
        {
            if (model.Body is not InteractiveAuthorSearchCommand command ||
                model.Status is not (CommandStatus.Cancelled or CommandStatus.Failed or CommandStatus.Aborted or CommandStatus.Orphaned))
            {
                return;
            }

            var key = Guid.Parse(command.SearchId).ToString();
            AuthorReleaseSearchResponse response;
            lock (_sessions)
            {
                var active = _sessions.Find(key);
                if (!ReferenceEquals(active?.Command, command) || active.Snapshot.IsComplete || active.Snapshot.StartedAt.HasValue) return;
                response = Copy(active.Snapshot, active.Snapshot.BookResults);
                response.IsComplete = true;
                response.IsCancelled = model.Status == CommandStatus.Cancelled;
                response.IsFailed = !response.IsCancelled;
                Retain(response);
                active.Cancellation.Dispose();
            }
            PublishAuthorSearchResults(response, response.BookResults).GetAwaiter().GetResult();
        }

        public void Execute(InteractiveAuthorSearchCommand command)
        {
            Execute(command, CancellationToken.None);
        }

        public void Execute(InteractiveAuthorSearchCommand command, CancellationToken cancellationToken)
        {
            if (Run(command, cancellationToken).GetAwaiter().GetResult())
            {
                throw new OperationCanceledException(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }

        internal async Task<bool> Run(InteractiveAuthorSearchCommand command, CancellationToken workerCancellation)
        {
            var timer = Stopwatch.StartNew();
            string authorName = null;
            var key = Guid.Parse(command.SearchId).ToString();
            ActiveSearch session;
            CancellationTokenSource cancellation;
            AuthorReleaseSearchResponse response;
            lock (_sessions)
            {
                var active = _sessions.Find(key);
                // Search results are in memory; a restored command cannot replay the session.
                if (!ReferenceEquals(active?.Command, command))
                {
                    _logger.Warn("Author search {0} interrupted: its saved session is no longer available", command.SearchId);
                    throw new CommandFailedException("Search interrupted because its saved session is no longer available. Start a new search.");
                }
                if (active.Snapshot.IsComplete)
                {
                    return false;
                }

                session = active;
                cancellation = active.Cancellation;
                response = Copy(active.Snapshot, active.Snapshot.BookResults);
                response.StartedAt = DateTime.UtcNow;
                Retain(response);
            }

            response.CommandId = (await session.Admission.Task).Id;
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token, workerCancellation);
            var cancellationToken = linkedCancellation.Token;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _releaseSearchService.AuthorSearch(command.AuthorId, false, true, true, command.MediaType,
                    async books =>
                    {
                        authorName = books.FirstOrDefault()?.Author?.Name;
                        command.AuthorName = authorName;
                        command.BooksTotal = books.Count;
                        response.BookResults = books.Select(book => new BookReleaseSearchResult
                        {
                            BookId = book.Id,
                            Title = book.Editions?.Where(edition => edition.Monitored).OrderBy(edition => edition.Id).FirstOrDefault()?.Title ?? book.Title,
                            MediaType = ToMediaTypeName(book.MediaType),
                            Status = "pending"
                        }).ToList();
                        _logger.ProgressInfo(command.ProgressMessage);
                        await PublishAuthorSearchResults(response, response.BookResults);
                    },
                    async (book, decisions, error) =>
                    {
                        var group = MapBookSearchResults(book, decisions, error, session);
                        var index = response.BookResults.FindIndex(result => result.BookId == book.Id);
                        response.BookResults[index] = group;
                        command.BooksDone = response.BookResults.Count(result => result.Status != "pending");
                        _logger.ProgressInfo(command.ProgressMessage);
                        await PublishAuthorSearchResults(response, new[] { group });
                    },
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                response.IsCancelled = true;
            }
            catch (Exception ex)
            {
                response.IsFailed = true;
                foreach (var group in response.BookResults.Where(result => result.Status == "pending"))
                {
                    group.Status = "failed";
                }

                _logger.Error(ex, "Grouped author search {0} failed", command.SearchId);
                throw;
            }
            finally
            {
                response.IsComplete = true;
                _logger.Debug("Author search for {0} (author {1}, search {2}) {3} in {4:F1} s: {5} of {6} books",
                    authorName ?? "Unknown author", command.AuthorId, command.SearchId,
                    response.IsCancelled ? "cancelled" : response.IsFailed ? "failed" : "finished", timer.Elapsed.TotalSeconds,
                    response.BookResults.Count(result => result.Status != "pending"), response.BookResults.Count);
                await PublishAuthorSearchResults(response, response.BookResults);
                if (_logger.IsDebugEnabled)
                {
                    _logger.Debug("Retained author search {0}: {1} rows", response.SearchId,
                        session.Decisions.Count);
                }
                lock (_sessions) cancellation.Dispose();
            }

            return response.IsCancelled;
        }

        private BookReleaseSearchResult MapBookSearchResults(Book book, BookSearchResult result, Exception error, ActiveSearch session)
        {
            var prioritized = _prioritizeDownloadDecision.PrioritizeDecisions(result.Decisions);
            var (visible, hidden, _) = _mapper.MapInteractiveDecisions(prioritized, false,
                (decision, weight) =>
                {
                    var resource = _mapper.MapDecision(decision, weight, session.Command.SearchId, book.Id);
                    lock (_sessions) session.Decisions[InteractiveReleaseMapper.GetCacheKey(resource)] = (decision, resource);
                    return resource;
                });

            var rank = 0;
            foreach (var resource in visible.Concat(hidden))
            {
                resource.ReleaseWeight = rank;
                resource.Rank = ++rank;
            }

            return new BookReleaseSearchResult
            {
                BookId = book.Id,
                Title = book.Editions?.Where(edition => edition.Monitored).OrderBy(edition => edition.Id).FirstOrDefault()?.Title ?? book.Title,
                MediaType = ToMediaTypeName(book.MediaType),
                Status = error == null ? "complete" : "failed",
                IndexersQueried = result.Indexers.Queried,
                IndexersFailed = result.Indexers.Failed,
                Releases = visible,
                HiddenReleases = hidden
            };
        }

        private void Retain(AuthorReleaseSearchResponse response)
        {
            var active = _sessions.Find(Guid.Parse(response.SearchId).ToString());
            if (active == null) return;
            if (response.IsComplete)
            {
                response.FinishedAt ??= DateTime.UtcNow;
                response.ExpiresAt = response.FinishedAt + InteractiveReleaseMapper.CacheLifetime;
                if (!active.Snapshot.IsComplete)
                    _logger.Debug("Author search {0} results expire at {1:O}", response.SearchId, response.ExpiresAt);
            }
            active.Command.ResultsExpireAt = response.ExpiresAt;
            active.Snapshot = Copy(response, response.BookResults, false);
            _sessions.Set(Guid.Parse(response.SearchId).ToString(), active,
                response.IsComplete ? response.ExpiresAt - DateTime.UtcNow : null);
            _searches.Set(Scope(response.AuthorId, active.Command.MediaType), active,
                response.IsComplete ? response.ExpiresAt - DateTime.UtcNow : null);
        }

        private static AuthorReleaseSearchResponse Copy(AuthorReleaseSearchResponse response, IEnumerable<BookReleaseSearchResult> groups, bool copyRows = true) => new()
        {
            CommandId = response.CommandId, SearchId = response.SearchId, AuthorId = response.AuthorId, MediaType = response.MediaType,
            StartedAt = response.StartedAt, FinishedAt = response.FinishedAt, ExpiresAt = response.ExpiresAt,
            IsComplete = response.IsComplete, IsCancelled = response.IsCancelled, IsFailed = response.IsFailed,
            BookResults = groups.Select(group => new BookReleaseSearchResult
            {
                BookId = group.BookId, Title = group.Title, MediaType = group.MediaType, Status = group.Status,
                IndexersQueried = group.IndexersQueried, IndexersFailed = group.IndexersFailed,
                Releases = copyRows ? group.Releases.Select(row => row.Copy(response.ExpiresAt)).ToList() : group.Releases,
                HiddenReleases = copyRows ? group.HiddenReleases.Select(row => row.Copy(response.ExpiresAt)).ToList() : group.HiddenReleases
            }).ToList()
        };

        private async Task PublishAuthorSearchResults(AuthorReleaseSearchResponse response, IEnumerable<BookReleaseSearchResult> groups)
        {
            AuthorReleaseSearchResponse message;
            lock (_sessions)
            {
                Retain(response);
                message = Copy(response, response.IsComplete ? Array.Empty<BookReleaseSearchResult>() : groups);
            }
            try
            {
                await _signalRBroadcaster.BroadcastMessage(new SignalRMessage
                {
                    Name = "AuthorSearchResults",
                    Body = message
                });
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Unable to publish author search {0}; its snapshot remains available", response.SearchId);
            }
        }

        private static string ToMediaTypeName(BookMediaType mediaType)
        {
            return mediaType.ToString().ToLowerInvariant();
        }
    }
}
