using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Download.Clients.Sabnzbd;
using NzbDrone.Core.Download.Clients.Sabnzbd.Responses;
using NzbDrone.Core.RemotePathMappings;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class SabnzbdFileSnapshotFallbackFixture
    {
        private class TestSabnzbdProxy : ISabnzbdProxy
        {
            public SabnzbdHistory History { get; set; } = new() { Items = new List<SabnzbdHistoryItem>() };
            public SabnzbdQueue Queue { get; set; } = new() { Items = new List<SabnzbdQueueItem>() };

            public string GetBaseUrl(SabnzbdSettings settings, string relativePath = null) => "http://localhost:8080/";
            public SabnzbdAddResponse DownloadNzb(byte[] nzbData, string filename, string category, int priority, SabnzbdSettings settings) => throw new NotImplementedException();
            public void RemoveFromQueue(string id, bool deleteData, SabnzbdSettings settings) => throw new NotImplementedException();
            public void RemoveFromHistory(string id, bool deleteData, bool deletePermanently, SabnzbdSettings settings) => throw new NotImplementedException();
            public string GetVersion(SabnzbdSettings settings) => "5.0.0";
            public SabnzbdConfig GetConfig(SabnzbdSettings settings) => throw new NotImplementedException();
            public SabnzbdFullStatus GetFullStatus(SabnzbdSettings settings) => throw new NotImplementedException();
            public SabnzbdQueue GetQueue(int start, int limit, SabnzbdSettings settings) => Queue;
            public SabnzbdHistory GetHistory(int start, int limit, SabnzbdSettings settings) => History;
            public string RetryDownload(string id, SabnzbdSettings settings) => throw new NotImplementedException();
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

        private class ConfigServiceProxy : DispatchProxy
        {
            public int DownloadClientHistoryLimit { get; set; } = 60;

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == "get_DownloadClientHistoryLimit")
                {
                    return DownloadClientHistoryLimit;
                }

                throw new NotImplementedException($"Test proxy does not implement IConfigService.{targetMethod?.Name}");
            }
        }

        [Test]
        public void should_leave_completed_history_file_list_unset_for_disk_snapshot_fallback()
        {
            var proxy = new TestSabnzbdProxy
            {
                History = new SabnzbdHistory
                {
                    Items = new List<SabnzbdHistoryItem>
                    {
                        new()
                        {
                            Id = "SABnzbd_nzo_1",
                            Title = "Example Book",
                            Category = "audiobooks",
                            Status = SabnzbdDownloadStatus.Completed,
                            Storage = "/downloads/Example Book",
                            Size = 1234
                        }
                    }
                }
            };

            var client = new Sabnzbd(
                proxy,
                httpClient: null,
                configService: CreateConfigService(),
                diskProvider: null,
                remotePathMappingService: new PassthroughRemotePathMappingService(),
                nzbValidationService: null,
                logger: LogManager.GetCurrentClassLogger())
            {
                Definition = new DownloadClientDefinition
                {
                    Id = 1,
                    Name = "SABnzbd",
                    Settings = new SabnzbdSettings
                    {
                        Host = "localhost",
                        Port = 8080,
                        AudiobookCategory = "audiobooks",
                        EbookCategory = "ebooks"
                    }
                }
            };

            var items = client.GetItems().ToList();

            Assert.That(items, Has.Count.EqualTo(1));
            Assert.That(items[0].Status, Is.EqualTo(DownloadItemStatus.Completed));
            Assert.That(items[0].OutputPath.FullPath, Is.EqualTo("/downloads/Example Book"));
            Assert.That(items[0].FilePaths, Is.Null);
            Assert.That(items[0].FileListConfidence, Is.Null);
        }

        private static IConfigService CreateConfigService()
        {
            return DispatchProxy.Create<IConfigService, ConfigServiceProxy>();
        }
    }
}
