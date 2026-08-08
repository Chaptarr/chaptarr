using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Download.Clients.Direct;
using NzbDrone.Core.Indexers;

namespace NzbDrone.Core.Download
{
    public interface IProvideDownloadClient
    {
        IDownloadClient GetDownloadClient(DownloadProtocol downloadProtocol, BookMediaType mediaType, int indexerId = 0, bool filterBlockedClients = false, HashSet<int> tags = null);
        IEnumerable<IDownloadClient> GetDownloadClients(bool filterBlockedClients = false);
        IDownloadClient Get(int id);
    }

    public class DownloadClientProvider : IProvideDownloadClient
    {
        private readonly Logger _logger;
        private readonly IDownloadClientFactory _downloadClientFactory;
        private readonly IDownloadClientStatusService _downloadClientStatusService;
        private readonly IIndexerFactory _indexerFactory;
        private readonly IInternalDirectClientProvider _internalDirectClientProvider;
        private readonly ICached<int> _lastUsedDownloadClient;

        public DownloadClientProvider(IDownloadClientStatusService downloadClientStatusService,
                                      IDownloadClientFactory downloadClientFactory,
                                      IIndexerFactory indexerFactory,
                                      IInternalDirectClientProvider internalDirectClientProvider,
                                      ICacheManager cacheManager,
                                      Logger logger)
        {
            _logger = logger;
            _downloadClientFactory = downloadClientFactory;
            _downloadClientStatusService = downloadClientStatusService;
            _indexerFactory = indexerFactory;
            _internalDirectClientProvider = internalDirectClientProvider;
            _lastUsedDownloadClient = cacheManager.GetCache<int>(GetType(), "lastDownloadClientId");
        }

        private static HashSet<int> GetEffectiveTags(DownloadClientDefinition definition, BookMediaType mediaType)
        {
            return mediaType == BookMediaType.Ebook
                ? definition.EbookTags ?? new HashSet<int>()
                : definition.AudiobookTags ?? new HashSet<int>();
        }

        public IDownloadClient GetDownloadClient(DownloadProtocol downloadProtocol, BookMediaType mediaType, int indexerId = 0, bool filterBlockedClients = false, HashSet<int> tags = null)
        {
            var blockedProviders = new HashSet<int>(_downloadClientStatusService.GetBlockedProviders().Select(v => v.ProviderId));
            var enabledProviders = _downloadClientFactory.GetAvailableProviders().ToList();
            var availableProviders = enabledProviders.Where(v => v.Protocol == downloadProtocol).ToList();

            if (!availableProviders.Any())
            {
                if (downloadProtocol == DownloadProtocol.Direct)
                {
                    _logger.Debug("No user-configured Direct download client found; using internal Direct client.");
                    return _internalDirectClientProvider.GetClient();
                }

                throw new DownloadClientUnavailableException(BuildNoMatchingProtocolMessage(downloadProtocol, enabledProviders));
            }

            if (tags is { Count: > 0 })
            {
                var matchingTagsClients = availableProviders
                    .Where(i =>
                    {
                        var definition = i.Definition as DownloadClientDefinition;
                        if (definition == null)
                        {
                            return false;
                        }

                        return GetEffectiveTags(definition, mediaType).Intersect(tags).Any();
                    })
                    .ToList();

                availableProviders = matchingTagsClients.Count > 0 ?
                    matchingTagsClients :
                    availableProviders.Where(i =>
                    {
                        var definition = i.Definition as DownloadClientDefinition;
                        if (definition == null)
                        {
                            return false;
                        }

                        return GetEffectiveTags(definition, mediaType).Count == 0;
                    }).ToList();

                if (!availableProviders.Any())
                {
                    throw new DownloadClientUnavailableException("No download client was found without tags or a matching author tag. Please check your settings.");
                }
            }

            if (indexerId > 0)
            {
                var indexer = _indexerFactory.Find(indexerId);

                if (indexer is { DownloadClientId: > 0 })
                {
                    var client = availableProviders.SingleOrDefault(d => d.Definition.Id == indexer.DownloadClientId);

                    if (client == null)
                    {
                        throw new DownloadClientUnavailableException($"Indexer specified download client does not exist for {indexer.Name}");
                    }

                    if (filterBlockedClients && blockedProviders.Contains(client.Definition.Id))
                    {
                        throw new DownloadClientUnavailableException($"Indexer specified download client is not available due to recent failures for {indexer.Name}");
                    }

                    return client;
                }
            }

            if (blockedProviders.Any())
            {
                var nonBlockedProviders = availableProviders.Where(v => !blockedProviders.Contains(v.Definition.Id)).ToList();

                if (nonBlockedProviders.Any())
                {
                    availableProviders = nonBlockedProviders;
                }
                else if (filterBlockedClients)
                {
                    throw new DownloadClientUnavailableException($"All download clients for {downloadProtocol} are not available");
                }
                else
                {
                    _logger.Trace("No non-blocked Download Client available, retrying blocked one.");
                }
            }

            // Use the first priority clients first
            availableProviders = availableProviders.GroupBy(v => (v.Definition as DownloadClientDefinition).Priority)
                                                   .OrderBy(v => v.Key)
                                                   .First().OrderBy(v => v.Definition.Id).ToList();

            var lastId = _lastUsedDownloadClient.Find(downloadProtocol.ToString());

            var provider = availableProviders.FirstOrDefault(v => v.Definition.Id > lastId) ?? availableProviders.First();

            _lastUsedDownloadClient.Set(downloadProtocol.ToString(), provider.Definition.Id);

            return provider;
        }

        private static string BuildNoMatchingProtocolMessage(DownloadProtocol requestedProtocol, List<IDownloadClient> enabledProviders)
        {
            var requestedProtocolLabel = requestedProtocol.ToString().ToLowerInvariant();
            var alternateClientsMessage = string.Concat(Enum
                .GetValues(typeof(DownloadProtocol))
                .Cast<DownloadProtocol>()
                .Where(protocol => protocol != DownloadProtocol.Unknown && protocol != requestedProtocol)
                .Select(protocol => new
                {
                    ProtocolLabel = protocol.ToString().ToLowerInvariant(),
                    Count = enabledProviders.Count(v => v.Protocol == protocol)
                })
                .Where(result => result.Count > 0)
                .Select(result => $" Found {result.Count} enabled {result.ProtocolLabel} client(s)."));

            return $"No enabled {requestedProtocolLabel} download client is configured.{alternateClientsMessage} Add and enable a {requestedProtocolLabel}-capable download client, then retry.";
        }

        public IEnumerable<IDownloadClient> GetDownloadClients(bool filterBlockedClients = false)
        {
            var enabledClients = _downloadClientFactory.GetAvailableProviders().ToList();

            if (!enabledClients.Any(c => c.Protocol == DownloadProtocol.Direct))
            {
                var internalClient = _internalDirectClientProvider.GetClient();
                if (internalClient != null)
                {
                    enabledClients.Add(internalClient);
                }
            }

            if (filterBlockedClients)
            {
                return FilterBlockedDownloadClients(enabledClients).ToList();
            }

            return enabledClients;
        }

        public IDownloadClient Get(int id)
        {
            if (id == -1)
            {
                return _internalDirectClientProvider.GetClient()
                       ?? throw new DownloadClientUnavailableException("Internal Direct download client is not available.");
            }

            return _downloadClientFactory.GetAvailableProviders().Single(d => d.Definition.Id == id);
        }

        private IEnumerable<IDownloadClient> FilterBlockedDownloadClients(IEnumerable<IDownloadClient> clients)
        {
            var blockedClients = _downloadClientStatusService.GetBlockedProviders().ToDictionary(v => v.ProviderId, v => v);

            foreach (var client in clients)
            {
                if (blockedClients.TryGetValue(client.Definition.Id, out var blockedClientStatus))
                {
                    _logger.Debug("Temporarily ignoring client {0} till {1} due to recent failures.", client.Definition.Name, blockedClientStatus.DisabledTill.Value.ToLocalTime());
                    continue;
                }

                yield return client;
            }
        }
    }
}
