using System;
using System.Collections.Generic;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Download.Clients.QBittorrent;
using NzbDrone.Core.MediaFiles.TorrentInfo;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class QBittorrentDuplicateHandlingFixture
    {
        private class TestTorrentFileInfoReader : ITorrentFileInfoReader
        {
            public string GetHashFromTorrentFile(byte[] fileContents)
            {
                return "hash";
            }
        }

        private class TestProxy : IQBittorrentProxy
        {
            public bool TorrentLoaded { get; set; }

            public List<string> LoadedHashes { get; } = new();
            public List<(string Hash, string Label)> Labels { get; } = new();
            public List<string> AddedMagnetLinks { get; } = new();
            public List<string> AddedTorrentFiles { get; } = new();

            public bool IsApiSupported(QBittorrentSettings settings) => true;

            public Version GetApiVersion(QBittorrentSettings settings) => new Version(2, 11, 0);

            public string GetVersion(QBittorrentSettings settings) => "5.0.0";

            public QBittorrentPreferences GetConfig(QBittorrentSettings settings) => new QBittorrentPreferences { DhtEnabled = true };

            public List<QBittorrentTorrent> GetTorrents(QBittorrentSettings settings, string category = null) => throw new NotImplementedException();

            public bool IsTorrentLoaded(string hash, QBittorrentSettings settings)
            {
                LoadedHashes.Add(hash);
                return TorrentLoaded;
            }

            public QBittorrentTorrentProperties GetTorrentProperties(string hash, QBittorrentSettings settings) => throw new NotImplementedException();

            public List<QBittorrentTorrentFile> GetTorrentFiles(string hash, QBittorrentSettings settings) => throw new NotImplementedException();

            public void AddTorrentFromUrl(string torrentUrl, TorrentSeedConfiguration seedConfiguration, QBittorrentSettings settings, string category = null)
            {
                AddedMagnetLinks.Add(torrentUrl);
                throw new DownloadClientException("qBittorrent rejected request");
            }

            public void AddTorrentFromFile(string fileName, byte[] fileContent, TorrentSeedConfiguration seedConfiguration, QBittorrentSettings settings, string category = null)
            {
                AddedTorrentFiles.Add(fileName);
                throw new DownloadClientException("qBittorrent rejected request");
            }

            public void RemoveTorrent(string hash, bool removeData, QBittorrentSettings settings) => throw new NotImplementedException();

            public void SetTorrentLabel(string hash, string label, QBittorrentSettings settings)
            {
                Labels.Add((hash, label));
            }

            public void AddLabel(string label, QBittorrentSettings settings) => throw new NotImplementedException();

            public Dictionary<string, QBittorrentLabel> GetLabels(QBittorrentSettings settings) => throw new NotImplementedException();

            public void SetTorrentSeedingConfiguration(string hash, TorrentSeedConfiguration seedConfiguration, QBittorrentSettings settings) => throw new NotImplementedException();

            public void MoveTorrentToTopInQueue(string hash, QBittorrentSettings settings) => throw new NotImplementedException();

            public void SetForceStart(string hash, bool enabled, QBittorrentSettings settings) => throw new NotImplementedException();
        }

        private class TestProxySelector : IQBittorrentProxySelector
        {
            private readonly IQBittorrentProxy _proxy;
            private readonly Version _apiVersion;

            public TestProxySelector(IQBittorrentProxy proxy, Version apiVersion)
            {
                _proxy = proxy;
                _apiVersion = apiVersion;
            }

            public IQBittorrentProxy GetProxy(QBittorrentSettings settings, bool force = false) => _proxy;

            public Version GetApiVersion(QBittorrentSettings settings, bool force = false) => _apiVersion;
        }

        private class TestQBittorrent : QBittorrent
        {
            public TestQBittorrent(IQBittorrentProxySelector proxySelector, ICacheManager cacheManager, Logger logger)
                : base(proxySelector,
                    new TestTorrentFileInfoReader(),
                    httpClient: null,
                    configService: null,
                    diskProvider: null,
                    remotePathMappingService: null,
                    cacheManager: cacheManager,
                    blocklistService: null,
                    logger: logger)
            {
            }

            public string AddTorrentFile(RemoteBook remoteBook, string hash)
            {
                return base.AddFromTorrentFile(remoteBook, hash, "file.torrent", Array.Empty<byte>());
            }

            public string AddMagnet(RemoteBook remoteBook, string hash)
            {
                return base.AddFromMagnetLink(remoteBook, hash, "magnet:?xt=urn:btih:HASH&tr=https://tracker.example/");
            }
        }

        [Test]
        public void should_treat_existing_torrent_as_added_when_torrent_file_add_fails()
        {
            var proxy = new TestProxy { TorrentLoaded = true };
            var proxySelector = new TestProxySelector(proxy, new Version(2, 11, 0));
            var cacheManager = new CacheManager();

            var client = new TestQBittorrent(proxySelector, cacheManager, LogManager.GetCurrentClassLogger())
            {
                Definition = new DownloadClientDefinition
                {
                    Name = "qBittorrent",
                    Settings = new QBittorrentSettings
                    {
                        EbookCategory = "ebooks",
                        AudiobookCategory = "audiobooks"
                    }
                }
            };

            var remoteBook = new RemoteBook
            {
                Books = new List<Book> { new Book { MediaType = BookMediaType.Ebook } }
            };

            var hash = "ABCDEF1234";
            var result = client.AddTorrentFile(remoteBook, hash);

            Assert.That(result, Is.EqualTo(hash));
            Assert.That(proxy.AddedTorrentFiles, Has.Count.EqualTo(1));
            Assert.That(proxy.LoadedHashes, Is.EquivalentTo(new[] { hash.ToLower() }));
            Assert.That(proxy.Labels, Is.EquivalentTo(new[] { (hash.ToLower(), "ebooks") }));
        }

        [Test]
        public void should_treat_existing_torrent_as_added_when_magnet_add_fails()
        {
            var proxy = new TestProxy { TorrentLoaded = true };
            var proxySelector = new TestProxySelector(proxy, new Version(2, 11, 0));
            var cacheManager = new CacheManager();

            var client = new TestQBittorrent(proxySelector, cacheManager, LogManager.GetCurrentClassLogger())
            {
                Definition = new DownloadClientDefinition
                {
                    Name = "qBittorrent",
                    Settings = new QBittorrentSettings
                    {
                        EbookCategory = "ebooks",
                        AudiobookCategory = "audiobooks"
                    }
                }
            };

            var remoteBook = new RemoteBook
            {
                Books = new List<Book> { new Book { MediaType = BookMediaType.Ebook } }
            };

            var hash = "ABCDEF1234";
            var result = client.AddMagnet(remoteBook, hash);

            Assert.That(result, Is.EqualTo(hash));
            Assert.That(proxy.AddedMagnetLinks, Has.Count.EqualTo(1));
            Assert.That(proxy.LoadedHashes, Is.EquivalentTo(new[] { hash.ToLower() }));
            Assert.That(proxy.Labels, Is.EquivalentTo(new[] { (hash.ToLower(), "ebooks") }));
        }

        [Test]
        public void should_surface_original_error_when_torrent_is_not_loaded()
        {
            var proxy = new TestProxy { TorrentLoaded = false };
            var proxySelector = new TestProxySelector(proxy, new Version(2, 11, 0));
            var cacheManager = new CacheManager();

            var client = new TestQBittorrent(proxySelector, cacheManager, LogManager.GetCurrentClassLogger())
            {
                Definition = new DownloadClientDefinition
                {
                    Name = "qBittorrent",
                    Settings = new QBittorrentSettings
                    {
                        EbookCategory = "ebooks",
                        AudiobookCategory = "audiobooks"
                    }
                }
            };

            var remoteBook = new RemoteBook
            {
                Books = new List<Book> { new Book { MediaType = BookMediaType.Ebook } }
            };

            Assert.Throws<DownloadClientException>(() => client.AddTorrentFile(remoteBook, "ABCDEF1234"));
        }
    }
}

