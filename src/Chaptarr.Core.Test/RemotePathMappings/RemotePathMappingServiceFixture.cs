using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Download;
using NzbDrone.Core.RemotePathMappings;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.ThingiProvider.Events;
using NzbDrone.Core.Validation;

namespace Chaptarr.Core.Test.RemotePathMappings
{
    [TestFixture]
    public class RemotePathMappingServiceFixture
    {
        private class DiskProviderProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IDiskProvider.FolderExists))
                {
                    return true;
                }

                if (targetMethod?.Name == nameof(IDiskProvider.FolderWritable))
                {
                    return true;
                }

                throw new NotImplementedException($"Test proxy does not implement IDiskProvider.{targetMethod?.Name}");
            }
        }

        private class RepositoryProxy : DispatchProxy
        {
            public List<RemotePathMapping> Mappings { get; set; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case nameof(IRemotePathMappingRepository.All):
                        return Mappings.ToList();

                    case nameof(IRemotePathMappingRepository.Get):
                        if (args[0] is int getId)
                        {
                            return Mappings.Single(m => m.Id == getId);
                        }

                        if (args[0] is IEnumerable<int> ids)
                        {
                            return Mappings.Where(m => ids.Contains(m.Id)).ToList();
                        }

                        break;

                    case nameof(IRemotePathMappingRepository.Find):
                        return Mappings.SingleOrDefault(m => m.Id == (int)args[0]);

                    case nameof(IRemotePathMappingRepository.Insert):
                        var inserted = (RemotePathMapping)args[0];
                        inserted.Id = Mappings.Count == 0 ? 1 : Mappings.Max(m => m.Id) + 1;
                        Mappings.Add(inserted);
                        return inserted;

                    case nameof(IRemotePathMappingRepository.Update):
                        var updated = (RemotePathMapping)args[0];
                        Mappings.RemoveAll(m => m.Id == updated.Id);
                        Mappings.Add(updated);
                        return updated;

                    case nameof(IRemotePathMappingRepository.Delete) when args[0] is int id:
                        Mappings.RemoveAll(m => m.Id == id);
                        return null;

                    case nameof(IRemotePathMappingRepository.Count):
                        return Mappings.Count;

                    case nameof(IRemotePathMappingRepository.HasItems):
                        return Mappings.Count > 0;

                }

                throw new NotImplementedException($"Test proxy does not implement IRemotePathMappingRepository.{targetMethod?.Name}");
            }
        }

        private class DownloadClientRepositoryProxy : DispatchProxy
        {
            public List<DownloadClientDefinition> DownloadClients { get; set; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case nameof(IDownloadClientRepository.Find):
                        return DownloadClients.SingleOrDefault(d => d.Id == (int)args[0]);

                    case nameof(IDownloadClientRepository.Get):
                        return DownloadClients.Single(d => d.Id == (int)args[0]);

                    case nameof(IDownloadClientRepository.All):
                        return DownloadClients.ToList();
                }

                throw new NotImplementedException($"Test proxy does not implement IDownloadClientRepository.{targetMethod?.Name}");
            }
        }

        private class DownloadClientSettings : IProviderConfig
        {
            public string Host { get; set; }

            public NzbDroneValidationResult Validate()
            {
                return new NzbDroneValidationResult();
            }
        }

        [Test]
        public void should_prefer_download_client_mapping_over_legacy_host_mapping()
        {
            var subject = CreateSubject(
                new RemotePathMapping { Id = 1, DownloadClientId = 0, Host = "download-host", RemotePath = "/downloads", LocalPath = "/local/legacy" },
                new RemotePathMapping { Id = 2, DownloadClientId = 7, Host = "download-host", RemotePath = "/downloads", LocalPath = "/local/client-seven" });

            var result = subject.RemapRemoteToLocal(7, "download-host", new OsPath("/downloads/book/file.m4b"));

            Assert.That(result.FullPath, Is.EqualTo("/local/client-seven/book/file.m4b"));
        }

        [Test]
        public void should_prefer_longest_download_client_remote_path_mapping_when_existing_rows_overlap()
        {
            var subject = CreateSubject(
                new RemotePathMapping { Id = 1, DownloadClientId = 7, Host = "download-host", RemotePath = "/downloads", LocalPath = "/local/all-downloads" },
                new RemotePathMapping { Id = 2, DownloadClientId = 7, Host = "download-host", RemotePath = "/downloads/complete", LocalPath = "/local/complete" });

            var result = subject.RemapRemoteToLocal(7, "download-host", new OsPath("/downloads/complete/book/file.m4b"));

            Assert.That(result.FullPath, Is.EqualTo("/local/complete/book/file.m4b"));
        }

        [Test]
        public void should_prefer_longest_download_client_local_path_mapping_when_existing_rows_overlap()
        {
            var subject = CreateSubject(
                new RemotePathMapping { Id = 1, DownloadClientId = 7, Host = "download-host", RemotePath = "/downloads", LocalPath = "/local" },
                new RemotePathMapping { Id = 2, DownloadClientId = 7, Host = "download-host", RemotePath = "/downloads/complete", LocalPath = "/local/complete" });

            var result = subject.RemapLocalToRemote(7, "download-host", new OsPath("/local/complete/book/file.m4b"));

            Assert.That(result.FullPath, Is.EqualTo("/downloads/complete/book/file.m4b"));
        }

        [Test]
        public void should_allow_same_host_and_remote_path_for_different_download_clients()
        {
            var subject = CreateSubjectWithClients(
                Array.Empty<RemotePathMapping>(),
                new[]
                {
                    CreateDownloadClient(1, "download-host"),
                    CreateDownloadClient(2, "download-host")
                });

            Assert.DoesNotThrow(() => subject.Add(new RemotePathMapping
            {
                DownloadClientId = 1,
                Host = "download-host",
                RemotePath = "/downloads",
                LocalPath = "/local/client-one"
            }));

            Assert.DoesNotThrow(() => subject.Add(new RemotePathMapping
            {
                DownloadClientId = 2,
                Host = "download-host",
                RemotePath = "/downloads",
                LocalPath = "/local/client-two"
            }));
        }

        [Test]
        public void should_reject_second_scoped_mapping_for_same_download_client()
        {
            var subject = CreateSubject(new RemotePathMapping
            {
                Id = 1,
                DownloadClientId = 7,
                Host = "download-host",
                RemotePath = "/downloads/",
                LocalPath = "/local/client-seven"
            });

            var ex = Assert.Throws<InvalidOperationException>(() => subject.Add(new RemotePathMapping
            {
                DownloadClientId = 7,
                Host = "other-host",
                RemotePath = "/downloads/complete",
                LocalPath = "/local/duplicate"
            }));

            Assert.That(ex.Message, Is.EqualTo("Download client already has a scoped remote path mapping."));
        }

        [Test]
        public void should_allow_updating_existing_scoped_mapping_for_same_download_client()
        {
            var subject = CreateSubject(new RemotePathMapping
            {
                Id = 1,
                DownloadClientId = 7,
                Host = "download-host",
                RemotePath = "/downloads/",
                LocalPath = "/local/client-seven"
            });

            Assert.DoesNotThrow(() => subject.Update(new RemotePathMapping
            {
                Id = 1,
                DownloadClientId = 7,
                Host = "posted-host",
                RemotePath = "/downloads/complete",
                LocalPath = "/local/complete"
            }));
        }

        [Test]
        public void should_allow_multiple_host_wide_mappings_for_same_host_when_remote_path_differs()
        {
            var subject = CreateSubject(new RemotePathMapping
            {
                Id = 1,
                DownloadClientId = 0,
                Host = "download-host",
                RemotePath = "/downloads/",
                LocalPath = "/local/downloads"
            });

            Assert.DoesNotThrow(() => subject.Add(new RemotePathMapping
            {
                DownloadClientId = 0,
                Host = "download-host",
                RemotePath = "/data/",
                LocalPath = "/local/data"
            }));
        }

        [Test]
        public void should_derive_host_from_download_client_when_adding_scoped_mapping()
        {
            var subject = CreateSubjectWithClients(
                Array.Empty<RemotePathMapping>(),
                new[] { CreateDownloadClient(7, "live-host") });

            var result = subject.Add(new RemotePathMapping
            {
                DownloadClientId = 7,
                Host = "posted-host",
                RemotePath = "/downloads",
                LocalPath = "/local/client-seven"
            });

            Assert.That(result.Host, Is.EqualTo("live-host"));
        }

        [Test]
        public void should_reject_scoped_mapping_when_download_client_has_no_host()
        {
            var subject = CreateSubjectWithClients(
                Array.Empty<RemotePathMapping>(),
                new[] { CreateDownloadClient(7, string.Empty) });

            var ex = Assert.Throws<ArgumentException>(() => subject.Add(new RemotePathMapping
            {
                DownloadClientId = 7,
                Host = "posted-host",
                RemotePath = "/downloads",
                LocalPath = "/local/client-seven"
            }));

            Assert.That(ex.Message, Is.EqualTo("Selected download client does not have a configured host."));
        }

        [Test]
        public void should_return_effective_host_from_live_download_client()
        {
            var subject = CreateSubjectWithClients(
                new[]
                {
                    new RemotePathMapping { Id = 1, DownloadClientId = 7, Host = "old-host", RemotePath = "/downloads", LocalPath = "/local/client-seven" }
                },
                new[] { CreateDownloadClient(7, "live-host") });

            var result = subject.All().Single();

            Assert.That(result.Host, Is.EqualTo("live-host"));
        }

        [Test]
        public void should_delete_scoped_mappings_when_download_client_is_deleted()
        {
            var subject = CreateSubject(
                new RemotePathMapping { Id = 1, DownloadClientId = 7, Host = "download-host", RemotePath = "/downloads", LocalPath = "/local/client-seven" },
                new RemotePathMapping { Id = 2, DownloadClientId = 0, Host = "download-host", RemotePath = "/downloads", LocalPath = "/local/legacy" },
                new RemotePathMapping { Id = 3, DownloadClientId = 8, Host = "download-host", RemotePath = "/downloads", LocalPath = "/local/client-eight" });

            subject.Handle(new ProviderDeletedEvent<IDownloadClient>(7));

            Assert.That(subject.All().Select(m => m.Id), Is.EquivalentTo(new[] { 2, 3 }));
        }

        [Test]
        public void should_keep_host_based_mapping_for_legacy_callers()
        {
            var subject = CreateSubject(
                new RemotePathMapping { Id = 1, DownloadClientId = 0, Host = "download-host", RemotePath = "/downloads", LocalPath = "/local/legacy" },
                new RemotePathMapping { Id = 2, DownloadClientId = 7, Host = "download-host", RemotePath = "/downloads", LocalPath = "/local/client-seven" });

            var result = subject.RemapRemoteToLocal("download-host", new OsPath("/downloads/book/file.m4b"));

            Assert.That(result.FullPath, Is.EqualTo("/local/legacy/book/file.m4b"));
        }

        [Test]
        public void should_fall_back_to_host_mapping_when_scoped_mapping_does_not_match()
        {
            var subject = CreateSubject(
                new RemotePathMapping { Id = 1, DownloadClientId = 7, Host = "download-host", RemotePath = "/other", LocalPath = "/local/client-seven" },
                new RemotePathMapping { Id = 2, DownloadClientId = 0, Host = "download-host", RemotePath = "/downloads", LocalPath = "/local/legacy" });

            var result = subject.RemapRemoteToLocal(7, "download-host", new OsPath("/downloads/book/file.m4b"));

            Assert.That(result.FullPath, Is.EqualTo("/local/legacy/book/file.m4b"));
        }

        [Test]
        public void should_map_local_to_remote_with_download_client_scope()
        {
            var subject = CreateSubject(
                new RemotePathMapping { Id = 1, DownloadClientId = 0, Host = "download-host", RemotePath = "/downloads", LocalPath = "/local/legacy" },
                new RemotePathMapping { Id = 2, DownloadClientId = 7, Host = "download-host", RemotePath = "/downloads", LocalPath = "/local/client-seven" });

            var result = subject.RemapLocalToRemote(7, "download-host", new OsPath("/local/client-seven/book/file.m4b"));

            Assert.That(result.FullPath, Is.EqualTo("/downloads/book/file.m4b"));
        }

        [Test]
        public void should_report_mapped_path_visibility_and_writability_when_testing_mapping()
        {
            var subject = CreateSubjectWithClients(
                Array.Empty<RemotePathMapping>(),
                new[] { CreateDownloadClient(7, "download-host") });

            var result = subject.Test(new RemotePathMapping
            {
                DownloadClientId = 7,
                Host = "posted-host",
                RemotePath = "/downloads",
                LocalPath = "/local/client-seven"
            });

            Assert.Multiple(() =>
            {
                Assert.That(result.MappedPath, Is.EqualTo("/local/client-seven/"));
                Assert.That(result.LocalPathExists, Is.True);
                Assert.That(result.LocalPathWritable, Is.True);
                Assert.That(result.MappedPathExists, Is.True);
                Assert.That(result.MappedPathWritable, Is.True);
            });
        }

        private static DownloadClientDefinition CreateDownloadClient(int id, string host)
        {
            return new DownloadClientDefinition
            {
                Id = id,
                Name = $"Client {id}",
                Settings = new DownloadClientSettings { Host = host }
            };
        }

        private static RemotePathMappingService CreateSubject(params RemotePathMapping[] mappings)
        {
            var downloadClients = mappings
                .Where(m => m.DownloadClientId > 0)
                .GroupBy(m => m.DownloadClientId)
                .Select(g => CreateDownloadClient(g.Key, g.First().Host))
                .ToArray();

            return CreateSubjectWithClients(mappings, downloadClients);
        }

        private static RemotePathMappingService CreateSubjectWithClients(IEnumerable<RemotePathMapping> mappings, IEnumerable<DownloadClientDefinition> downloadClients)
        {
            var repository = DispatchProxy.Create<IRemotePathMappingRepository, RepositoryProxy>();
            ((RepositoryProxy)(object)repository).Mappings = mappings.ToList();

            var downloadClientRepository = DispatchProxy.Create<IDownloadClientRepository, DownloadClientRepositoryProxy>();
            ((DownloadClientRepositoryProxy)(object)downloadClientRepository).DownloadClients = downloadClients.ToList();

            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();

            return new RemotePathMappingService(
                downloadClientRepository: downloadClientRepository,
                remotePathMappingRepository: repository,
                diskProvider: diskProvider,
                cacheManager: new CacheManager(),
                logger: LogManager.GetCurrentClassLogger());
        }
    }
}
