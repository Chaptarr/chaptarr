using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Core.HealthCheck.Checks;
using NzbDrone.Core.Localization;
using NzbDrone.Core.MediaFiles.Events;

namespace Chaptarr.Core.Test.HealthCheck.Checks
{
    [TestFixture]
    public class RemotePathMappingCheckFixture
    {
        private class DiskProviderProxy : DispatchProxy
        {
            public string ExistingFolder { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IDiskProvider.FolderExists))
                {
                    return string.Equals((string)args[0], ExistingFolder, StringComparison.Ordinal);
                }

                throw new NotImplementedException($"Test proxy does not implement IDiskProvider.{targetMethod?.Name}");
            }
        }

        private class DownloadClientProviderProxy : DispatchProxy
        {
            public IDownloadClient Client { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IProvideDownloadClient.GetDownloadClients))
                {
                    return new[] { Client };
                }

                throw new NotImplementedException($"Test proxy does not implement IProvideDownloadClient.{targetMethod?.Name}");
            }
        }

        private class ConfigServiceProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == "get_EnableCompletedDownloadHandling")
                {
                    return true;
                }

                throw new NotImplementedException($"Test proxy does not implement IConfigService.{targetMethod?.Name}");
            }
        }

        private class OsInfoProxy : DispatchProxy
        {
            public bool IsDocker { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    "get_IsDocker" => IsDocker,
                    "get_Name" => "Linux",
                    _ => throw new NotImplementedException($"Test proxy does not implement IOsInfo.{targetMethod?.Name}")
                };
            }
        }

        private class DownloadClientProxy : DispatchProxy
        {
            public DownloadClientDefinition Definition { get; set; }
            public DownloadClientInfo Status { get; set; }
            public DownloadClientItem Item { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    "get_Definition" => Definition,
                    "get_Name" => Definition.Name,
                    nameof(IDownloadClient.GetStatus) => Status,
                    nameof(IDownloadClient.GetItems) => new[] { Item },
                    _ => throw new NotImplementedException($"Test proxy does not implement IDownloadClient.{targetMethod?.Name}")
                };
            }
        }

        private sealed class StubLocalizationService : ILocalizationService
        {
            public Dictionary<string, string> GetLocalizationDictionary()
            {
                return new Dictionary<string, string>();
            }

            public string GetLocalizedString(string phrase)
            {
                return phrase switch
                {
                    "RemotePathMappingCheckFolderPermissions" => "Chaptarr can see but not access download directory {1}. Likely permissions error.",
                    "RemotePathMappingCheckDockerFolderMissing" => "You are using docker; download client {0} places downloads in {1} but this directory does not appear to exist inside the container. Review your remote path mappings and container volume settings.",
                    _ => phrase
                };
            }

            public string GetLocalizedString(string phrase, Dictionary<string, object> tokens)
            {
                return GetLocalizedString(phrase);
            }
        }

        [Test]
        public void should_return_permissions_error_with_path_when_import_folder_exists()
        {
            var subject = CreateSubject(folderExists: true, isDocker: false, out var item);

            var result = subject.Check(new TrackImportFailedEvent(null, null, true, item));

            Assert.That(result.Type, Is.EqualTo(HealthCheckResult.Error));
            Assert.That(result.Message, Does.Contain(item.OutputPath.FullPath));
            Assert.That(result.Message, Does.Contain("permissions error"));
        }

        [Test]
        public void should_return_missing_folder_mapping_error_in_docker()
        {
            var subject = CreateSubject(folderExists: false, isDocker: true, out var item);

            var result = subject.Check(new TrackImportFailedEvent(null, null, true, item));

            Assert.That(result.Type, Is.EqualTo(HealthCheckResult.Error));
            Assert.That(result.Message, Does.Contain(item.OutputPath.FullPath));
            Assert.That(result.Message, Does.Contain("container volume settings"));
            Assert.That(result.Message, Does.Not.Contain("permissions error"));
        }

        private static RemotePathMappingCheck CreateSubject(bool folderExists, bool isDocker, out DownloadClientItem item)
        {
            var path = Path.Combine(Path.GetTempPath(), "chaptarr-remote-path-test", "book");
            var definition = new DownloadClientDefinition { Name = "Test Client" };
            item = new DownloadClientItem
            {
                DownloadId = "download-id",
                OutputPath = new OsPath(path),
                DownloadClientInfo = new DownloadClientItemClientInfo { Name = definition.Name }
            };

            var downloadClient = DispatchProxy.Create<IDownloadClient, DownloadClientProxy>();
            var downloadClientProxy = (DownloadClientProxy)(object)downloadClient;
            downloadClientProxy.Definition = definition;
            downloadClientProxy.Status = new DownloadClientInfo { IsLocalhost = false };
            downloadClientProxy.Item = item;

            var downloadClientProvider = DispatchProxy.Create<IProvideDownloadClient, DownloadClientProviderProxy>();
            ((DownloadClientProviderProxy)(object)downloadClientProvider).Client = downloadClient;

            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            ((DiskProviderProxy)(object)diskProvider).ExistingFolder = folderExists ? path : null;

            var configService = DispatchProxy.Create<IConfigService, ConfigServiceProxy>();
            var osInfo = DispatchProxy.Create<IOsInfo, OsInfoProxy>();
            ((OsInfoProxy)(object)osInfo).IsDocker = isDocker;

            return new RemotePathMappingCheck(
                diskProvider,
                downloadClientProvider,
                configService,
                osInfo,
                LogManager.GetCurrentClassLogger(),
                new StubLocalizationService());
        }
    }
}
