using System;
using System.Collections.Generic;
using NLog;
using Newtonsoft.Json;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Download.Clients.DownloadStation;
using NzbDrone.Core.Download.Clients.DownloadStation.Proxies;
using NzbDrone.Core.Download.Clients.DownloadStation.Responses;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles.TorrentInfo;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RemotePathMappings;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class DownloadStationFileSnapshotFixture
    {
        private class TestTorrentFileInfoReader : ITorrentFileInfoReader
        {
            public string GetHashFromTorrentFile(byte[] fileContents)
            {
                return "hash";
            }
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

        private class TestSharedFolderResolver : ISharedFolderResolver
        {
            public OsPath RemapToFullPath(OsPath sharedFolderPath, DownloadStationSettings settings, string serialNumber)
            {
                if (sharedFolderPath.FullPath.StartsWith("/downloads", StringComparison.InvariantCultureIgnoreCase))
                {
                    return new OsPath($"/volume1{sharedFolderPath.FullPath}");
                }

                return sharedFolderPath;
            }
        }

        private class TestSerialNumberProvider : ISerialNumberProvider
        {
            public string GetSerialNumber(DownloadStationSettings settings)
            {
                return "SERIAL";
            }
        }

        private class TestTaskProxy : IDownloadStationTaskProxy
        {
            public List<string> RequestedFileTaskIds { get; } = new();
            public List<DownloadStationTaskFile> TaskFiles { get; set; } = new();
            public Exception TaskFilesException { get; set; }

            public DiskStationApiInfo GetApiInfo(DownloadStationSettings settings)
            {
                return new DiskStationApiInfo { MinVersion = 1, MaxVersion = 1 };
            }

            public bool IsApiSupported(DownloadStationSettings settings) => true;
            public IEnumerable<DownloadStationTask> GetTasks(DownloadStationSettings settings) => throw new NotImplementedException();

            public IEnumerable<DownloadStationTaskFile> GetTaskFiles(string downloadId, DownloadStationSettings settings)
            {
                RequestedFileTaskIds.Add(downloadId);

                if (TaskFilesException != null)
                {
                    throw TaskFilesException;
                }

                return TaskFiles;
            }

            public void RemoveTask(string downloadId, DownloadStationSettings settings) => throw new NotImplementedException();
            public void AddTaskFromUrl(string url, string downloadDirectory, DownloadStationSettings settings) => throw new NotImplementedException();
            public void AddTaskFromData(byte[] data, string filename, string downloadDirectory, DownloadStationSettings settings) => throw new NotImplementedException();
        }

        private class TestTaskProxySelector : IDownloadStationTaskProxySelector
        {
            private readonly IDownloadStationTaskProxy _proxy;

            public TestTaskProxySelector(IDownloadStationTaskProxy proxy)
            {
                _proxy = proxy;
            }

            public IDownloadStationTaskProxy GetProxy(DownloadStationSettings settings) => _proxy;
        }

        private class TestTorrentDownloadStation : TorrentDownloadStation
        {
            public TestTorrentDownloadStation(IDownloadStationTaskProxy taskProxy, Logger logger)
                : base(new TestSharedFolderResolver(),
                    new TestSerialNumberProvider(),
                    fileStationProxy: null,
                    dsInfoProxy: null,
                    dsTaskProxySelector: new TestTaskProxySelector(taskProxy),
                    torrentFileInfoReader: new TestTorrentFileInfoReader(),
                    httpClient: null,
                    configService: null,
                    diskProvider: null,
                    remotePathMappingService: new PassthroughRemotePathMappingService(),
                    blocklistService: null,
                    logger: logger)
            {
            }
        }

        private class TestUsenetDownloadStation : UsenetDownloadStation
        {
            public TestUsenetDownloadStation(IDownloadStationTaskProxy taskProxy, Logger logger)
                : base(new TestSharedFolderResolver(),
                    new TestSerialNumberProvider(),
                    fileStationProxy: null,
                    dsInfoProxy: null,
                    dsTaskProxySelector: new TestTaskProxySelector(taskProxy),
                    httpClient: null,
                    configService: null,
                    diskProvider: null,
                    remotePathMappingService: new PassthroughRemotePathMappingService(),
                    nzbValidationService: null,
                    logger: logger)
            {
            }
        }

        [Test]
        public void should_deserialize_download_station_task_file_list_response()
        {
            const string json = @"{
                ""success"": true,
                ""data"": {
                    ""tasks"": [{
                        ""id"": ""dbid_001"",
                        ""type"": ""BT"",
                        ""username"": ""admin"",
                        ""title"": ""Author - Book"",
                        ""size"": ""80235"",
                        ""status"": ""finished"",
                        ""additional"": {
                            ""file"": [
                                { ""filename"": ""Author - Book/part1.m4b"", ""size"": ""12345"", ""size_downloaded"": ""12345"", ""priority"": ""normal"" },
                                { ""filename"": ""Author - Book/part2.m4b"", ""size"": ""67890"", ""size_downloaded"": ""0"", ""priority"": ""skip"" }
                            ]
                        }
                    }]
                }
            }";

            var response = JsonConvert.DeserializeObject<DiskStationResponse<DownloadStationTaskInfoResponse>>(json);
            var files = response.Data.Tasks[0].Additional.Files;

            Assert.That(files, Has.Count.EqualTo(2));
            Assert.That(files[0].FileName, Is.EqualTo("Author - Book/part1.m4b"));
            Assert.That(files[0].TotalSize, Is.EqualTo(12345));
            Assert.That(files[1].FileName, Is.EqualTo("Author - Book/part2.m4b"));
            Assert.That(files[1].BytesDownloaded, Is.EqualTo(0));
            Assert.That(files[1].Priority, Is.EqualTo(DownloadStationPriority.Skip));
        }

        [Test]
        public void should_capture_authoritative_file_list_for_import_item()
        {
            var proxy = new TestTaskProxy
            {
                TaskFiles = new List<DownloadStationTaskFile>
                {
                    new() { FileName = "Author - Book/part1.m4b" },
                    new() { FileName = "/downloads/Author - Book/part2.m4b" },
                    new() { FileName = "Author - Book/sample.nfo", Priority = DownloadStationPriority.Skip }
                }
            };
            var client = BuildClient(proxy);

            var importItem = client.GetImportItem(ImportItem(), null);

            Assert.That(proxy.RequestedFileTaskIds, Is.EqualTo(new[] { "dbid_001" }));
            Assert.That(importItem.FilePaths, Is.EqualTo(new[]
            {
                "/volume1/downloads/Author - Book/part1.m4b",
                "/volume1/downloads/Author - Book/part2.m4b"
            }));
            Assert.That(importItem.FileListConfidence, Is.EqualTo(DownloadClientFileListConfidence.Authoritative));
        }

        [Test]
        public void should_capture_relative_file_list_for_windows_output_path()
        {
            var proxy = new TestTaskProxy
            {
                TaskFiles = new List<DownloadStationTaskFile>
                {
                    new() { FileName = "Author - Book/part1.m4b" }
                }
            };
            var client = BuildClient(proxy);

            var importItem = client.GetImportItem(ImportItem(@"D:\downloads\Author - Book"), null);

            Assert.That(importItem.FilePaths, Is.EqualTo(new[]
            {
                @"D:\downloads\Author - Book\part1.m4b"
            }));
            Assert.That(importItem.FileListConfidence, Is.EqualTo(DownloadClientFileListConfidence.Authoritative));
        }

        [Test]
        public void should_not_capture_file_list_when_path_escapes_output_path()
        {
            var proxy = new TestTaskProxy
            {
                TaskFiles = new List<DownloadStationTaskFile>
                {
                    new() { FileName = "../part1.m4b" }
                }
            };
            var client = BuildClient(proxy);

            var importItem = client.GetImportItem(ImportItem(), null);

            Assert.That(importItem.FilePaths, Is.Null);
            Assert.That(importItem.FileListConfidence, Is.Null);
        }

        [Test]
        public void should_keep_existing_output_path_when_file_list_api_fails()
        {
            var proxy = new TestTaskProxy
            {
                TaskFilesException = new DownloadClientException("Download Station rejected request")
            };
            var client = BuildClient(proxy);

            var item = ImportItem();
            var importItem = client.GetImportItem(item, null);

            Assert.That(importItem.OutputPath, Is.EqualTo(item.OutputPath));
            Assert.That(importItem.FilePaths, Is.Null);
            Assert.That(importItem.FileListConfidence, Is.Null);
        }

        [Test]
        public void usenet_download_station_should_remain_disk_snapshot_only()
        {
            var client = new TestUsenetDownloadStation(new TestTaskProxy(), LogManager.GetCurrentClassLogger())
            {
                Definition = new DownloadClientDefinition
                {
                    Id = 12,
                    Name = "Download Station (Usenet)",
                    Settings = new DownloadStationSettings { Host = "nas" }
                }
            };

            var item = new DownloadClientItem
            {
                DownloadId = "SERIAL:dbid_002",
                Title = "Author - Book",
                OutputPath = new OsPath("/volume1/downloads/Author - Book"),
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Id = 12,
                    Name = "Download Station (Usenet)",
                    Protocol = DownloadProtocol.Usenet
                }
            };

            var importItem = client.GetImportItem(item, null);

            Assert.That(importItem, Is.Not.SameAs(item));
            Assert.That(importItem.OutputPath, Is.EqualTo(item.OutputPath));
            Assert.That(importItem.FilePaths, Is.Null);
            Assert.That(importItem.FileListConfidence, Is.Null);
        }

        private static TestTorrentDownloadStation BuildClient(TestTaskProxy proxy)
        {
            return new TestTorrentDownloadStation(proxy, LogManager.GetCurrentClassLogger())
            {
                Definition = new DownloadClientDefinition
                {
                    Id = 11,
                    Name = "Download Station",
                    Settings = new DownloadStationSettings { Host = "nas" }
                }
            };
        }

        private static DownloadClientItem ImportItem(string outputPath = "/volume1/downloads/Author - Book")
        {
            return new DownloadClientItem
            {
                DownloadId = "SERIAL:dbid_001",
                Title = "Author - Book",
                OutputPath = new OsPath(outputPath),
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Id = 11,
                    Name = "Download Station",
                    Protocol = DownloadProtocol.Torrent
                }
            };
        }
    }
}
