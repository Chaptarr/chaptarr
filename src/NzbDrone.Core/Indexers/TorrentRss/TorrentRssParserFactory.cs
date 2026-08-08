using System;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Indexers.Exceptions;

namespace NzbDrone.Core.Indexers.TorrentRss
{
    public interface ITorrentRssParserFactory
    {
        TorrentRssParser GetParser(TorrentRssIndexerSettings settings, int? proxyId, int? indexerId);
    }

    public class TorrentRssParserFactory : ITorrentRssParserFactory
    {
        protected readonly Logger _logger;

        private readonly ICached<TorrentRssIndexerParserSettings> _settingsCache;

        private readonly ITorrentRssSettingsDetector _torrentRssSettingsDetector;

        public TorrentRssParserFactory(ICacheManager cacheManager, ITorrentRssSettingsDetector torrentRssSettingsDetector, Logger logger)
        {
            _settingsCache = cacheManager.GetCache<TorrentRssIndexerParserSettings>(GetType());
            _torrentRssSettingsDetector = torrentRssSettingsDetector;
            _logger = logger;
        }

        public TorrentRssParser GetParser(TorrentRssIndexerSettings indexerSettings, int? proxyId, int? indexerId)
        {
            var key = $"{proxyId?.ToString() ?? "default"}:{indexerId?.ToString() ?? "unsaved"}:{indexerSettings.ToJson()}";
            var parserSettings = _settingsCache.Get(key, () => DetectParserSettings(indexerSettings, proxyId, indexerId), TimeSpan.FromDays(7));

            if (parserSettings.UseEZTVFormat)
            {
                return new EzrssTorrentRssParser();
            }
            else
            {
                return new TorrentRssParser
                {
                    UseGuidInfoUrl = false,
                    ParseSeedersInDescription = parserSettings.ParseSeedersInDescription,

                    UseEnclosureUrl = parserSettings.UseEnclosureUrl,
                    UseEnclosureLength = parserSettings.UseEnclosureLength,
                    ParseSizeInDescription = parserSettings.ParseSizeInDescription,
                    SizeElementName = parserSettings.SizeElementName
                };
            }
        }

        private TorrentRssIndexerParserSettings DetectParserSettings(TorrentRssIndexerSettings indexerSettings, int? proxyId, int? indexerId)
        {
            var settings = _torrentRssSettingsDetector.Detect(indexerSettings, proxyId, indexerId);

            if (settings == null)
            {
                throw new UnsupportedFeedException("Could not parse feed from {0}", indexerSettings.BaseUrl);
            }

            return settings;
        }
    }
}
