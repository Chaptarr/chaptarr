using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Download.Clients.QBittorrent;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MediaFiles.TorrentInfo;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RemotePathMappings;

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
            public List<QBittorrentTorrent> Torrents { get; } = new();
            public Dictionary<string, List<QBittorrentTorrent>> TorrentsByCategory { get; } = new(StringComparer.OrdinalIgnoreCase);
            public QBittorrentPreferences Preferences { get; set; } = new() { DhtEnabled = true };

            public bool IsApiSupported(QBittorrentSettings settings) => true;

            public Version GetApiVersion(QBittorrentSettings settings) => new Version(2, 11, 0);

            public string GetVersion(QBittorrentSettings settings) => "5.0.0";

            public QBittorrentPreferences GetConfig(QBittorrentSettings settings) => Preferences;

            public Dictionary<string, List<string>> HashesByCategory { get; } = new(StringComparer.OrdinalIgnoreCase);
            public bool TorrentLoaded { get; set; }

            public List<QBittorrentTorrent> GetTorrents(QBittorrentSettings settings, string category = null)
            {
                if (category == null)
                {
                    return Torrents;
                }

                if (TorrentsByCategory.TryGetValue(category, out var categoryTorrents))
                {
                    return categoryTorrents;
                }

                var hashes = category != null && HashesByCategory.TryGetValue(category, out var found) ? found : new List<string>();
                return hashes.ConvertAll(hash => new QBittorrentTorrent { Hash = hash });
            }

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

        private class PassthroughRemotePathMappingService : IRemotePathMappingService
        {
            public List<RemotePathMapping> All() => throw new NotImplementedException();
            public RemotePathMapping Add(RemotePathMapping mapping) => throw new NotImplementedException();
            public void Remove(int id) => throw new NotImplementedException();
            public RemotePathMapping Get(int id) => throw new NotImplementedException();
            public RemotePathMapping Update(RemotePathMapping mapping) => throw new NotImplementedException();
            public OsPath RemapRemoteToLocal(string host, OsPath remotePath) => remotePath;
            public OsPath RemapLocalToRemote(string host, OsPath localPath) => localPath;
            public OsPath RemapRemoteToLocal(int downloadClientId, string host, OsPath remotePath) => remotePath;
            public OsPath RemapLocalToRemote(int downloadClientId, string host, OsPath localPath) => localPath;
            public RemotePathMappingTestResult Test(RemotePathMapping mapping) => throw new NotImplementedException();
        }

        private class TestQBittorrent : QBittorrent
        {
            public TestQBittorrent(IQBittorrentProxySelector proxySelector, ICacheManager cacheManager, Logger logger)
                : base(proxySelector,
                    new TestTorrentFileInfoReader(),
                    httpClient: null,
                    configService: null,
                    diskProvider: null,
                    remotePathMappingService: new PassthroughRemotePathMappingService(),
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
                Assert.That(proxy.LoadedHashes, Is.EqualTo(new[] { "abcdef1234" }));
                Assert.That(proxy.Labels, Is.Empty);
            });
        }

        [Test]
        public void should_adopt_an_existing_torrent_file_when_qbittorrent_returns_a_conflict()
        {
            var proxy = new TestProxy { AddException = ConflictException() };
            proxy.HashesByCategory["ebooks"] = new List<string> { "abcdef1234" };
            var client = CreateClient(proxy);

            var result = client.AddTorrentFile(CreateRemoteBook(), "ABCDEF1234");

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.EqualTo("ABCDEF1234"));
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
                Assert.That(proxy.LoadedHashes, Is.EqualTo(new[] { "abcdef1234" }));
                Assert.That(proxy.Labels, Is.Empty);
            });
        }

        [Test]
        public void should_adopt_an_existing_magnet_torrent_when_qbittorrent_returns_a_conflict()
        {
            var proxy = new TestProxy { AddException = ConflictException() };
            proxy.HashesByCategory["ebooks"] = new List<string> { "abcdef1234" };
            var client = CreateClient(proxy);

            var result = client.AddMagnet(CreateRemoteBook(), "ABCDEF1234");

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.EqualTo("ABCDEF1234"));
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
                Assert.That(proxy.Labels, Is.Empty);
            });
        }

        [Test]
        public void should_continue_with_existing_torrent_already_in_a_chaptarr_category()
        {
            var proxy = new TestProxy { AddException = new DownloadClientException("qBittorrent rejected the torrent add request (response: Fails.).") };
            proxy.HashesByCategory["ebooks"] = new List<string> { "abcdef1234" };
            var client = CreateClient(proxy);

            var result = client.AddTorrentFile(CreateRemoteBook(), "ABCDEF1234");

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.EqualTo("ABCDEF1234"));
                Assert.That(proxy.LoadedHashes, Is.Empty);
                Assert.That(proxy.Labels, Is.Empty);
            });
        }

        [Test]
        public void should_move_existing_torrent_back_from_post_import_category()
        {
            var proxy = new TestProxy { AddException = ConflictException() };
            proxy.HashesByCategory["ebooks-imported"] = new List<string> { "abcdef1234" };
            var client = CreateClient(proxy);

            client.AddMagnet(CreateRemoteBook(), "ABCDEF1234");

            Assert.That(proxy.Labels, Is.EqualTo(new[] { ("abcdef1234", "ebooks") }));
        }

        [Test]
        public void should_reject_without_adopting_when_existing_torrent_is_outside_chaptarr_categories()
        {
            var proxy = new TestProxy
            {
                AddException = ConflictException(),
                TorrentLoaded = true
            };
            var client = CreateClient(proxy);

            var exception = Assert.Throws<DownloadClientRejectedReleaseException>(() => client.AddTorrentFile(CreateRemoteBook(), "ABCDEF1234"));

            Assert.Multiple(() =>
            {
                Assert.That(exception.Message, Is.EqualTo("qBittorrent already has this torrent outside Chaptarr's categories"));
                Assert.That(proxy.LoadedHashes, Is.EqualTo(new[] { "abcdef1234" }));
                Assert.That(proxy.Labels, Is.Empty);
            });
        }

        [Test]
        public void should_keep_an_actively_seeding_torrent_completed_but_not_movable_or_removable()
        {
            var downloadPath = Path.Combine(Path.GetTempPath(), "qbittorrent-downloads");
            var proxy = new TestProxy
            {
                Preferences = new QBittorrentPreferences { DhtEnabled = true, MaxRatioEnabled = true, MaxRatio = 2.0f }
            };
            proxy.TorrentsByCategory["ebooks"] = new List<QBittorrentTorrent>
            {
                new QBittorrentTorrent
                {
                    Hash = "abcdef1234",
                    Name = "Seeding ebook",
                    Category = "ebooks",
                    State = "uploading",
                    SavePath = downloadPath,
                    ContentPath = Path.Combine(downloadPath, "Seeding ebook.m4b"),
                    Progress = 1.0,
                    Ratio = 2.0f,
                    RatioLimit = -2
                }
            };
            var client = CreateClient(proxy);
            client.Definition = new DownloadClientDefinition
            {
                Id = 7,
                Name = "qBittorrent",
                RemoveCompletedDownloads = true,
                Settings = new QBittorrentSettings { EbookCategory = "ebooks" }
            };

            var item = client.GetItems().Single();

            Assert.Multiple(() =>
            {
                Assert.That(item.Status, Is.EqualTo(DownloadItemStatus.Completed));
                Assert.That(item.CanMoveFiles, Is.False);
                Assert.That(item.CanBeRemoved, Is.False);
            });
        }

        [Test]
        public void should_only_allow_moving_a_paused_completed_torrent_after_its_seed_limit()
        {
            var downloadPath = Path.Combine(Path.GetTempPath(), "qbittorrent-downloads");
            var proxy = new TestProxy();
            proxy.TorrentsByCategory["ebooks"] = new List<QBittorrentTorrent>
            {
                new QBittorrentTorrent
                {
                    Hash = "below-limit",
                    Name = "Below seed limit",
                    Category = "ebooks",
                    State = "pausedUP",
                    SavePath = downloadPath,
                    ContentPath = Path.Combine(downloadPath, "Below seed limit.m4b"),
                    Progress = 1.0,
                    Ratio = 1.99f,
                    RatioLimit = 2.0f
                },
                new QBittorrentTorrent
                {
                    Hash = "at-limit",
                    Name = "At seed limit",
                    Category = "ebooks",
                    State = "pausedUP",
                    SavePath = downloadPath,
                    ContentPath = Path.Combine(downloadPath, "At seed limit.m4b"),
                    Progress = 1.0,
                    Ratio = 2.0f,
                    RatioLimit = 2.0f
                }
            };
            var client = CreateClient(proxy);
            client.Definition = new DownloadClientDefinition
            {
                Id = 7,
                Name = "qBittorrent",
                RemoveCompletedDownloads = true,
                Settings = new QBittorrentSettings { EbookCategory = "ebooks" }
            };

            var items = client.GetItems().ToDictionary(item => item.DownloadId);

            Assert.Multiple(() =>
            {
                Assert.That(items["BELOW-LIMIT"].Status, Is.EqualTo(DownloadItemStatus.Completed));
                Assert.That(items["AT-LIMIT"].Status, Is.EqualTo(DownloadItemStatus.Completed));
                Assert.That(items["BELOW-LIMIT"].CanMoveFiles, Is.False);
                Assert.That(items["BELOW-LIMIT"].CanBeRemoved, Is.False);
                Assert.That(items["AT-LIMIT"].CanMoveFiles, Is.True);
                Assert.That(items["AT-LIMIT"].CanBeRemoved, Is.True);
            });
        }

        private static TestQBittorrent CreateClient(TestProxy proxy)
        {
            return new TestQBittorrent(new TestProxySelector(proxy, new Version(2, 11, 0)), new CacheManager(), LogManager.GetCurrentClassLogger())
            {
                Definition = new DownloadClientDefinition
                {
                    Name = "qBittorrent",
                    Settings = new QBittorrentSettings
                    {
                        EbookCategory = "ebooks",
                        AudiobookCategory = "audiobooks",
                        EbookImportedCategory = "ebooks-imported"
                    }
                }
            };
        }

        private static RemoteBook CreateRemoteBook()
        {
            return new RemoteBook
            {
                Books = new List<Book> { new Book { MediaType = BookMediaType.Ebook } },
                Release = new TorrentInfo()
            };
        }

        private static DownloadClientException ConflictException()
        {
            var request = new HttpRequest("http://localhost/api/v2/torrents/add");
            var response = new HttpResponse(request, new HttpHeader(), Array.Empty<byte>(), HttpStatusCode.Conflict);

            return new DownloadClientException("qBittorrent rejected request", new HttpException(request, response));
        }
    }
}
