using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Exceptions;
using NzbDrone.Core.Indexers.Newznab;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Indexers
{
    public class IndexerSearchOutcomeFixture
    {
        [TestCase("key")]
        [TestCase("timeout")]
        [TestCase("connection")]
        public async Task swallowed_fetch_errors_are_counted_for_this_search_only(string reason)
        {
            var criteria = new BookSearchCriteria();
            var indexer = new Probe(1, 1, _ => throw reason switch
            {
                "key" => new ApiKeyException("Bad API key"),
                "timeout" => new TaskCanceledException("Request timed out"),
                _ => new WebException("Not reachable", WebExceptionStatus.ConnectFailure)
            });
            Assert.That(await indexer.Fetch(criteria), Is.Empty);
            Assert.That(criteria.IndexerStatistics.Queried, Is.EqualTo(1));
            Assert.That(criteria.IndexerStatistics.Failed, Is.EqualTo(1));

            var nextSearch = new BookSearchCriteria();
            Assert.That(await new Probe(1, 3, _ => new List<ReleaseInfo>()).Fetch(nextSearch), Is.Empty);
            Assert.That(nextSearch.IndexerStatistics.Queried, Is.EqualTo(1), "Count indexers, not query tiers/pages");
            Assert.That(nextSearch.IndexerStatistics.Failed, Is.Zero, "An earlier failure must not leak to another search");
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task concurrent_indexers_distinguish_all_failed_from_partial_and_keep_results(bool allFailed)
        {
            var criteria = new BookSearchCriteria();
            var first = new Probe(1, 1, _ => throw new ApiKeyException("Invalid key"));
            var second = new Probe(2, 1, _ => allFailed ? throw new TaskCanceledException() : new List<ReleaseInfo>());
            var skipped = new Probe(3, 0, _ => throw new AssertionException("No query should run"));
            var results = await Task.WhenAll(first.Fetch(criteria), second.Fetch(criteria), skipped.Fetch(criteria));
            Assert.That(results.SelectMany(r => r), Is.Empty);
            Assert.That(criteria.IndexerStatistics.Queried, Is.EqualTo(2));
            Assert.That(criteria.IndexerStatistics.Failed, Is.EqualTo(allFailed ? 2 : 1));
        }

        [Test]
        public async Task retained_results_survive_a_later_query_failure()
        {
            var criteria = new BookSearchCriteria();
            var indexer = new Probe(1, 2, call => call == 2 ? throw new ApiKeyException("Rejected") : new List<ReleaseInfo>
            {
                new() { Guid = "retained", Title = "Terry Pratchett - Mort", DownloadUrl = "https://indexer.example/download" }
            });
            Assert.That((await indexer.Fetch(criteria)).Select(r => r.Guid), Is.EqualTo(new[] { "retained" }));
            Assert.That(criteria.IndexerStatistics.Failed, Is.EqualTo(1));
        }

        private sealed class Probe : HttpIndexerBase<NewznabSettings>
        {
            private readonly int _queries;
            private readonly Func<int, IList<ReleaseInfo>> _response;
            private int _calls;
            public override string Name => "Outcome probe";
            public override DownloadProtocol Protocol => DownloadProtocol.Usenet;

            public Probe(int id, int queries, Func<int, IList<ReleaseInfo>> response)
                : base(null, Proxy<IIndexerStatusService>((_, _) => null), null, null, LogManager.GetCurrentClassLogger())
            {
                _queries = queries;
                _response = response;
                Definition = new IndexerDefinition { Id = id, Name = Name, Settings = new NewznabSettings() };
            }

            public override IIndexerRequestGenerator GetRequestGenerator() => Proxy<IIndexerRequestGenerator>((_, _) =>
            {
                var chain = new IndexerPageableRequestChain();
                for (var i = 0; i < _queries; i++) chain.Add(new[] { new IndexerRequest("https://indexer.example/search", null) });
                return chain;
            });
            public override IParseIndexerResponse GetParser() => null;
            protected override Task<IList<ReleaseInfo>> FetchPage(IndexerRequest request, IParseIndexerResponse parser) => Task.FromResult(_response(++_calls));
        }
    }
}
