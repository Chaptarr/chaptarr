using System;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.TorrentRss;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture]
    public class TorrentRssProxyRoutingFixture
    {
        private class IndexerHttpClientProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IIndexerHttpClient.Execute))
                {
                    throw new WebException("stop after routing assertion");
                }

                throw new NotImplementedException(targetMethod?.Name);
            }
        }

        private class IndexerHttpClientFactoryProxy : DispatchProxy
        {
            public int? RequestedProxyId { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IIndexerHttpClientFactory.GetClient))
                {
                    RequestedProxyId = (int?)args[0];
                    return DispatchProxy.Create<IIndexerHttpClient, IndexerHttpClientProxy>();
                }

                throw new NotImplementedException(targetMethod?.Name);
            }
        }

        private class DetectorProxy : DispatchProxy
        {
            public List<int?> ProxyIds { get; } = new List<int?>();
            public List<int?> IndexerIds { get; } = new List<int?>();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(ITorrentRssSettingsDetector.Detect))
                {
                    ProxyIds.Add((int?)args[1]);
                    IndexerIds.Add((int?)args[2]);
                    return new TorrentRssIndexerParserSettings();
                }

                throw new NotImplementedException(targetMethod?.Name);
            }
        }

        [Test]
        public void detector_should_use_the_indexers_selected_proxy()
        {
            var factory = DispatchProxy.Create<IIndexerHttpClientFactory, IndexerHttpClientFactoryProxy>();
            var detector = new TorrentRssSettingsDetector(factory, LogManager.GetCurrentClassLogger());

            detector.Detect(new TorrentRssIndexerSettings { BaseUrl = "https://feed.example/rss" }, 17, 42);

            Assert.That(((IndexerHttpClientFactoryProxy)(object)factory).RequestedProxyId, Is.EqualTo(17));
        }

        [Test]
        public void parser_detection_cache_should_not_cross_proxy_assignments()
        {
            var detector = DispatchProxy.Create<ITorrentRssSettingsDetector, DetectorProxy>();
            var factory = new TorrentRssParserFactory(new CacheManager(), detector, LogManager.GetCurrentClassLogger());
            var settings = new TorrentRssIndexerSettings { BaseUrl = "https://feed.example/rss" };

            factory.GetParser(settings, 17, 42);
            factory.GetParser(settings, 18, 42);
            factory.GetParser(settings, 17, 42);

            Assert.That(((DetectorProxy)(object)detector).ProxyIds, Is.EqualTo(new int?[] { 17, 18 }));
            Assert.That(((DetectorProxy)(object)detector).IndexerIds, Is.EqualTo(new int?[] { 42, 42 }));
        }

        [Test]
        public void parser_detection_cache_should_not_cross_indexers()
        {
            var detector = DispatchProxy.Create<ITorrentRssSettingsDetector, DetectorProxy>();
            var factory = new TorrentRssParserFactory(new CacheManager(), detector, LogManager.GetCurrentClassLogger());
            var settings = new TorrentRssIndexerSettings { BaseUrl = "https://feed.example/rss" };

            factory.GetParser(settings, 17, 42);
            factory.GetParser(settings, 17, 43);
            factory.GetParser(settings, 17, 42);

            Assert.That(((DetectorProxy)(object)detector).IndexerIds, Is.EqualTo(new int?[] { 42, 43 }));
        }
    }
}
