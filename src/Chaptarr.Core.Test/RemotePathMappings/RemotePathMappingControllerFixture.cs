using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Chaptarr.Api.V1.RemotePathMappings;
using FluentValidation.Results;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RemotePathMappings;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;
using NUnit.Framework;

namespace Chaptarr.Core.Test.RemotePathMappings
{
    [TestFixture]
    public class RemotePathMappingControllerFixture
    {
        private class DiskProviderProxy : DispatchProxy
        {
            public HashSet<string> ExistingFolders { get; set; } = new(StringComparer.InvariantCultureIgnoreCase);
            public HashSet<string> ExistingFiles { get; set; } = new(StringComparer.InvariantCultureIgnoreCase);
            public HashSet<string> WritableFolders { get; set; } = new(StringComparer.InvariantCultureIgnoreCase);

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case nameof(IDiskProvider.FolderExists):
                        return ExistingFolders.Contains((string)args[0]);

                    case nameof(IDiskProvider.FileExists):
                        return ExistingFiles.Contains((string)args[0]);

                    case nameof(IDiskProvider.FolderWritable):
                        return WritableFolders.Contains((string)args[0]);
                }

                throw new NotImplementedException($"Test proxy does not implement IDiskProvider.{targetMethod?.Name}");
            }
        }

        private class RootFolderServiceProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IRootFolderService.All))
                {
                    return new List<RootFolder>();
                }

                throw new NotImplementedException($"Test proxy does not implement IRootFolderService.{targetMethod?.Name}");
            }
        }

        private class RuntimeInfoProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IRuntimeInfo.IsWindowsService))
                {
                    return false;
                }

                throw new NotImplementedException($"Test proxy does not implement IRuntimeInfo.{targetMethod?.Name}");
            }
        }

        private sealed class FakeRemotePathMappingService : IRemotePathMappingService
        {
            public List<RemotePathMapping> Mappings { get; set; } = new();

            public RemotePathMappingTestResult Test(RemotePathMapping mapping)
            {
                var remotePath = new OsPath(mapping.RemotePath).AsDirectory();
                var localPath = new OsPath(mapping.LocalPath).AsDirectory();

                return new RemotePathMappingTestResult
                {
                    DownloadClientId = mapping.DownloadClientId,
                    Host = mapping.Host,
                    RemotePath = remotePath.FullPath,
                    LocalPath = localPath.FullPath,
                    MappedPath = localPath.FullPath,
                    IsMapped = true,
                    LocalPathExists = true,
                    LocalPathWritable = true,
                    MappedPathExists = true,
                    MappedPathWritable = true
                };
            }

            public OsPath RemapLocalToRemote(int downloadClientId, string host, OsPath localPath)
            {
                var localRoot = new OsPath("/downloads");
                var remoteRoot = new OsPath("/data/usenet/complete");

                if (localRoot.Contains(localPath))
                {
                    return remoteRoot + (localPath - localRoot);
                }

                return localPath;
            }

            public List<RemotePathMapping> All() => Mappings.ToList();
            public RemotePathMapping Add(RemotePathMapping mapping) => throw new NotImplementedException();
            public void Remove(int id) => throw new NotImplementedException();
            public RemotePathMapping Get(int id) => throw new NotImplementedException();
            public RemotePathMapping Update(RemotePathMapping mapping) => throw new NotImplementedException();
            public OsPath RemapRemoteToLocal(string host, OsPath remotePath) => throw new NotImplementedException();
            public OsPath RemapLocalToRemote(string host, OsPath localPath) => RemapLocalToRemote(0, host, localPath);
            public OsPath RemapRemoteToLocal(int downloadClientId, string host, OsPath remotePath) => throw new NotImplementedException();
        }

        private sealed class FakeDownloadClientFactory : IDownloadClientFactory
        {
            private readonly DownloadClientDefinition _definition;
            private readonly IDownloadClient _client;

            public FakeDownloadClientFactory(DownloadClientDefinition definition, IDownloadClient client)
            {
                _definition = definition;
                _client = client;
            }

            public List<DownloadClientDefinition> All() => new() { _definition };
            public DownloadClientDefinition Get(int id) => _definition.Id == id ? _definition : throw new KeyNotFoundException();
            public IDownloadClient GetInstance(DownloadClientDefinition definition) => _client;
            public List<IDownloadClient> DownloadHandlingEnabled(bool filterBlockedClients = true) => throw new NotImplementedException();
            public List<IDownloadClient> GetAvailableProviders() => throw new NotImplementedException();
            public bool Exists(int id) => _definition.Id == id;
            public DownloadClientDefinition Find(int id) => _definition.Id == id ? _definition : null;
            public IEnumerable<DownloadClientDefinition> Get(IEnumerable<int> ids) => ids.Contains(_definition.Id) ? new[] { _definition } : Array.Empty<DownloadClientDefinition>();
            public DownloadClientDefinition Create(DownloadClientDefinition definition) => throw new NotImplementedException();
            public void Update(DownloadClientDefinition definition) => throw new NotImplementedException();
            public IEnumerable<DownloadClientDefinition> Update(IEnumerable<DownloadClientDefinition> definitions) => throw new NotImplementedException();
            public void Delete(int id) => throw new NotImplementedException();
            public void Delete(IEnumerable<int> ids) => throw new NotImplementedException();
            public IEnumerable<DownloadClientDefinition> GetDefaultDefinitions() => throw new NotImplementedException();
            public IEnumerable<DownloadClientDefinition> GetPresetDefinitions(DownloadClientDefinition providerDefinition) => throw new NotImplementedException();
            public void SetProviderCharacteristics(DownloadClientDefinition definition) { }
            public void SetProviderCharacteristics(IDownloadClient provider, DownloadClientDefinition definition) { }
            public ValidationResult Test(DownloadClientDefinition definition) => throw new NotImplementedException();
            public object RequestAction(DownloadClientDefinition definition, string action, IDictionary<string, string> query) => throw new NotImplementedException();
            public List<DownloadClientDefinition> AllForTag(int tagId) => throw new NotImplementedException();
        }

        private sealed class FakeDownloadClient : IDownloadClient
        {
            private readonly List<OsPath> _outputRootFolders;
            private readonly List<DownloadClientItem> _items;
            private readonly bool _throwOnStatus;

            public FakeDownloadClient(List<OsPath> outputRootFolders, List<DownloadClientItem> items, bool throwOnStatus = false)
            {
                _outputRootFolders = outputRootFolders;
                _items = items;
                _throwOnStatus = throwOnStatus;
            }

            public string Name => "Fake";
            public Type ConfigContract => typeof(FakeDownloadClientSettings);
            public ProviderMessage Message => null;
            public IEnumerable<ProviderDefinition> DefaultDefinitions => Array.Empty<ProviderDefinition>();
            public ProviderDefinition Definition { get; set; }
            public DownloadProtocol Protocol => DownloadProtocol.Usenet;
            public ValidationResult Test() => new();
            public object RequestAction(string stage, IDictionary<string, string> query) => null;
            public Task<string> Download(RemoteBook remoteBook, IIndexer indexer) => throw new NotImplementedException();
            public IEnumerable<DownloadClientItem> GetItems() => _items;
            public DownloadClientItem GetImportItem(DownloadClientItem item, DownloadClientItem previousImportAttempt) => item;
            public void RemoveItem(DownloadClientItem item, bool deleteData) => throw new NotImplementedException();
            public DownloadClientInfo GetStatus()
            {
                if (_throwOnStatus)
                {
                    throw new Exception("Download client status failed");
                }

                return new DownloadClientInfo { OutputRootFolders = _outputRootFolders };
            }
            public void MarkItemAsImported(DownloadClientItem downloadClientItem) { }
        }

        private sealed class FakeDownloadClientSettings : IProviderConfig
        {
            public string Host { get; set; }

            public NzbDroneValidationResult Validate()
            {
                return new NzbDroneValidationResult();
            }
        }

        [Test]
        public void suggestions_should_not_probe_download_clients_for_host_wide_mapping()
        {
            var controller = CreateController(
                new List<OsPath> { new("/downloads") },
                new List<DownloadClientItem>(),
                existingFolders: new[] { "/downloads/" },
                writableFolders: new[] { "/downloads/" },
                mappings: new[]
                {
                    new RemotePathMapping { Id = 1, DownloadClientId = 0, Host = "download-host", RemotePath = "/data/", LocalPath = "/downloads/" },
                    new RemotePathMapping { Id = 2, DownloadClientId = 7, Host = "download-host", RemotePath = "/data/usenet/complete/audiobooks", LocalPath = "/data/usenet/complete/audiobooks" }
                },
                throwOnStatus: true);

            var result = controller.GetSuggestions(0, "download-host");

            Assert.Multiple(() =>
            {
                Assert.That(result.DownloadClientError, Is.Null);
                Assert.That(result.DownloadClientPaths, Does.Contain("/data/"));
                Assert.That(result.DownloadClientPaths, Does.Not.Contain("/data/usenet/complete/audiobooks/"));
            });
        }

        [Test]
        public void suggestions_should_not_surface_download_client_status_errors()
        {
            var controller = CreateController(
                new List<OsPath> { new("/downloads") },
                new List<DownloadClientItem>(),
                existingFolders: new[] { "/downloads/" },
                writableFolders: new[] { "/downloads/" },
                mappings: new[]
                {
                    new RemotePathMapping { Id = 1, DownloadClientId = 7, Host = "download-host", RemotePath = "/data/", LocalPath = "/downloads/" }
                },
                throwOnStatus: true);

            var result = controller.GetSuggestions(7, "download-host");

            Assert.Multiple(() =>
            {
                Assert.That(result.DownloadClientError, Is.Null);
                Assert.That(result.DownloadClientPaths, Does.Contain("/data/"));
            });
        }

        [Test]
        public void test_mapping_should_not_probe_download_clients_for_host_wide_mapping()
        {
            var controller = CreateController(
                new List<OsPath> { new("/downloads") },
                new List<DownloadClientItem>(),
                existingFolders: new[] { "/downloads/" },
                writableFolders: new[] { "/downloads/" },
                throwOnStatus: true);

            var result = controller.TestMapping(new RemotePathMappingTestResource
            {
                DownloadClientId = 0,
                Host = "download-host",
                RemotePath = "/data",
                LocalPath = "/downloads"
            }).Value;

            Assert.Multiple(() =>
            {
                Assert.That(result.DownloadClientPathChecked, Is.False);
                Assert.That(result.DownloadClientTestError, Is.Null);
                Assert.That(result.MappedPath, Is.EqualTo("/downloads/"));
                Assert.That(result.IsMapped, Is.True);
                Assert.That(result.LocalPathExists, Is.True);
                Assert.That(result.LocalPathWritable, Is.True);
                Assert.That(result.MappedPathExists, Is.True);
                Assert.That(result.MappedPathWritable, Is.True);
            });
        }

        [Test]
        public void test_mapping_should_still_check_items_when_status_probe_fails()
        {
            var controller = CreateController(
                new List<OsPath>(),
                new List<DownloadClientItem> { new() { OutputPath = new OsPath("/downloads/Book") } },
                existingFolders: new[] { "/downloads/", "/downloads/Book" },
                writableFolders: new[] { "/downloads/", "/downloads/Book" },
                throwOnStatus: true);

            var result = controller.TestMapping(new RemotePathMappingTestResource
            {
                DownloadClientId = 7,
                Host = "download-host",
                RemotePath = "/data/usenet/complete",
                LocalPath = "/downloads"
            }).Value;

            Assert.Multiple(() =>
            {
                Assert.That(result.DownloadClientItemPathChecked, Is.True);
                Assert.That(result.DownloadClientItemMappedPath, Is.EqualTo("/downloads/Book"));
                Assert.That(result.DownloadClientItemPathExists, Is.True);
                Assert.That(result.DownloadClientTestError, Is.EqualTo("Could not probe 1 download client(s). See logs for details."));
                Assert.That(result.DownloadClientTestError, Does.Not.Contain("Download client status failed"));
            });
        }

        [Test]
        public void test_mapping_should_verify_actual_download_client_item_round_trip()
        {
            var controller = CreateController(
                new List<OsPath> { new("/downloads") },
                new List<DownloadClientItem> { new() { OutputPath = new OsPath("/downloads/Book") } },
                existingFolders: new[] { "/downloads/", "/downloads/Book" },
                writableFolders: new[] { "/downloads/", "/downloads/Book" });

            var result = controller.TestMapping(new RemotePathMappingTestResource
            {
                DownloadClientId = 7,
                Host = "download-host",
                RemotePath = "/data/usenet/complete",
                LocalPath = "/downloads"
            }).Value;

            Assert.Multiple(() =>
            {
                Assert.That(result.DownloadClientPathChecked, Is.True);
                Assert.That(result.DownloadClientPathMatched, Is.True);
                Assert.That(result.DownloadClientMatchedPath, Is.EqualTo("/data/usenet/complete"));
                Assert.That(result.DownloadClientItemPathChecked, Is.True);
                Assert.That(result.DownloadClientItemMappedPath, Is.EqualTo("/downloads/Book"));
                Assert.That(result.DownloadClientItemPathExists, Is.True);
                Assert.That(result.DownloadClientItemPathWritable, Is.True);
            });
        }

        [Test]
        public void test_mapping_should_warn_when_download_client_reports_no_matching_path()
        {
            var controller = CreateController(
                new List<OsPath> { new("/downloads") },
                new List<DownloadClientItem> { new() { OutputPath = new OsPath("/downloads/Book") } },
                existingFolders: new[] { "/wrong/", "/downloads/Book" },
                writableFolders: new[] { "/wrong/", "/downloads/Book" });

            var result = controller.TestMapping(new RemotePathMappingTestResource
            {
                DownloadClientId = 7,
                Host = "download-host",
                RemotePath = "/wrong",
                LocalPath = "/wrong"
            }).Value;

            Assert.Multiple(() =>
            {
                Assert.That(result.DownloadClientPathChecked, Is.True);
                Assert.That(result.DownloadClientPathMatched, Is.False);
                Assert.That(result.DownloadClientItemPathChecked, Is.False);
            });
        }

        private static RemotePathMappingController CreateController(
            List<OsPath> outputRootFolders,
            List<DownloadClientItem> items,
            IEnumerable<string> existingFolders,
            IEnumerable<string> writableFolders,
            IEnumerable<RemotePathMapping> mappings = null,
            bool throwOnStatus = false)
        {
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            ((DiskProviderProxy)(object)diskProvider).ExistingFolders = existingFolders.ToHashSet(StringComparer.InvariantCultureIgnoreCase);
            ((DiskProviderProxy)(object)diskProvider).WritableFolders = writableFolders.ToHashSet(StringComparer.InvariantCultureIgnoreCase);

            var rootFolderService = DispatchProxy.Create<IRootFolderService, RootFolderServiceProxy>();
            var runtimeInfo = DispatchProxy.Create<IRuntimeInfo, RuntimeInfoProxy>();

            var definition = new DownloadClientDefinition
            {
                Id = 7,
                Name = "sabnzb",
                Settings = new FakeDownloadClientSettings { Host = "download-host" }
            };

            var client = new FakeDownloadClient(outputRootFolders, items, throwOnStatus) { Definition = definition };
            var remotePathMappingService = new FakeRemotePathMappingService
            {
                Mappings = mappings?.ToList() ?? new List<RemotePathMapping>()
            };

            return new RemotePathMappingController(
                remotePathMappingService: remotePathMappingService,
                downloadClientFactory: new FakeDownloadClientFactory(definition, client),
                rootFolderService: rootFolderService,
                diskProvider: diskProvider,
                pathExistsValidator: new PathExistsValidator(diskProvider),
                mappedNetworkDriveValidator: new MappedNetworkDriveValidator(runtimeInfo, diskProvider));
        }
    }
}
