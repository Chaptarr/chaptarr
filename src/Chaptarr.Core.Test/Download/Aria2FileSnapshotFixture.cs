using System;
using System.Collections.Generic;
using System.Xml.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Download.Clients.Aria2;
using NzbDrone.Core.MediaFiles.TorrentInfo;
using NzbDrone.Core.RemotePathMappings;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class Aria2FileSnapshotFixture
    {
        private class TestTorrentFileInfoReader : ITorrentFileInfoReader
        {
            public string GetHashFromTorrentFile(byte[] fileContents)
            {
                return "hash";
            }
        }

        private class TestProxy : IAria2Proxy
        {
            public List<Aria2Status> Torrents { get; set; } = new();
            public Aria2File[] Files { get; set; } = Array.Empty<Aria2File>();
            public Exception GetFilesException { get; set; }
            public string RequestedGid { get; private set; }

            public string GetVersion(Aria2Settings settings) => throw new NotImplementedException();
            public string AddMagnet(Aria2Settings settings, string magnet) => throw new NotImplementedException();
            public string AddTorrent(Aria2Settings settings, byte[] torrent) => throw new NotImplementedException();
            public bool RemoveTorrent(Aria2Settings settings, string gid) => throw new NotImplementedException();
            public bool RemoveCompletedTorrent(Aria2Settings settings, string gid) => throw new NotImplementedException();
            public Dictionary<string, string> GetGlobals(Aria2Settings settings) => throw new NotImplementedException();
            public List<Aria2Status> GetTorrents(Aria2Settings settings) => Torrents;
            public Aria2Status GetFromGID(Aria2Settings settings, string gid) => throw new NotImplementedException();

            public Aria2File[] GetFiles(Aria2Settings settings, string gid)
            {
                RequestedGid = gid;

                if (GetFilesException != null)
                {
                    throw GetFilesException;
                }

                return Files;
            }
        }

        private class PrefixRemotePathMappingService : IRemotePathMappingService
        {
            public int DownloadClientId { get; private set; }
            public string Host { get; private set; }

            public List<RemotePathMapping> All() => throw new NotImplementedException();
            public RemotePathMapping Add(RemotePathMapping mapping) => throw new NotImplementedException();
            public void Remove(int id) => throw new NotImplementedException();
            public RemotePathMapping Get(int id) => throw new NotImplementedException();
            public RemotePathMapping Update(RemotePathMapping mapping) => throw new NotImplementedException();
            public OsPath RemapRemoteToLocal(string host, OsPath remotePath) => Remap(remotePath);
            public OsPath RemapLocalToRemote(string host, OsPath localPath) => localPath;

            public OsPath RemapRemoteToLocal(int downloadClientId, string host, OsPath remotePath)
            {
                DownloadClientId = downloadClientId;
                Host = host;

                return Remap(remotePath);
            }

            public OsPath RemapLocalToRemote(int downloadClientId, string host, OsPath localPath) => localPath;
            public RemotePathMappingTestResult Test(RemotePathMapping mapping) => throw new NotImplementedException();

            private static OsPath Remap(OsPath remotePath)
            {
                if (remotePath.FullPath.StartsWith("/remote/", StringComparison.InvariantCulture))
                {
                    return new OsPath("/local/" + remotePath.FullPath.Substring("/remote/".Length));
                }

                return remotePath;
            }
        }

        private class TestAria2 : Aria2
        {
            public TestAria2(IAria2Proxy proxy, IRemotePathMappingService remotePathMappingService, Logger logger)
                : base(proxy,
                    new TestTorrentFileInfoReader(),
                    httpClient: null,
                    configService: null,
                    diskProvider: null,
                    remotePathMappingService: remotePathMappingService,
                    blocklistService: null,
                    logger: logger)
            {
            }
        }

        [Test]
        public void should_deserialize_aria2_get_files_file_entry()
        {
            var file = File("/downloads/Author - Book/part1.m4b", "1", "12345", "12345", "true");

            Assert.That(file.Index, Is.EqualTo("1"));
            Assert.That(file.Path, Is.EqualTo("/downloads/Author - Book/part1.m4b"));
            Assert.That(file.Length, Is.EqualTo("12345"));
            Assert.That(file.CompletedLength, Is.EqualTo("12345"));
            Assert.That(file.Selected, Is.EqualTo("true"));
            Assert.That(file.IsSelected, Is.True);
        }

        [Test]
        public void should_capture_authoritative_file_list_for_import_item()
        {
            var proxy = new TestProxy
            {
                Torrents = new List<Aria2Status>
                {
                    TorrentStatus("2089b05ecca3d829", "ABCDEF1234")
                },
                Files = new[]
                {
                    File("/remote/downloads/Author - Book/part1.m4b"),
                    File("/remote/downloads/Author - Book/part2.m4b"),
                    File("/remote/downloads/Author - Book/sample.nfo", selected: "false")
                }
            };

            var remotePathMappingService = new PrefixRemotePathMappingService();
            var client = Client(proxy, remotePathMappingService);

            var importItem = client.GetImportItem(new DownloadClientItem
            {
                DownloadId = "abcdef1234",
                Title = "Author - Book",
                OutputPath = new OsPath("/local/downloads/Author - Book")
            }, null);

            Assert.That(proxy.RequestedGid, Is.EqualTo("2089b05ecca3d829"));
            Assert.That(remotePathMappingService.DownloadClientId, Is.EqualTo(42));
            Assert.That(remotePathMappingService.Host, Is.EqualTo("aria2.example"));
            Assert.That(importItem.FilePaths, Is.EqualTo(new[]
            {
                "/local/downloads/Author - Book/part1.m4b",
                "/local/downloads/Author - Book/part2.m4b"
            }));
            Assert.That(importItem.FileListConfidence, Is.EqualTo(DownloadClientFileListConfidence.Authoritative));
        }

        [Test]
        public void should_return_clone_without_file_paths_when_get_files_fails_with_output_path()
        {
            var proxy = new TestProxy
            {
                Torrents = new List<Aria2Status>
                {
                    TorrentStatus("2089b05ecca3d829", "ABCDEF1234")
                },
                GetFilesException = new DownloadClientException("aria2 unavailable")
            };

            var client = Client(proxy, new PrefixRemotePathMappingService());
            var item = new DownloadClientItem
            {
                DownloadId = "ABCDEF1234",
                Title = "Author - Book",
                OutputPath = new OsPath("/local/downloads/Author - Book"),
                FilePaths = new List<string> { "/stale/path.m4b" },
                FileListConfidence = DownloadClientFileListConfidence.Authoritative
            };

            var importItem = client.GetImportItem(item, null);

            Assert.That(importItem, Is.Not.SameAs(item));
            Assert.That(importItem.OutputPath, Is.EqualTo(item.OutputPath));
            Assert.That(importItem.FilePaths, Is.Null);
            Assert.That(importItem.FileListConfidence, Is.Null);
        }

        [Test]
        public void should_not_capture_metadata_phase_file_list()
        {
            var proxy = new TestProxy
            {
                Torrents = new List<Aria2Status>
                {
                    TorrentStatus("2089b05ecca3d829", "ABCDEF1234")
                },
                Files = new[]
                {
                    File("[METADATA]abcdef1234")
                }
            };

            var client = Client(proxy, new PrefixRemotePathMappingService());

            var importItem = client.GetImportItem(new DownloadClientItem
            {
                DownloadId = "ABCDEF1234",
                Title = "Author - Book",
                OutputPath = new OsPath("/local/downloads/Author - Book")
            }, null);

            Assert.That(importItem.FilePaths, Is.Null);
            Assert.That(importItem.FileListConfidence, Is.Null);
        }

        [Test]
        public void should_return_clone_without_file_paths_when_get_files_fails_without_output_path()
        {
            var proxy = new TestProxy
            {
                Torrents = new List<Aria2Status>
                {
                    TorrentStatus("2089b05ecca3d829", "ABCDEF1234")
                },
                GetFilesException = new DownloadClientException("aria2 unavailable")
            };

            var client = Client(proxy, new PrefixRemotePathMappingService());

            var importItem = client.GetImportItem(new DownloadClientItem
            {
                DownloadId = "ABCDEF1234",
                Title = "Author - Book"
            }, null);

            Assert.That(importItem.FilePaths, Is.Null);
            Assert.That(importItem.FileListConfidence, Is.Null);
        }

        private static TestAria2 Client(IAria2Proxy proxy, IRemotePathMappingService remotePathMappingService)
        {
            return new TestAria2(proxy, remotePathMappingService, LogManager.GetCurrentClassLogger())
            {
                Definition = new DownloadClientDefinition
                {
                    Id = 42,
                    Name = "Aria2",
                    Settings = new Aria2Settings { Host = "aria2.example" }
                }
            };
        }

        private static Aria2Status TorrentStatus(string gid, string infoHash)
        {
            return new Aria2Status(XElement.Parse($@"
                <value>
                    <struct>
                        <member><name>gid</name><value><string>{gid}</string></value></member>
                        <member><name>infoHash</name><value><string>{infoHash}</string></value></member>
                    </struct>
                </value>"));
        }

        private static Aria2File File(string path, string index = "1", string length = "12345", string completedLength = "12345", string selected = "true")
        {
            return new Aria2File(XElement.Parse($@"
                <value>
                    <struct>
                        <member><name>index</name><value><string>{index}</string></value></member>
                        <member><name>path</name><value><string>{path}</string></value></member>
                        <member><name>length</name><value><string>{length}</string></value></member>
                        <member><name>completedLength</name><value><string>{completedLength}</string></value></member>
                        <member><name>selected</name><value><string>{selected}</string></value></member>
                    </struct>
                </value>"));
        }
    }
}
