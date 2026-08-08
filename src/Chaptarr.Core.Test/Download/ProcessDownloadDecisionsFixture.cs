using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class ProcessDownloadDecisionsFixture
    {
        [Test]
        public async Task should_only_grab_one_release_per_book_row_even_when_second_release_is_unknown_text()
        {
            var downloadService = new RecordingDownloadService();
            var subject = new ProcessDownloadDecisions(
                downloadService,
                new IdentityPrioritizer(),
                new NoopPendingReleaseService(),
                LogManager.GetLogger("ProcessDownloadDecisionsFixture"));

            var book = new Book { Id = 5792, Title = "Best Served Cold", MediaType = BookMediaType.Ebook };
            var first = BuildDecision(book, "Best Served Cold", Quality.AZW3);
            var duplicateUnknown = BuildDecision(book, "Joe Abercrombie - The First Law Trilogy & Best Served Cold", Quality.Unknown);

            var result = await subject.ProcessDecisions(new List<DownloadDecision> { first, duplicateUnknown });

            Assert.That(result.Grabbed, Has.Count.EqualTo(1));
            Assert.That(downloadService.Downloaded, Has.Count.EqualTo(1));
            Assert.That(downloadService.Downloaded[0].ParsedBookInfo.Quality.Quality, Is.EqualTo(Quality.AZW3));
        }

        [Test]
        public async Task central_mam_slot_race_should_return_the_release_to_the_existing_pending_queue()
        {
            var pending = new RecordingPendingReleaseService();
            var subject = new ProcessDownloadDecisions(
                new MamSlotBlockedDownloadService(),
                new IdentityPrioritizer(),
                pending,
                LogManager.GetLogger("ProcessDownloadDecisionsFixture"));
            var decision = BuildDecision(new Book { Id = 5792, Title = "Book", MediaType = BookMediaType.Ebook }, "Book", Quality.EPUB);

            var result = await subject.ProcessDecision(decision, null);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.EqualTo(ProcessedDecisionResult.Pending));
                Assert.That(pending.Added.Single().Decision, Is.SameAs(decision));
                Assert.That(pending.Added.Single().Reason, Is.EqualTo(PendingReleaseReason.Delay));
            });
        }

        private static DownloadDecision BuildDecision(Book book, string title, Quality quality)
        {
            var author = new Author { Id = 38, Name = "Joe Abercrombie" };
            return new DownloadDecision(new RemoteBook
            {
                Author = author,
                Books = new List<Book> { book },
                Release = new ReleaseInfo
                {
                    Title = title,
                    DownloadProtocol = DownloadProtocol.Torrent,
                    PublishDate = DateTime.UtcNow
                },
                ParsedBookInfo = new ParsedBookInfo
                {
                    Quality = new QualityModel(quality)
                }
            });
        }

        private sealed class RecordingDownloadService : IDownloadService
        {
            public List<RemoteBook> Downloaded { get; } = new();

            public Task DownloadReport(RemoteBook remoteBook, int? downloadClientId)
            {
                Downloaded.Add(remoteBook);
                return Task.CompletedTask;
            }
        }

        private sealed class MamSlotBlockedDownloadService : IDownloadService
        {
            public Task DownloadReport(RemoteBook remoteBook, int? downloadClientId)
            {
                throw new MamUnsatisfiedSlotsUnavailableException("MAM safety pause");
            }
        }

        private sealed class IdentityPrioritizer : IPrioritizeDownloadDecision
        {
            public List<DownloadDecision> PrioritizeDecisions(List<DownloadDecision> decisions)
            {
                return decisions;
            }
        }

        private class NoopPendingReleaseService : IPendingReleaseService
        {
            public virtual void Add(DownloadDecision decision, PendingReleaseReason reason) { }
            public void AddMany(List<Tuple<DownloadDecision, PendingReleaseReason>> decisions) { }
            public List<ReleaseInfo> GetPending() => new();
            public List<RemoteBook> GetPendingRemoteBooks(int authorId) => new();
            public List<NzbDrone.Core.Queue.Queue> GetPendingQueue() => new();
            public NzbDrone.Core.Queue.Queue FindPendingQueueItem(int queueId) => null;
            public void RemovePendingQueueItems(int queueId) { }
            public RemoteBook OldestPendingRelease(int authorId, int[] bookIds) => null;
        }

        private sealed class RecordingPendingReleaseService : NoopPendingReleaseService
        {
            public List<(DownloadDecision Decision, PendingReleaseReason Reason)> Added { get; } = new();

            public override void Add(DownloadDecision decision, PendingReleaseReason reason)
            {
                Added.Add((decision, reason));
            }
        }
    }
}
