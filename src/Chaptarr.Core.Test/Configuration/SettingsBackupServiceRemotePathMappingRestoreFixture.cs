using System;
using System.Collections.Generic;
using System.Reflection;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration.SettingsBackups;
using NzbDrone.Core.Download;
using NzbDrone.Core.RemotePathMappings;
using NUnit.Framework;

namespace Chaptarr.Core.Test.Configuration
{
    [TestFixture]
    public class SettingsBackupServiceRemotePathMappingRestoreFixture
    {
        private class DownloadClientFactoryProxy : DispatchProxy
        {
            public List<DownloadClientDefinition> Definitions { get; set; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IDownloadClientFactory.All))
                {
                    return Definitions;
                }

                throw new NotImplementedException($"Test proxy does not implement IDownloadClientFactory.{targetMethod?.Name}");
            }
        }

        private class RemotePathMappingServiceProxy : DispatchProxy
        {
            public List<RemotePathMapping> Added { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case nameof(IRemotePathMappingService.All):
                        return new List<RemotePathMapping>();

                    case nameof(IRemotePathMappingService.Add):
                        var mapping = (RemotePathMapping)args[0];
                        Added.Add(mapping);
                        return mapping;
                }

                throw new NotImplementedException($"Test proxy does not implement IRemotePathMappingService.{targetMethod?.Name}");
            }
        }

        [Test]
        public void restore_should_skip_a_scoped_mapping_when_only_an_unrelated_client_reuses_the_source_id()
        {
            var downloadClientFactory = DispatchProxy.Create<IDownloadClientFactory, DownloadClientFactoryProxy>();
            ((DownloadClientFactoryProxy)(object)downloadClientFactory).Definitions = new List<DownloadClientDefinition>
            {
                new() { Id = 42, Name = "Unrelated client" }
            };
            var remotePathMappingService = DispatchProxy.Create<IRemotePathMappingService, RemotePathMappingServiceProxy>();
            var service = new SettingsBackupService(
                null,
                null,
                null,
                null,
                null,
                downloadClientFactory,
                null,
                null,
                null,
                null,
                null,
                null,
                remotePathMappingService);
            var package = new SettingsBackupPackage
            {
                RemotePathMappings = new List<RemotePathMappingBackup>
                {
                    new()
                    {
                        DownloadClientId = 42,
                        DownloadClientName = "Original client",
                        Host = "download-host",
                        RemotePath = "/downloads".AsOsAgnostic(),
                        LocalPath = "/data/downloads".AsOsAgnostic()
                    }
                }
            };
            var result = new SettingsBackupRestoreResult();

            InvokeRestoreRemotePathMappings(service, package, result);

            Assert.Multiple(() =>
            {
                Assert.That(((RemotePathMappingServiceProxy)(object)remotePathMappingService).Added, Is.Empty);
                Assert.That(result.Warnings, Has.Count.EqualTo(1));
                Assert.That(result.Warnings[0], Does.Contain("Original client"));
                Assert.That(result.Warnings[0], Does.Contain("could not be resolved"));
            });
        }

        private static void InvokeRestoreRemotePathMappings(
            SettingsBackupService service,
            SettingsBackupPackage package,
            SettingsBackupRestoreResult result)
        {
            var method = typeof(SettingsBackupService).GetMethod("RestoreRemotePathMappings", BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.That(method, Is.Not.Null);
            method.Invoke(service, new object[]
            {
                package,
                new Dictionary<int, int>(),
                SettingsBackupRestoreMode.Merge,
                result
            });
        }
    }
}
