using NLog;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Parser;

namespace NzbDrone.Core.Indexers.TorrentRss
{
    public class TorrentRssIndexer : HttpIndexerBase<TorrentRssIndexerSettings>
    {
        public override string Name => "Torrent RSS Feed";

        public override DownloadProtocol Protocol => DownloadProtocol.Torrent;
        public override bool SupportsSearch => false;
        public override int PageSize => 0;

        private readonly ITorrentRssParserFactory _torrentRssParserFactory;

        public TorrentRssIndexer(ITorrentRssParserFactory torrentRssParserFactory, IIndexerHttpClientFactory httpClientFactory, IIndexerStatusService indexerStatusService, IConfigService configService, IParsingService parsingService, Logger logger)
            : base(httpClientFactory, indexerStatusService, configService, parsingService, logger)
        {
            _torrentRssParserFactory = torrentRssParserFactory;
        }

        public override IIndexerRequestGenerator GetRequestGenerator()
        {
            return new TorrentRssIndexerRequestGenerator { Settings = Settings };
        }

        public override IParseIndexerResponse GetParser()
        {
            var definition = (IndexerDefinition)Definition;
            return _torrentRssParserFactory.GetParser(Settings, definition?.ProxyId, definition?.Id > 0 ? definition.Id : null);
        }
    }
}
