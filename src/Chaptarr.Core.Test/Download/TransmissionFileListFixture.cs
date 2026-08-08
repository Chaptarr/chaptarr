using System;
using System.Collections.Generic;
using NLog;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Download.Clients.Transmission;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.RemotePathMappings;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class TransmissionFileListFixture
    {
        private class TestProxy : ITransmissionProxy
        {
            public string RequestedHash { get; private set; }
            public TransmissionTorrent TorrentDetails { get; set; }
            public bool ThrowOnDetails { get; set; }

            public List<TransmissionTorrent> GetTorrents(TransmissionSettings settings) => throw new NotImplementedException();

            public TransmissionTorrent GetTorrentDetails(string hashString, TransmissionSettings settings)
            {
                RequestedHash = hashString;

                if (ThrowOnDetails)
                {
                    throw new TransmissionException("RPC failure");
                }

                return TorrentDetails;
            }

            public void AddTorrentFromUrl(string torrentUrl, string downloadDirectory, TransmissionSettings settings) => throw new NotImplementedException();
            public void AddTorrentFromData(byte[] torrentData, string downloadDirectory, TransmissionSettings settings) => throw new NotImplementedException();
            public void SetTorrentSeedingConfiguration(string hash, TorrentSeedConfiguration seedConfiguration, TransmissionSettings settings) => throw new NotImplementedException();
            public TransmissionConfig GetConfig(TransmissionSettings settings) => throw new NotImplementedException();
            public string GetProtocolVersion(TransmissionSettings settings) => throw new NotImplementedException();
            public string GetClientVersion(TransmissionSettings settings) => throw new NotImplementedException();
            public void RemoveTorrent(string hash, bool removeData, TransmissionSettings settings) => throw new NotImplementedException();
            public void MoveTorrentToTopInQueue(string hashString, TransmissionSettings settings) => throw new NotImplementedException();
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

                if (downloadClientId == 13 &&
                    string.Equals(host, "transmission", StringComparison.InvariantCultureIgnoreCase) &&
                    path.StartsWith("/remote/", StringComparison.InvariantCultureIgnoreCase))
                {
                    return new OsPath("/local/" + path.Substring("/remote/".Length));
                }

                return remotePath;
            }
        }

        [Test]
        public void should_capture_authoritative_file_list_for_import_item()
        {
            var proxy = new TestProxy
            {
                TorrentDetails = new TransmissionTorrent
                {
                    DownloadDir = "/remote/downloads",
                    Files = new List<TransmissionTorrentFile>
                    {
                        new() { Name = "Author - Book/part1.m4b" },
                        new() { Name = "Author - Book/part2.m4b" },
                        new() { Name = "Author - Book/PART2.m4b" }
                    },
                    FileStats = new List<TransmissionTorrentFileStats>
                    {
                        new() { Wanted = true },
                        new() { Wanted = true },
                        new() { Wanted = true }
                    }
                }
            };

            var client = CreateClient(proxy);

            var importItem = client.GetImportItem(new DownloadClientItem
            {
                DownloadId = "ABCDEF1234",
                Title = "Author - Book",
                OutputPath = new OsPath("/local/downloads/Author - Book"),
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Id = 13,
                    Name = "Transmission",
                    Protocol = DownloadProtocol.Torrent
                }
            }, null);

            Assert.That(proxy.RequestedHash, Is.EqualTo("abcdef1234"));
            Assert.That(importItem.FilePaths, Is.EqualTo(new[]
            {
                "/local/downloads/Author - Book/part1.m4b",
                "/local/downloads/Author - Book/part2.m4b"
            }));
            Assert.That(importItem.FileListConfidence, Is.EqualTo(DownloadClientFileListConfidence.Authoritative));
        }

        [Test]
        public void should_ignore_unwanted_transmission_files()
        {
            var proxy = new TestProxy
            {
                TorrentDetails = new TransmissionTorrent
                {
                    DownloadDir = "/remote/downloads",
                    Files = new List<TransmissionTorrentFile>
                    {
                        new() { Name = "Author - Book/part1.m4b" },
                        new() { Name = "Author - Book/sample.nfo" }
                    },
                    FileStats = new List<TransmissionTorrentFileStats>
                    {
                        new() { Wanted = true },
                        new() { Wanted = false }
                    }
                }
            };

            var client = CreateClient(proxy);

            var importItem = client.GetImportItem(new DownloadClientItem
            {
                DownloadId = "ABCDEF1234",
                Title = "Author - Book",
                OutputPath = new OsPath("/local/downloads/Author - Book"),
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Id = 13,
                    Name = "Transmission",
                    Protocol = DownloadProtocol.Torrent
                }
            }, null);

            Assert.That(importItem.FilePaths, Is.EqualTo(new[]
            {
                "/local/downloads/Author - Book/part1.m4b"
            }));
            Assert.That(importItem.FileListConfidence, Is.EqualTo(DownloadClientFileListConfidence.Authoritative));
        }

        [Test]
        public void should_resolve_windows_download_dir_with_forward_slash_torrent_member_paths()
        {
            var proxy = new TestProxy
            {
                TorrentDetails = new TransmissionTorrent
                {
                    DownloadDir = @"X:\TempBooks",
                    Files = new List<TransmissionTorrentFile>
                    {
                        new() { Name = "Author - Book/part1.epub" }
                    },
                    FileStats = new List<TransmissionTorrentFileStats>
                    {
                        new() { Wanted = true }
                    }
                }
            };

            var importItem = CreateClient(proxy).GetImportItem(new DownloadClientItem
            {
                DownloadId = "ABCDEF1234",
                Title = "Author - Book",
                OutputPath = new OsPath(@"X:\TempBooks\Author - Book"),
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Id = 13,
                    Name = "Transmission",
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
        public void should_fall_back_to_output_path_when_file_list_fetch_fails()
        {
            var client = CreateClient(new TestProxy { ThrowOnDetails = true });

            var importItem = client.GetImportItem(new DownloadClientItem
            {
                DownloadId = "ABCDEF1234",
                Title = "Author - Book",
                OutputPath = new OsPath("/local/downloads/Author - Book"),
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Id = 13,
                    Name = "Transmission",
                    Protocol = DownloadProtocol.Torrent
                }
            }, null);

            Assert.That(importItem.OutputPath.FullPath, Is.EqualTo("/local/downloads/Author - Book"));
            Assert.That(importItem.FilePaths, Is.Null);
            Assert.That(importItem.FileListConfidence, Is.Null);
        }

        [Test]
        public void should_return_clone_without_file_paths_when_file_list_fetch_fails_without_output_path()
        {
            var client = CreateClient(new TestProxy { ThrowOnDetails = true });

            var importItem = client.GetImportItem(new DownloadClientItem
            {
                DownloadId = "ABCDEF1234",
                Title = "Author - Book"
            }, null);

            Assert.That(importItem.FilePaths, Is.Null);
            Assert.That(importItem.FileListConfidence, Is.Null);
        }

        [Test]
        public void should_deserialize_transmission_torrent_get_file_list_response()
        {
            const string json = @"{
                ""result"": ""success"",
                ""arguments"": {
                    ""torrents"": [
                        {
                            ""hashString"": ""ABCDEF1234"",
                            ""downloadDir"": ""/downloads"",
                            ""files"": [
                                { ""name"": ""Author - Book/part1.m4b"", ""length"": 12345, ""bytesCompleted"": 12345 },
                                { ""name"": ""Author - Book/part2.m4b"", ""length"": 67890, ""bytesCompleted"": 1000 }
                            ],
                            ""fileStats"": [
                                { ""bytesCompleted"": 12345, ""wanted"": true, ""priority"": 0 },
                                { ""bytesCompleted"": 1000, ""wanted"": false, ""priority"": -1 }
                            ]
                        }
                    ]
                }
            }";

            var response = JsonConvert.DeserializeObject<TransmissionResponse>(json);
            var torrents = ((JArray)response.Arguments["torrents"]).ToObject<List<TransmissionTorrent>>();

            Assert.That(torrents, Has.Count.EqualTo(1));
            Assert.That(torrents[0].HashString, Is.EqualTo("ABCDEF1234"));
            Assert.That(torrents[0].DownloadDir, Is.EqualTo("/downloads"));
            Assert.That(torrents[0].Files, Has.Count.EqualTo(2));
            Assert.That(torrents[0].Files[0].Name, Is.EqualTo("Author - Book/part1.m4b"));
            Assert.That(torrents[0].Files[0].Length, Is.EqualTo(12345));
            Assert.That(torrents[0].Files[1].Name, Is.EqualTo("Author - Book/part2.m4b"));
            Assert.That(torrents[0].Files[1].BytesCompleted, Is.EqualTo(1000));
            Assert.That(torrents[0].FileStats, Has.Count.EqualTo(2));
            Assert.That(torrents[0].FileStats[0].Wanted, Is.True);
            Assert.That(torrents[0].FileStats[1].Wanted, Is.False);
        }

        private static Transmission CreateClient(ITransmissionProxy proxy)
        {
            return new Transmission(
                proxy,
                torrentFileInfoReader: null,
                httpClient: null,
                configService: null,
                diskProvider: null,
                remotePathMappingService: new MappingRemotePathService(),
                blocklistService: null,
                logger: LogManager.GetCurrentClassLogger())
            {
                Definition = new DownloadClientDefinition
                {
                    Id = 13,
                    Name = "Transmission",
                    Settings = new TransmissionSettings
                    {
                        Host = "transmission"
                    }
                }
            };
        }
    }
}
