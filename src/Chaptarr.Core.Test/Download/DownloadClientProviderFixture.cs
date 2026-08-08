using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentValidation.Results;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Download.Clients.Direct;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.ThingiProvider.Status;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class DownloadClientProviderFixture
    {
        private sealed class StubDownloadClient : IDownloadClient
        {
            public StubDownloadClient(string implementationName, string configuredName, DownloadProtocol protocol)
            {
                Name = implementationName;
                Protocol = protocol;
                Definition = new DownloadClientDefinition
                {
                    Id = implementationName.GetHashCode(),
                    Name = configuredName,
                    ImplementationName = implementationName,
                    Protocol = protocol
                };
            }

            public string Name { get; }
            public Type ConfigContract => typeof(NullConfig);
            public ProviderMessage Message => null;
            public IEnumerable<ProviderDefinition> DefaultDefinitions => Enumerable.Empty<ProviderDefinition>();
            public ProviderDefinition Definition { get; set; }
            public DownloadProtocol Protocol { get; }

            public ValidationResult Test() => new();
            public object RequestAction(string action, IDictionary<string, string> query) => null;
            public Task<string> Download(RemoteBook remoteBook, IIndexer indexer) => throw new NotImplementedException();
            public IEnumerable<DownloadClientItem> GetItems() => Enumerable.Empty<DownloadClientItem>();
            public DownloadClientItem GetImportItem(DownloadClientItem item, DownloadClientItem previousImportAttempt) => null;
            public void RemoveItem(DownloadClientItem item, bool deleteData) { }
            public DownloadClientInfo GetStatus() => new();
            public void MarkItemAsImported(DownloadClientItem downloadClientItem) { }
        }

        private sealed class StubDownloadClientFactory : IDownloadClientFactory
        {
            private readonly List<IDownloadClient> _clients;

            public StubDownloadClientFactory(IEnumerable<IDownloadClient> clients)
            {
                _clients = clients.ToList();
            }

            public List<IDownloadClient> GetAvailableProviders() => _clients;
            public List<DownloadClientDefinition> All() => _clients.Select(c => (DownloadClientDefinition)c.Definition).ToList();
            public List<IDownloadClient> DownloadHandlingEnabled(bool filterBlockedClients = true) => _clients;
            public bool Exists(int id) => _clients.Any(c => c.Definition.Id == id);
            public DownloadClientDefinition Find(int id) => All().SingleOrDefault(d => d.Id == id);
            public DownloadClientDefinition Get(int id) => All().Single(d => d.Id == id);
            public IEnumerable<DownloadClientDefinition> Get(IEnumerable<int> ids) => All().Where(d => ids.Contains(d.Id));
            public DownloadClientDefinition Create(DownloadClientDefinition definition) => throw new NotImplementedException();
            public void Update(DownloadClientDefinition definition) => throw new NotImplementedException();
            public IEnumerable<DownloadClientDefinition> Update(IEnumerable<DownloadClientDefinition> definitions) => throw new NotImplementedException();
            public void Delete(int id) => throw new NotImplementedException();
            public void Delete(IEnumerable<int> ids) => throw new NotImplementedException();
            public IEnumerable<DownloadClientDefinition> GetDefaultDefinitions() => Enumerable.Empty<DownloadClientDefinition>();
            public IEnumerable<DownloadClientDefinition> GetPresetDefinitions(DownloadClientDefinition providerDefinition) => Enumerable.Empty<DownloadClientDefinition>();
            public void SetProviderCharacteristics(DownloadClientDefinition definition) => throw new NotImplementedException();
            public void SetProviderCharacteristics(IDownloadClient provider, DownloadClientDefinition definition) => throw new NotImplementedException();
            public IDownloadClient GetInstance(DownloadClientDefinition definition) => _clients.Single(c => c.Definition.Id == definition.Id);
            public ValidationResult Test(DownloadClientDefinition definition) => new();
            public object RequestAction(DownloadClientDefinition definition, string action, IDictionary<string, string> query) => null;
            public List<DownloadClientDefinition> AllForTag(int tagId) => new();
        }

        private sealed class StubDownloadClientStatusService : IDownloadClientStatusService
        {
            public List<DownloadClientStatus> GetBlockedProviders() => new();
            public void RecordSuccess(int providerId) { }
            public void RecordFailure(int providerId, TimeSpan minimumBackOff = default) { }
            public void RecordConnectionFailure(int providerId) { }
        }

        private sealed class StubIndexerFactory : IIndexerFactory
        {
            public List<IndexerDefinition> All() => new();
            public List<IIndexer> GetAvailableProviders() => new();
            public bool Exists(int id) => false;
            public IndexerDefinition Find(int id) => null;
            public IndexerDefinition Get(int id) => null;
            public IEnumerable<IndexerDefinition> Get(IEnumerable<int> ids) => Enumerable.Empty<IndexerDefinition>();
            public IndexerDefinition Create(IndexerDefinition definition) => throw new NotImplementedException();
            public void Update(IndexerDefinition definition) => throw new NotImplementedException();
            public IEnumerable<IndexerDefinition> Update(IEnumerable<IndexerDefinition> definitions) => throw new NotImplementedException();
            public void Delete(int id) => throw new NotImplementedException();
            public void Delete(IEnumerable<int> ids) => throw new NotImplementedException();
            public IEnumerable<IndexerDefinition> GetDefaultDefinitions() => Enumerable.Empty<IndexerDefinition>();
            public IEnumerable<IndexerDefinition> GetPresetDefinitions(IndexerDefinition providerDefinition) => Enumerable.Empty<IndexerDefinition>();
            public void SetProviderCharacteristics(IndexerDefinition definition) => throw new NotImplementedException();
            public void SetProviderCharacteristics(IIndexer provider, IndexerDefinition definition) => throw new NotImplementedException();
            public IIndexer GetInstance(IndexerDefinition definition) => throw new NotImplementedException();
            public ValidationResult Test(IndexerDefinition definition) => new();
            public object RequestAction(IndexerDefinition definition, string action, IDictionary<string, string> query) => null;
            public List<IndexerDefinition> AllForTag(int tagId) => new();
            public List<IIndexer> RssEnabled(bool filterBlockedIndexers = true) => new();
            public List<IIndexer> AutomaticSearchEnabled(bool filterBlockedIndexers = true) => new();
            public List<IIndexer> InteractiveSearchEnabled(bool filterBlockedIndexers = true) => new();
        }

        private sealed class StubInternalDirectClientProvider : IInternalDirectClientProvider
        {
            private readonly IDownloadClient _client;

            public StubInternalDirectClientProvider(IDownloadClient client)
            {
                _client = client;
            }

            public IDownloadClient GetClient() => _client;
        }

        private static IInternalDirectClientProvider NoInternalDirectClient()
        {
            return new StubInternalDirectClientProvider(null);
        }

        private static IInternalDirectClientProvider InternalDirectClientReturning(IDownloadClient client)
        {
            return new StubInternalDirectClientProvider(client);
        }

        [Test]
        public void should_not_include_user_defined_alternate_client_names_in_protocol_mismatch_message()
        {
            var provider = new DownloadClientProvider(
                new StubDownloadClientStatusService(),
                new StubDownloadClientFactory(new IDownloadClient[]
                {
                    new StubDownloadClient("Download Station", "Office qBittorrent", DownloadProtocol.Torrent),
                    new StubDownloadClient("qBittorrent", "Seedbox", DownloadProtocol.Torrent)
                }),
                new StubIndexerFactory(),
                NoInternalDirectClient(),
                new CacheManager(),
                LogManager.GetCurrentClassLogger());

            var exception = Assert.Throws<DownloadClientUnavailableException>(() =>
                provider.GetDownloadClient(DownloadProtocol.Usenet, BookMediaType.Audiobook));

            Assert.That(exception.Message, Does.Contain("Found 2 enabled torrent client(s)."));
            Assert.That(exception.Message, Does.Not.Contain("Office qBittorrent"));
            Assert.That(exception.Message, Does.Not.Contain("Seedbox"));
        }

        [Test]
        public void should_use_plain_message_when_no_alternate_protocol_clients_exist()
        {
            var provider = new DownloadClientProvider(
                new StubDownloadClientStatusService(),
                new StubDownloadClientFactory(Array.Empty<IDownloadClient>()),
                new StubIndexerFactory(),
                NoInternalDirectClient(),
                new CacheManager(),
                LogManager.GetCurrentClassLogger());

            var exception = Assert.Throws<DownloadClientUnavailableException>(() =>
                provider.GetDownloadClient(DownloadProtocol.Usenet, BookMediaType.Audiobook));

            Assert.That(exception.Message, Is.EqualTo("No enabled usenet download client is configured. Add and enable a usenet-capable download client, then retry."));
        }

        [Test]
        public void should_use_internal_direct_client_when_other_protocols_configured_but_no_direct()
        {
            var internalClient = new StubDownloadClient("DirectDownloadClient", "Internal Direct", DownloadProtocol.Direct);
            var provider = new DownloadClientProvider(
                new StubDownloadClientStatusService(),
                new StubDownloadClientFactory(new IDownloadClient[]
                {
                    new StubDownloadClient("SABnzbd", "Books Usenet", DownloadProtocol.Usenet),
                    new StubDownloadClient("qBittorrent", "Books Torrent", DownloadProtocol.Torrent)
                }),
                new StubIndexerFactory(),
                InternalDirectClientReturning(internalClient),
                new CacheManager(),
                LogManager.GetCurrentClassLogger());

            var selected = provider.GetDownloadClient(DownloadProtocol.Direct, BookMediaType.Ebook);

            Assert.That(selected, Is.SameAs(internalClient));
        }

        [Test]
        public void should_assign_direct_releases_only_to_direct_clients()
        {
            var directClient = new StubDownloadClient("Direct Download Client", "Books Direct", DownloadProtocol.Direct);
            var provider = new DownloadClientProvider(
                new StubDownloadClientStatusService(),
                new StubDownloadClientFactory(new IDownloadClient[]
                {
                    new StubDownloadClient("SABnzbd", "Books Usenet", DownloadProtocol.Usenet),
                    new StubDownloadClient("qBittorrent", "Books Torrent", DownloadProtocol.Torrent),
                    directClient
                }),
                new StubIndexerFactory(),
                NoInternalDirectClient(),
                new CacheManager(),
                LogManager.GetCurrentClassLogger());

            var selected = provider.GetDownloadClient(DownloadProtocol.Direct, BookMediaType.Ebook);

            Assert.That(selected, Is.SameAs(directClient));
        }

        [Test]
        public void should_resolve_internal_direct_client_when_no_configured_direct_client_exists()
        {
            var internalClient = new StubDownloadClient("DirectDownloadClient", "Direct Download", DownloadProtocol.Direct);
            var provider = new DownloadClientProvider(
                new StubDownloadClientStatusService(),
                new StubDownloadClientFactory(Array.Empty<IDownloadClient>()),
                new StubIndexerFactory(),
                InternalDirectClientReturning(internalClient),
                new CacheManager(),
                LogManager.GetCurrentClassLogger());

            var selected = provider.GetDownloadClient(DownloadProtocol.Direct, BookMediaType.Ebook);

            Assert.That(selected, Is.SameAs(internalClient));
        }

        [Test]
        public void should_still_throw_for_usenet_when_no_clients_configured()
        {
            var provider = new DownloadClientProvider(
                new StubDownloadClientStatusService(),
                new StubDownloadClientFactory(Array.Empty<IDownloadClient>()),
                new StubIndexerFactory(),
                NoInternalDirectClient(),
                new CacheManager(),
                LogManager.GetCurrentClassLogger());

            var exception = Assert.Throws<DownloadClientUnavailableException>(() =>
                provider.GetDownloadClient(DownloadProtocol.Usenet, BookMediaType.Audiobook));

            Assert.That(exception.Message, Does.Contain("No enabled usenet download client is configured"));
        }

        [Test]
        public void should_still_throw_for_torrent_when_no_clients_configured()
        {
            var provider = new DownloadClientProvider(
                new StubDownloadClientStatusService(),
                new StubDownloadClientFactory(Array.Empty<IDownloadClient>()),
                new StubIndexerFactory(),
                NoInternalDirectClient(),
                new CacheManager(),
                LogManager.GetCurrentClassLogger());

            var exception = Assert.Throws<DownloadClientUnavailableException>(() =>
                provider.GetDownloadClient(DownloadProtocol.Torrent, BookMediaType.Ebook));

            Assert.That(exception.Message, Does.Contain("No enabled torrent download client is configured"));
        }

        [Test]
        public void should_prefer_user_configured_direct_client_over_internal()
        {
            var userDirectClient = new StubDownloadClient("DirectDownloadClient", "User Direct", DownloadProtocol.Direct);
            var internalClient = new StubDownloadClient("DirectDownloadClient", "Internal Direct", DownloadProtocol.Direct);
            var provider = new DownloadClientProvider(
                new StubDownloadClientStatusService(),
                new StubDownloadClientFactory(new IDownloadClient[]
                {
                    new StubDownloadClient("SABnzbd", "Books Usenet", DownloadProtocol.Usenet),
                    userDirectClient
                }),
                new StubIndexerFactory(),
                InternalDirectClientReturning(internalClient),
                new CacheManager(),
                LogManager.GetCurrentClassLogger());

            var selected = provider.GetDownloadClient(DownloadProtocol.Direct, BookMediaType.Ebook);

            Assert.That(selected, Is.SameAs(userDirectClient));
            Assert.That(selected, Is.Not.SameAs(internalClient));
        }
    }
}
