using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentValidation.Results;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.ThingiProvider;

namespace Chaptarr.Core.Test.DecisionEngine
{
    [TestFixture]
    public class MyAnonaMouseVipAccessSpecificationFixture
    {
        [Test]
        public void should_reject_mam_vip_only_release_when_user_is_not_vip()
        {
            var definition = new IndexerDefinition
            {
                Id = 12,
                Name = "MAM",
                Enable = true,
                Implementation = "MyAnonaMouse",
                Settings = new MyAnonaMouseSettings { IsVip = false }
            };

            var spec = new MyAnonaMouseVipAccessSpecification(new StubIndexerFactory(definition), LogManager.GetCurrentClassLogger());
            var remoteBook = new RemoteBook
            {
                Release = new ReleaseInfo
                {
                    Title = "VIP-only release",
                    Indexer = "MAM",
                    IndexerId = 12,
                    IndexerFlags = IndexerFlags.VipExclusive
                }
            };

            var decision = spec.IsSatisfiedBy(remoteBook, null);

            Assert.That(decision.Accepted, Is.False);
            Assert.That(decision.Reason, Is.EqualTo("MAM VIP-only torrent requires VIP membership"));
        }

        [Test]
        public void should_accept_mam_vip_only_release_when_user_is_vip()
        {
            var definition = new IndexerDefinition
            {
                Id = 12,
                Name = "MAM",
                Enable = true,
                Implementation = "MyAnonaMouse",
                Settings = new MyAnonaMouseSettings { IsVip = true }
            };

            var spec = new MyAnonaMouseVipAccessSpecification(new StubIndexerFactory(definition), LogManager.GetCurrentClassLogger());
            var remoteBook = new RemoteBook
            {
                Release = new ReleaseInfo
                {
                    Title = "VIP-only release",
                    Indexer = "MAM",
                    IndexerId = 12,
                    IndexerFlags = IndexerFlags.VipExclusive
                }
            };

            var decision = spec.IsSatisfiedBy(remoteBook, null);

            Assert.That(decision.Accepted, Is.True);
        }

        [Test]
        public void unsatisfied_slot_pause_should_be_a_temporary_decision_for_rss_and_search_pending_flow()
        {
            var spec = new MyAnonaMouseUnsatisfiedSlotSpecification(new RejectingSlotGuard(), LogManager.GetCurrentClassLogger());
            var remoteBook = new RemoteBook
            {
                Release = new ReleaseInfo { Indexer = "MAM", Guid = "MAM-659145" }
            };

            var decision = spec.IsSatisfiedBy(remoteBook, null);

            Assert.Multiple(() =>
            {
                Assert.That(decision.Accepted, Is.False);
                Assert.That(decision.Reason, Does.Contain("unsatisfied slot"));
                Assert.That(spec.Type, Is.EqualTo(RejectionType.Temporary));
            });
        }

        private sealed class RejectingSlotGuard : IMamUnsatisfiedSlotGuard
        {
            public MamUnsatisfiedSlotAvailability Check(RemoteBook remoteBook) => MamUnsatisfiedSlotAvailability.Reject("MAM has no safely available unsatisfied slot");
            public MamUnsatisfiedSlotAvailability TryReserve(RemoteBook remoteBook) => Check(remoteBook);
            public void Reconcile(MyAnonaMouse indexer, MyAnonaMouseAccountStatus status)
            {
            }
        }

        private sealed class StubIndexerFactory : IIndexerFactory
        {
            private readonly IndexerDefinition _definition;

            public StubIndexerFactory(IndexerDefinition definition)
            {
                _definition = definition;
            }

            public List<IndexerDefinition> All() => new List<IndexerDefinition> { _definition };
            public List<IIndexer> GetAvailableProviders() => new List<IIndexer> { new StubIndexer(_definition) };
            public bool Exists(int id) => id == _definition.Id;
            public IndexerDefinition Find(int id) => id == _definition.Id ? _definition : null;
            public IndexerDefinition Get(int id) => Find(id);
            public IEnumerable<IndexerDefinition> Get(IEnumerable<int> ids) => ids.Select(Find).Where(d => d != null);
            public IndexerDefinition Create(IndexerDefinition definition) => throw new NotImplementedException();
            public void Update(IndexerDefinition definition) => throw new NotImplementedException();
            public IEnumerable<IndexerDefinition> Update(IEnumerable<IndexerDefinition> definitions) => throw new NotImplementedException();
            public void Delete(int id) => throw new NotImplementedException();
            public void Delete(IEnumerable<int> ids) => throw new NotImplementedException();
            public IEnumerable<IndexerDefinition> GetDefaultDefinitions() => Enumerable.Empty<IndexerDefinition>();
            public IEnumerable<IndexerDefinition> GetPresetDefinitions(IndexerDefinition providerDefinition) => Enumerable.Empty<IndexerDefinition>();
            public void SetProviderCharacteristics(IndexerDefinition definition) => throw new NotImplementedException();
            public void SetProviderCharacteristics(IIndexer provider, IndexerDefinition definition) => throw new NotImplementedException();
            public IIndexer GetInstance(IndexerDefinition definition) => new StubIndexer(definition);
            public ValidationResult Test(IndexerDefinition definition) => new ValidationResult();
            public object RequestAction(IndexerDefinition definition, string action, IDictionary<string, string> query) => null;
            public List<IndexerDefinition> AllForTag(int tagId) => new List<IndexerDefinition>();
            public List<IIndexer> RssEnabled(bool filterBlockedIndexers = true) => new List<IIndexer>();
            public List<IIndexer> AutomaticSearchEnabled(bool filterBlockedIndexers = true) => new List<IIndexer>();
            public List<IIndexer> InteractiveSearchEnabled(bool filterBlockedIndexers = true) => new List<IIndexer>();
        }

        private sealed class StubIndexer : IIndexer
        {
            public StubIndexer(IndexerDefinition definition)
            {
                Definition = definition;
            }

            public string Name => "MAM";
            public Type ConfigContract => typeof(MyAnonaMouseSettings);
            public ProviderMessage Message => null;
            public IEnumerable<ProviderDefinition> DefaultDefinitions => Enumerable.Empty<ProviderDefinition>();
            public ProviderDefinition Definition { get; set; }
            public ValidationResult Test() => new ValidationResult();
            public object RequestAction(string stage, IDictionary<string, string> query) => null;
            public bool SupportsRss => true;
            public bool SupportsSearch => true;
            public DownloadProtocol Protocol => DownloadProtocol.Torrent;
            public Task<IList<ReleaseInfo>> FetchRecent() => throw new NotImplementedException();
            public Task<IList<ReleaseInfo>> Fetch(BookSearchCriteria searchCriteria) => throw new NotImplementedException();
            public Task<IList<ReleaseInfo>> Fetch(AuthorSearchCriteria searchCriteria) => throw new NotImplementedException();
            public HttpRequest GetDownloadRequest(string link) => new HttpRequest(link);
            public Task<HttpResponse> ExecuteDownloadRequestAsync(HttpRequest request) => Task.FromResult(new HttpResponse(request, new HttpHeader(), string.Empty));
        }
    }
}
