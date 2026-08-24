using System;
using System.Collections.Generic;
using System.Net;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Download.Clients.QBittorrent;
using NzbDrone.Core.Exceptions;
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
            public Exception AddException { get; set; }

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
                return false;
            }

            public QBittorrentTorrentProperties GetTorrentProperties(string hash, QBittorrentSettings settings) => throw new NotImplementedException();

            public List<QBittorrentTorrentFile> GetTorrentFiles(string hash, QBittorrentSettings settings) => throw new NotImplementedException();

            public void AddTorrentFromUrl(string torrentUrl, TorrentSeedConfiguration seedConfiguration, QBittorrentSettings settings, string category = null)
            {
                AddedMagnetLinks.Add(torrentUrl);

                if (AddException != null)
                {
                    throw AddException;
                }
            }

            public void AddTorrentFromFile(string fileName, byte[] fileContent, TorrentSeedConfiguration seedConfiguration, QBittorrentSettings settings, string category = null)
            {
                AddedTorrentFiles.Add(fileName);

                if (AddException != null)
                {
                    throw AddException;
                }
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
        public void should_reject_torrent_file_conflicts_without_adopting_the_existing_torrent()
        {
            var proxy = new TestProxy { AddException = ConflictException() };
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
                Books = new List<Book> { new Book { MediaType = BookMediaType.Ebook } },
                Release = new TorrentInfo()
            };

            var hash = "ABCDEF1234";
            var exception = Assert.Throws<DownloadClientRejectedReleaseException>(() => client.AddTorrentFile(remoteBook, hash));

            Assert.Multiple(() =>
            {
                Assert.That(exception.Message, Is.EqualTo("qBittorrent rejected the torrent file due to a conflict"));
                Assert.That(proxy.AddedTorrentFiles, Has.Count.EqualTo(1));
                Assert.That(proxy.LoadedHashes, Is.Empty);
                Assert.That(proxy.Labels, Is.Empty);
            });
        }

        [Test]
        public void should_reject_magnet_conflicts_without_adopting_the_existing_torrent()
        {
            var proxy = new TestProxy { AddException = ConflictException() };
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
                Books = new List<Book> { new Book { MediaType = BookMediaType.Ebook } },
                Release = new TorrentInfo()
            };

            var hash = "ABCDEF1234";
            var exception = Assert.Throws<DownloadClientRejectedReleaseException>(() => client.AddMagnet(remoteBook, hash));

            Assert.Multiple(() =>
            {
                Assert.That(exception.Message, Is.EqualTo("qBittorrent rejected the magnet link due to a conflict"));
                Assert.That(proxy.AddedMagnetLinks, Has.Count.EqualTo(1));
                Assert.That(proxy.LoadedHashes, Is.Empty);
                Assert.That(proxy.Labels, Is.Empty);
            });
        }

        [Test]
        public void should_surface_non_conflict_errors_unchanged()
        {
            var original = new DownloadClientException("qBittorrent rejected request");
            var proxy = new TestProxy { AddException = original };
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

            var exception = Assert.Throws<DownloadClientException>(() => client.AddTorrentFile(remoteBook, "ABCDEF1234"));

            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.SameAs(original));
                Assert.That(proxy.LoadedHashes, Is.Empty);
                Assert.That(proxy.Labels, Is.Empty);
            });
        }

        private static DownloadClientException ConflictException()
        {
            var request = new HttpRequest("http://localhost/api/v2/torrents/add");
            var response = new HttpResponse(request, new HttpHeader(), Array.Empty<byte>(), HttpStatusCode.Conflict);

            return new DownloadClientException("qBittorrent rejected request", new HttpException(request, response));
        }
    }
}
