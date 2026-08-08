using System;
using System.Collections.Generic;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Http;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients.Flood;
using NzbDrone.Core.Download.Clients.Flood.Types;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles.TorrentInfo;
using NzbDrone.Core.RemotePathMappings;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class FloodFileSnapshotFixture
    {
        private class TestTorrentFileInfoReader : ITorrentFileInfoReader
        {
            public string GetHashFromTorrentFile(byte[] fileContents)
            {
                return "hash";
            }
        }

        private class TestProxy : IFloodProxy
        {
            public List<string> ContentPaths { get; set; } = new();

            public void AuthVerify(FloodSettings settings) => throw new NotImplementedException();
            public void AddTorrentByUrl(string url, IEnumerable<string> tags, FloodSettings settings) => throw new NotImplementedException();
            public void AddTorrentByFile(string file, IEnumerable<string> tags, FloodSettings settings) => throw new NotImplementedException();
            public void DeleteTorrent(string hash, bool deleteData, FloodSettings settings) => throw new NotImplementedException();
            public Dictionary<string, Torrent> GetTorrents(FloodSettings settings) => throw new NotImplementedException();
            public List<string> GetTorrentContentPaths(string hash, FloodSettings settings) => ContentPaths;
            public void SetTorrentsTags(string hash, IEnumerable<string> tags, FloodSettings settings) => throw new NotImplementedException();
            public FloodClientSettings GetClientSettings(FloodSettings settings) => throw new NotImplementedException();
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

        [Test]
        public void should_resolve_single_file_windows_output_path_with_forward_slash_torrent_member_path()
        {
            var client = new Flood(
                new TestProxy
                {
                    ContentPaths = new List<string> { "Author - Book/part1.epub" }
                },
                downloadSeedConfigProvider: null,
                torrentFileInfoReader: new TestTorrentFileInfoReader(),
                httpClient: null,
                configService: null,
                diskProvider: null,
                remotePathMappingService: new PassthroughRemotePathMappingService(),
                blocklistService: null,
                logger: LogManager.GetCurrentClassLogger())
            {
                Definition = new DownloadClientDefinition
                {
                    Id = 21,
                    Name = "Flood",
                    Settings = new FloodSettings { Host = "flood" }
                }
            };

            var importItem = client.GetImportItem(new DownloadClientItem
            {
                DownloadId = "ABCDEF1234",
                Title = "Author - Book",
                OutputPath = new OsPath(@"X:\TempBooks")
            }, null);

            Assert.That(importItem.OutputPath.FullPath, Is.EqualTo(@"X:\TempBooks\Author - Book\part1.epub"));
        }
    }
}
