using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Books;
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
    }

    public class GroupedAuthorSearchService : IGroupedAuthorSearch, IExecute<InteractiveAuthorSearchCommand>, IHandleAsync<ModelEvent<CommandModel>>
    {
        private sealed class ActiveSearch
        {
            public InteractiveAuthorSearchCommand Command { get; init; }
            public CancellationTokenSource Cancellation { get; } = new();
        }

        private readonly ISearchForReleases _releaseSearchService;
        private readonly IPrioritizeDownloadDecision _prioritizeDownloadDecision;
        private readonly IManageCommandQueue _commandQueue;
        private readonly InteractiveReleaseMapper _mapper;
        private readonly IBroadcastSignalRMessage _signalRBroadcaster;
        private readonly ICached<ActiveSearch> _cancellations;
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
            _cancellations = cacheManager.GetCache<ActiveSearch>(typeof(GroupedAuthorSearchService), "authorSearchCancellations");
            _logger = logger;
        }

        public AuthorReleaseSearchResponse Start(int authorId, BookMediaType mediaType, string searchId)
        {
            var key = Guid.Parse(searchId).ToString();
            int commandId;
            lock (_cancellations)
            {
                if (_cancellations.Find(key) != null)
                {
                    throw new NzbDroneClientException(HttpStatusCode.Conflict, "This searchId is already active");
                }

                var active = new ActiveSearch
                {
                    Command = new InteractiveAuthorSearchCommand
                    {
                        SearchId = searchId,
                        AuthorId = authorId,
                        MediaType = mediaType,
                        SuppressMessages = true,
                        SendUpdatesToClient = true
                    }
                };
                _cancellations.Set(key, active);
                try
                {
                    var queued = _commandQueue.Push(active.Command, CommandPriority.High, CommandTrigger.Manual);
                    commandId = queued.Id;
                    if (!ReferenceEquals(queued.Body, active.Command))
                    {
                        throw new NzbDroneClientException(HttpStatusCode.Conflict, "This searchId is already active");
                    }
                }
                catch
                {
                    _cancellations.Remove(key);
                    active.Cancellation.Dispose();
                    throw;
                }
            }

            return new AuthorReleaseSearchResponse { CommandId = commandId, SearchId = searchId, AuthorId = authorId, MediaType = ToMediaTypeName(mediaType) };
        }

        public AuthorReleaseSearchStopResponse Stop(Guid searchId)
        {
            lock (_cancellations)
            {
                var cancellation = _cancellations.Find(searchId.ToString())?.Cancellation;
                cancellation?.Cancel();
                return new AuthorReleaseSearchStopResponse { IsStopping = cancellation != null };
            }
        }

        public void HandleAsync(ModelEvent<CommandModel> message)
        {
            if (message.Model.Body is not InteractiveAuthorSearchCommand command ||
                message.Model.Status != CommandStatus.Cancelled || message.Model.StartedAt.HasValue)
            {
                return;
            }

            var key = Guid.Parse(command.SearchId).ToString();
            lock (_cancellations)
            {
                var active = _cancellations.Find(key);
                if (!ReferenceEquals(active?.Command, command))
                {
                    return;
                }

                _cancellations.Remove(key);
                active.Cancellation.Dispose();
            }

            PublishAuthorSearchResults(new AuthorReleaseSearchResponse
            {
                SearchId = command.SearchId,
                AuthorId = command.AuthorId,
                MediaType = ToMediaTypeName(command.MediaType),
                IsComplete = true,
                IsCancelled = true
            }, Array.Empty<BookReleaseSearchResult>()).GetAwaiter().GetResult();
        }

        public void Execute(InteractiveAuthorSearchCommand command)
        {
            Execute(command, CancellationToken.None);
        }

        public void Execute(InteractiveAuthorSearchCommand command, CancellationToken cancellationToken)
        {
            Run(command, cancellationToken).GetAwaiter().GetResult();
            cancellationToken.ThrowIfCancellationRequested();
        }

        internal async Task Run(InteractiveAuthorSearchCommand command, CancellationToken workerCancellation)
        {
            var key = Guid.Parse(command.SearchId).ToString();
            CancellationTokenSource cancellation;
            lock (_cancellations)
            {
                var active = _cancellations.Find(key);
                cancellation = ReferenceEquals(active?.Command, command) ? active.Cancellation : null;
            }

            // Interactive results belong to the current session, not a replay after restart.
            if (cancellation == null)
            {
                return;
            }

            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token, workerCancellation);
            var cancellationToken = linkedCancellation.Token;
            var response = new AuthorReleaseSearchResponse
            {
                SearchId = command.SearchId,
                AuthorId = command.AuthorId,
                MediaType = ToMediaTypeName(command.MediaType)
            };
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _releaseSearchService.AuthorSearch(command.AuthorId, false, true, true, command.MediaType,
                    async books =>
                    {
                        response.BookResults = books.Select(book => new BookReleaseSearchResult
                        {
                            BookId = book.Id,
                            Title = book.Editions?.Where(edition => edition.Monitored).OrderBy(edition => edition.Id).FirstOrDefault()?.Title ?? book.Title,
                            MediaType = ToMediaTypeName(book.MediaType),
                            Status = "pending"
                        }).ToList();
                        await PublishAuthorSearchResults(response, response.BookResults);
                    },
                    async (book, decisions, error) =>
                    {
                        var group = MapBookSearchResults(book, decisions, error, response.SearchId);
                        var index = response.BookResults.FindIndex(result => result.BookId == book.Id);
                        response.BookResults[index] = group;
                        await PublishAuthorSearchResults(response, new[] { group });
                    },
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                response.IsCancelled = true;
                _logger.Info("Stopped author search {0} after {1} of {2} books; completed results retained", command.SearchId,
                    response.BookResults.Count(result => result.Status != "pending"), response.BookResults.Count);
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
                await PublishAuthorSearchResults(response, response.BookResults);
                lock (_cancellations)
                {
                    _cancellations.Remove(key);
                    cancellation.Dispose();
                }
            }
        }

        private BookReleaseSearchResult MapBookSearchResults(Book book, List<DownloadDecision> decisions, Exception error, string searchId)
        {
            var prioritized = _prioritizeDownloadDecision.PrioritizeDecisions(decisions);
            var (visible, hidden, _) = _mapper.MapInteractiveDecisions(prioritized, false,
                (decision, weight) => _mapper.MapDecision(decision, weight, searchId, book.Id));

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
                Releases = visible,
                HiddenReleases = hidden
            };
        }

        private async Task PublishAuthorSearchResults(AuthorReleaseSearchResponse response, IEnumerable<BookReleaseSearchResult> groups)
        {
            try
            {
                await _signalRBroadcaster.BroadcastMessage(new SignalRMessage
                {
                    Name = "AuthorSearchResults",
                    Body = new AuthorReleaseSearchResponse
                    {
                        SearchId = response.SearchId,
                        AuthorId = response.AuthorId,
                        MediaType = response.MediaType,
                        BookResults = groups.ToList(),
                        IsComplete = response.IsComplete,
                        IsCancelled = response.IsCancelled,
                        IsFailed = response.IsFailed
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Unable to deliver author search update");
            }
        }

        private static string ToMediaTypeName(BookMediaType mediaType)
        {
            return mediaType.ToString().ToLowerInvariant();
        }
    }
}
