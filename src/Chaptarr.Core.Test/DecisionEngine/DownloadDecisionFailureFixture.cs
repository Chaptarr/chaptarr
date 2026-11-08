using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Chaptarr.Core.Test.Common;
using Chaptarr.Core.Test.Queue;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Aggregation;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.DecisionEngine
{
    [TestFixture]
    public class DownloadDecisionFailureFixture
    {
        private sealed class Fts : IEditionFtsRepository
        {
            public Book Book;
            public void EnsureIndexPopulated() => RebuildIndex();
            public bool FtsTableExists() => true;
            public void RebuildIndex() => throw new NotImplementedException();
            public List<BookFtsMatch> RecallBooks(int? authorId, IEnumerable<string> tokens, BookMediaType mediaType, Action<EditionFtsTraceEvent> trace = null, int limit = 20, bool monitoredOnly = false)
                => mediaType == Book.MediaType ? new List<BookFtsMatch> { new() { BookId = Book.Id, AuthorId = Book.AuthorId, BookTitle = Book.Title } } : new();
            public List<EditionFtsMatch> RankEditions(IReadOnlyCollection<BookFtsMatch> recalledBooks, IReadOnlyCollection<EditionFtsFieldQuery> queries, BookMediaType mediaType, Action<EditionFtsTraceEvent> trace = null) => throw new NotImplementedException();
        }

        private sealed class Spec : IDecisionEngineSpecification
        {
            public int Mode;
            public SpecificationPriority Priority => SpecificationPriority.Default;
            public RejectionType Type => RejectionType.Permanent;
            public Decision IsSatisfiedBy(RemoteBook remote, SearchCriteriaBase criteria)
            {
                if (remote.Release.IndexerId != 7) return Decision.Accept();
                if (Mode == 1) throw new InvalidOperationException("Infrastructure unavailable");
                if (Mode == 2) return Decision.Reject("Deliberate profile rejection");
                if (Mode == 3) throw new NotImplementedException();
                return Decision.Accept();
            }
        }

        private static (DownloadDecisionMaker Maker, Author Author, Book Book) Maker(int mode, List<CustomFormat> formats = null)
        {
            var author = new Author { Id = 38, Name = "Test Writer", AudiobookMonitored = true };
            var book = new Book
            {
                Id = 501, AuthorId = author.Id, Author = author, Title = "Paper Lanterns", MediaType = BookMediaType.Audiobook,
                AudiobookMonitored = true, AnyEditionOk = true, Editions = new List<Edition> { new() { Id = 501, BookId = 501, Title = "Paper Lanterns", Monitored = true } }
            };
            author.Books = new List<Book> { book };
            var parsing = Proxy<IParsingService>((method, args) => new RemoteBook { Author = author, Books = new List<Book> { book }, ParsedBookInfo = (ParsedBookInfo)args[0] });
            var books = Proxy<IBookService>((method, args) => new List<Book> { book });
            var config = Proxy<IConfigService>((method, args) => method.Name == "get_BookMatchingStrictness" ? BookMatchingStrictness.Balanced : method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null);
            var aggregation = Proxy<IRemoteBookAggregationService>((method, args) =>
            {
                if (mode == 0 && ((RemoteBook)args[0]).Release.IndexerId == 7) throw new InvalidOperationException("Infrastructure unavailable");
                return null;
            });
            var maker = new DownloadDecisionMaker(new[] { new Spec { Mode = mode } }, parsing, books, new Fts { Book = book },
                Proxy<ICustomFormatCalculationService>((method, args) => formats ?? new List<CustomFormat>()), aggregation,
                Proxy<IReleaseNarratorMetadataEnricher>((method, args) => Array.Empty<ReleaseInfo>()), config, LogManager.GetCurrentClassLogger());
            return (maker, author, book);
        }

        private static ReleaseInfo Report(int indexerId) => new() { Title = "Test Writer - Paper Lanterns [M4B]", Author = "Test Writer", Book = "Paper Lanterns", IndexerId = indexerId, Indexer = "Test Indexer", PublishDate = new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc) };

        [TestCase(false, 0)]
        [TestCase(false, 1)]
        [TestCase(true, 0)]
        [TestCase(true, 1)]
        public void rss_processing_and_spec_failures_should_retain_pending_rows_and_continue_neighbors(bool postgres, int mode)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var (maker, author, book) = Maker(mode);
                var native = new PendingReleaseRepository(database, Proxy<IEventAggregator>((method, args) => null));
                var pending = QueuePendingActionsFixture.Insert(native, author, Report(7).Title, PendingReleaseReason.Delay, book.Id);
                pending.Release = Report(7);
                native.Update(pending);
                var decisions = maker.GetRssDecision(new List<ReleaseInfo> { Report(7), Report(8) });
                var service = QueuePendingActionsFixture.Service(database, author, new List<Book> { book }, new QueuePendingActionsFixture.Observed());
                service.Handle(new RssSyncCompleteEvent(new ProcessedDecisions(new(), new(), decisions.Where(decision => decision.Rejected).ToList())));
                Assert.Multiple(() =>
                {
                    Assert.That(decisions.Where(decision => decision.RemoteBook.Release.IndexerId == 7), Is.Empty);
                    Assert.That(decisions.Single(decision => decision.RemoteBook.Release.IndexerId == 8).Approved, Is.True);
                    Assert.That(native.All().Select(row => row.Id), Is.EqualTo(new[] { pending.Id }));
                });
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void deliberate_rejections_should_still_remove_pending_rows(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var (maker, author, book) = Maker(2);
                var native = new PendingReleaseRepository(database, Proxy<IEventAggregator>((method, args) => null));
                var pending = QueuePendingActionsFixture.Insert(native, author, Report(7).Title, PendingReleaseReason.Delay, book.Id);
                pending.Release = Report(7);
                native.Update(pending);
                var decisions = maker.GetRssDecision(new List<ReleaseInfo> { Report(7) });
                QueuePendingActionsFixture.Service(database, author, new List<Book> { book }, new QueuePendingActionsFixture.Observed())
                    .Handle(new RssSyncCompleteEvent(new ProcessedDecisions(new(), new(), decisions.Where(decision => decision.Rejected).ToList())));
                Assert.That(decisions.Single().Rejected, Is.True);
                Assert.That(native.All(), Is.Empty);
            });
        }

        [TestCase(0)]
        [TestCase(1)]
        public void search_infrastructure_errors_should_be_hard_and_nonbypassable(int mode)
        {
            var (maker, author, book) = Maker(mode);
            var criteria = new BookSearchCriteria { Author = author, Books = new List<Book> { book }, AuthorCatalog = new List<Book> { book }, InteractiveSearch = true };
            var decision = maker.GetSearchDecision(new List<ReleaseInfo> { Report(7), Report(8) }, criteria);
            Assert.Multiple(() =>
            {
                Assert.That(decision.Single(item => item.RemoteBook.Release.IndexerId == 7).Rejected, Is.True);
                Assert.That(decision.Single(item => item.RemoteBook.Release.IndexerId == 7).Rejections.All(item => !item.IsSoftFilter && !item.CanBypass && item.Category == "Error"), Is.True);
                Assert.That(decision.Single(item => item.RemoteBook.Release.IndexerId == 8).Approved, Is.True);
            });
        }

        [Test]
        public void unimplemented_specs_should_preserve_existing_skip_behavior()
        {
            Assert.That(Maker(3).Maker.GetRssDecision(new List<ReleaseInfo> { Report(7) }).Single().Approved, Is.True);
        }

        [TestCase(BookMediaType.Audiobook, 15)]
        [TestCase(BookMediaType.Ebook, 50)]
        public void scoring_should_use_the_proven_target_profile_with_unknown_quality(BookMediaType mediaType, int score)
        {
            var format = new CustomFormat { Id = 1, Name = "Test format" };
            var (maker, author, book) = Maker(4, new List<CustomFormat> { format });
            author.AudiobookQualityProfileId = 1;
            author.EbookQualityProfileId = 2;
            author.AudiobookQualityProfile = new QualityProfile { ProfileType = ProfileType.Audiobook, FormatItems = new() { new ProfileFormatItem { Format = format, Score = 15 } } };
            author.EbookQualityProfile = new QualityProfile { ProfileType = ProfileType.Ebook, FormatItems = new() { new ProfileFormatItem { Format = format, Score = 50 } } };
            book.MediaType = mediaType;
            var remote = new RemoteBook { Author = author, Books = new List<Book> { book }, Release = Report(8), ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(Quality.Unknown) } };
            typeof(DownloadDecisionMaker).GetMethod("ScoreAndDecide", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(maker, new object[] { remote, null, new ReleaseTitleMatchScorer.CatalogPreparation() });
            Assert.That(remote.CustomFormatScore, Is.EqualTo(score));
        }
    }
}
