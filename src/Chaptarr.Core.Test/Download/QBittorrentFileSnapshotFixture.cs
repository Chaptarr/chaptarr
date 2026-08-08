using System;
using System.Collections.Generic;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Download.Clients.QBittorrent;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles.TorrentInfo;
using NzbDrone.Core.RemotePathMappings;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class QBittorrentFileSnapshotFixture
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
            public List<QBittorrentTorrentFile> Files { get; set; } = new();
            public QBittorrentTorrentProperties Properties { get; set; } = new();
            public Exception FilesException { get; set; }
            public string RequestedHash { get; private set; }

            public bool IsApiSupported(QBittorrentSettings settings) => true;
            public Version GetApiVersion(QBittorrentSettings settings) => new(2, 11, 0);
            public string GetVersion(QBittorrentSettings settings) => "5.0.0";
            public QBittorrentPreferences GetConfig(QBittorrentSettings settings) => throw new NotImplementedException();
            public List<QBittorrentTorrent> GetTorrents(QBittorrentSettings settings, string category = null) => throw new NotImplementedException();
            public bool IsTorrentLoaded(string hash, QBittorrentSettings settings) => throw new NotImplementedException();
            public QBittorrentTorrentProperties GetTorrentProperties(string hash, QBittorrentSettings settings) => Properties;

            public List<QBittorrentTorrentFile> GetTorrentFiles(string hash, QBittorrentSettings settings)
            {
                RequestedHash = hash;

                if (FilesException != null)
                {
                    throw FilesException;
                }

                return Files;
            }

            public void AddTorrentFromUrl(string torrentUrl, TorrentSeedConfiguration seedConfiguration, QBittorrentSettings settings, string category = null) => throw new NotImplementedException();
            public void AddTorrentFromFile(string fileName, byte[] fileContent, TorrentSeedConfiguration seedConfiguration, QBittorrentSettings settings, string category = null) => throw new NotImplementedException();
            public void RemoveTorrent(string hash, bool removeData, QBittorrentSettings settings) => throw new NotImplementedException();
            public void SetTorrentLabel(string hash, string label, QBittorrentSettings settings) => throw new NotImplementedException();
            public void AddLabel(string label, QBittorrentSettings settings) => throw new NotImplementedException();
            public Dictionary<string, QBittorrentLabel> GetLabels(QBittorrentSettings settings) => throw new NotImplementedException();
            public void SetTorrentSeedingConfiguration(string hash, TorrentSeedConfiguration seedConfiguration, QBittorrentSettings settings) => throw new NotImplementedException();
            public void MoveTorrentToTopInQueue(string hash, QBittorrentSettings settings) => throw new NotImplementedException();
            public void SetForceStart(string hash, bool enabled, QBittorrentSettings settings) => throw new NotImplementedException();
        }

        private class TestProxySelector : IQBittorrentProxySelector
        {
            private readonly IQBittorrentProxy _proxy;

            public TestProxySelector(IQBittorrentProxy proxy)
            {
                _proxy = proxy;
            }

            public IQBittorrentProxy GetProxy(QBittorrentSettings settings, bool force = false) => _proxy;
            public Version GetApiVersion(QBittorrentSettings settings, bool force = false) => new(2, 11, 0);
        }

        private class MappingRemotePathService : IRemotePathMappingService
        {
            public List<RemotePathMapping> All() => throw new NotImplementedException();
            public RemotePathMapping Add(RemotePathMapping mapping) => throw new NotImplementedException();
            public void Remove(int id) => throw new NotImplementedException();
            public RemotePathMapping Get(int id) => throw new NotImplementedException();
            public RemotePathMapping Update(RemotePathMapping mapping) => throw new NotImplementedException();
            public OsPath RemapLocalToRemote(string host, OsPath localPath) => localPath;
            public OsPath RemapRemoteToLocal(string host, OsPath remotePath) => RemapRemoteToLocal(0, host, remotePath);
            public OsPath RemapLocalToRemote(int downloadClientId, string host, OsPath localPath) => localPath;
            public RemotePathMappingTestResult Test(RemotePathMapping mapping) => throw new NotImplementedException();

            public OsPath RemapRemoteToLocal(int downloadClientId, string host, OsPath remotePath)
            {
                var path = remotePath.FullPath;

                if (downloadClientId == 17 &&
                    string.Equals(host, "qbittorrent", StringComparison.InvariantCultureIgnoreCase) &&
                    path.StartsWith("/remote/", StringComparison.InvariantCultureIgnoreCase))
                {
                    return new OsPath("/local/" + path.Substring("/remote/".Length));
                }

                return remotePath;
            }
        }

        private class TestQBittorrent : QBittorrent
        {
            public TestQBittorrent(IQBittorrentProxySelector proxySelector)
                : base(proxySelector,
                    new TestTorrentFileInfoReader(),
                    httpClient: null,
                    configService: null,
                    diskProvider: null,
                    remotePathMappingService: new MappingRemotePathService(),
                    cacheManager: new CacheManager(),
                    blocklistService: null,
                    logger: LogManager.GetCurrentClassLogger())
            {
            }
        }

        [Test]
        public void should_capture_selected_qbittorrent_files_only()
        {
            var proxy = new TestProxy
            {
                Properties = new QBittorrentTorrentProperties { SavePath = "/remote/downloads" },
                Files = new List<QBittorrentTorrentFile>
                {
                    new() { Name = "Author - Book/part1.m4b", Priority = 1 },
                    new() { Name = "Author - Book/sample.nfo", Priority = 0 }
                }
            };

            var client = Client(proxy);

            var importItem = client.GetImportItem(new DownloadClientItem
            {
                DownloadId = "ABCDEF1234",
                Title = "Author - Book",
                OutputPath = new OsPath("/local/downloads/Author - Book"),
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Id = 17,
                    Name = "qBittorrent",
                    Protocol = DownloadProtocol.Torrent
                }
            }, null);

            Assert.That(proxy.RequestedHash, Is.EqualTo("abcdef1234"));
            Assert.That(importItem.FilePaths, Is.EqualTo(new[]
            {
                "/local/downloads/Author - Book/part1.m4b"
            }));
            Assert.That(importItem.FileListConfidence, Is.EqualTo(DownloadClientFileListConfidence.Authoritative));
        }

        [Test]
        public void should_resolve_windows_save_path_with_forward_slash_torrent_member_paths()
        {
            var proxy = new TestProxy
            {
                Properties = new QBittorrentTorrentProperties { SavePath = @"X:\TempBooks" },
                Files = new List<QBittorrentTorrentFile>
                {
                    new() { Name = "Author - Book/part1.epub", Priority = 1 }
                }
            };

            var importItem = Client(proxy).GetImportItem(new DownloadClientItem
            {
                DownloadId = "ABCDEF1234",
                Title = "Author - Book",
                OutputPath = new OsPath(@"X:\TempBooks\Author - Book"),
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Id = 17,
                    Name = "qBittorrent",
                    Protocol = DownloadProtocol.Torrent
                }
            }, null);

            Assert.That(importItem.FilePaths, Is.EqualTo(new[]
            {
                @"X:\TempBooks\Author - Book\part1.epub"
            }));
            Assert.That(importItem.FileListConfidence, Is.EqualTo(DownloadClientFileListConfidence.Authoritative));
        }

        [Test]
        public void should_not_throw_when_download_id_is_missing()
        {
            var importItem = Client(new TestProxy()).GetImportItem(new DownloadClientItem
            {
                Title = "Author - Book",
                OutputPath = new OsPath("/local/downloads/Author - Book")
            }, null);

            Assert.That(importItem.FilePaths, Is.Null);
            Assert.That(importItem.FileListConfidence, Is.Null);
        }

        [Test]
        public void should_return_clone_without_file_paths_when_file_list_fetch_fails_without_output_path()
        {
            var importItem = Client(new TestProxy { FilesException = new DownloadClientException("qbit unavailable") })
                .GetImportItem(new DownloadClientItem
                {
                    DownloadId = "ABCDEF1234",
                    Title = "Author - Book"
                }, null);

            Assert.That(importItem.FilePaths, Is.Null);
            Assert.That(importItem.FileListConfidence, Is.Null);
        }

        [Test]
        public void should_return_clone_without_file_paths_when_save_path_is_missing()
        {
            var proxy = new TestProxy
            {
                Properties = new QBittorrentTorrentProperties { SavePath = "" },
                Files = new List<QBittorrentTorrentFile>
                {
                    new() { Name = "Author - Book/part1.m4b", Priority = 1 }
                }
            };

            var importItem = Client(proxy).GetImportItem(new DownloadClientItem
            {
                DownloadId = "ABCDEF1234",
                Title = "Author - Book",
                OutputPath = new OsPath("/local/downloads/Author - Book")
            }, null);

            Assert.That(importItem.FilePaths, Is.Null);
            Assert.That(importItem.FileListConfidence, Is.Null);
        }

        private static TestQBittorrent Client(IQBittorrentProxy proxy)
        {
            return new TestQBittorrent(new TestProxySelector(proxy))
            {
                Definition = new DownloadClientDefinition
                {
                    Id = 17,
                    Name = "qBittorrent",
                    Settings = new QBittorrentSettings { Host = "qbittorrent" }
                }
            };
        }
    }
}
