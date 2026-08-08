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
using NzbDrone.Core.Download.Clients.Nzbget;
using NzbDrone.Core.RemotePathMappings;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class NzbgetFileSnapshotFallbackFixture
    {
        private class TestNzbgetProxy : INzbgetProxy
        {
            public List<NzbgetQueueItem> Queue { get; set; } = new();
            public List<NzbgetHistoryItem> History { get; set; } = new();

            public string GetBaseUrl(NzbgetSettings settings, string relativePath = null) => "http://localhost:6789/";
            public string DownloadNzb(byte[] nzbData, string title, string category, int priority, bool addpaused, NzbgetSettings settings) => throw new NotImplementedException();
            public NzbgetGlobalStatus GetGlobalStatus(NzbgetSettings settings) => new();
            public List<NzbgetQueueItem> GetQueue(NzbgetSettings settings) => Queue;
            public List<NzbgetHistoryItem> GetHistory(NzbgetSettings settings) => History;
            public string GetVersion(NzbgetSettings settings) => "24.8";
            public Dictionary<string, string> GetConfig(NzbgetSettings settings) => throw new NotImplementedException();
            public void RemoveItem(string id, NzbgetSettings settings) => throw new NotImplementedException();
            public void RetryDownload(string id, NzbgetSettings settings) => throw new NotImplementedException();
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
            var proxy = new TestNzbgetProxy
            {
                History = new List<NzbgetHistoryItem>
                {
                    new()
                    {
                        Id = 42,
                        Name = "Example Book",
                        Category = "audiobooks",
                        DestDir = "/downloads/Example Book",
                        FinalDir = "/downloads/Example Book",
                        ParStatus = "SUCCESS",
                        UnpackStatus = "SUCCESS",
                        MoveStatus = "SUCCESS",
                        ScriptStatus = "SUCCESS",
                        DeleteStatus = "NONE",
                        MarkStatus = "NONE",
                        Parameters = new List<NzbgetParameter>()
                    }
                }
            };

            var client = new Nzbget(
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
                    Name = "NZBGet",
                    Settings = new NzbgetSettings
                    {
                        Host = "localhost",
                        Port = 6789,
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
