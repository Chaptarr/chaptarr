using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class ImportDiscoveredAuthorCommandHandlerMonitoringFixture
    {
        private class AuthorLibraryServiceProxy : DispatchProxy
        {
            public string ProviderId { get; private set; }
            public MonitoringConfig Config { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IAuthorLibraryService.AddAuthorAsync))
                {
                    ProviderId = (string)args[0];
                    Config = (MonitoringConfig)args[1];
                    return Task.FromResult(new Author { Id = 1, Name = "Discovered Author" });
                }

                throw new NotImplementedException(targetMethod?.Name);
            }
        }

        private class AuthorServiceProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IAuthorService.FindByProviderId))
                {
                    return null;
                }

                throw new NotImplementedException(targetMethod?.Name);
            }
        }

        private class RootFolderServiceProxy : DispatchProxy
        {
            public RootFolder RootFolder { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IRootFolderService.All))
                {
                    return new List<RootFolder> { RootFolder };
                }

                throw new NotImplementedException(targetMethod?.Name);
            }
        }

        private class EventAggregatorProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IEventAggregator.PublishEvent))
                {
                    return null;
                }

                throw new NotImplementedException(targetMethod?.Name);
            }
        }

        [Test]
        public void root_discovery_should_forward_each_media_sides_gate_initial_mode_and_later_row_policy()
        {
            var root = new RootFolder
            {
                Path = "/library",
                FolderType = FolderType.Mixed
            };
            root.SetAudiobookSettings(new MediaTypeSettings
            {
                QualityProfileId = 10,
                MetadataProfileId = 20,
                Monitored = false,
                MonitorExistingMode = MonitorTypes.Missing,
                MonitorNewItems = NewItemMonitorTypes.All,
                Tags = new List<int> { 10 }
            });
            root.SetEbookSettings(new MediaTypeSettings
            {
                QualityProfileId = 11,
                MetadataProfileId = 21,
                Monitored = true,
                MonitorExistingMode = MonitorTypes.Existing,
                MonitorNewItems = NewItemMonitorTypes.New,
                Tags = new List<int> { 20 }
            });

            var libraryService = DispatchProxy.Create<IAuthorLibraryService, AuthorLibraryServiceProxy>();
            var rootService = DispatchProxy.Create<IRootFolderService, RootFolderServiceProxy>();
            ((RootFolderServiceProxy)(object)rootService).RootFolder = root;
            var subject = new ImportDiscoveredAuthorCommandHandler(
                libraryService,
                DispatchProxy.Create<IAuthorService, AuthorServiceProxy>(),
                rootService,
                LogManager.GetCurrentClassLogger(),
                DispatchProxy.Create<IEventAggregator, EventAggregatorProxy>());

            subject.Execute(new ImportDiscoveredAuthorCommand
            {
                ProviderId = "hc:123",
                RootFolderPath = root.Path,
                DiscoveredAuthorFolderPath = "/library/Discovered Author"
            });

            var capture = (AuthorLibraryServiceProxy)(object)libraryService;
            Assert.Multiple(() =>
            {
                Assert.That(capture.ProviderId, Is.EqualTo("hc:123"));
                Assert.That(capture.Config.CreateAudiobook, Is.True);
                Assert.That(capture.Config.AudiobookMonitored, Is.False);
                Assert.That(capture.Config.AudiobookMonitorExistingMode, Is.EqualTo(MonitorTypes.Missing));
                Assert.That(capture.Config.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.All));
                Assert.That(capture.Config.AudiobookTags, Is.EquivalentTo(new[] { 10 }));
                Assert.That(capture.Config.CreateEbook, Is.True);
                Assert.That(capture.Config.EbookMonitored, Is.True);
                Assert.That(capture.Config.EbookMonitorExistingMode, Is.EqualTo(MonitorTypes.Existing));
                Assert.That(capture.Config.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.New));
                Assert.That(capture.Config.EbookTags, Is.EquivalentTo(new[] { 20 }));
                Assert.That(capture.Config.Tags, Is.Null);
                Assert.That(capture.Config.DiscoveredAuthorFolderPath, Is.EqualTo("/library/Discovered Author"));
            });
        }

        [Test]
        public void single_type_root_discovery_should_leave_the_other_media_sides_monitoring_unconfigured()
        {
            var root = new RootFolder
            {
                Path = "/audiobooks",
                FolderType = FolderType.Audiobook
            };
            root.SetAudiobookSettings(new MediaTypeSettings
            {
                QualityProfileId = 10,
                MetadataProfileId = 20,
                Monitored = false,
                MonitorExistingMode = MonitorTypes.Missing,
                MonitorNewItems = NewItemMonitorTypes.New
            });

            var libraryService = DispatchProxy.Create<IAuthorLibraryService, AuthorLibraryServiceProxy>();
            var rootService = DispatchProxy.Create<IRootFolderService, RootFolderServiceProxy>();
            ((RootFolderServiceProxy)(object)rootService).RootFolder = root;
            var subject = new ImportDiscoveredAuthorCommandHandler(
                libraryService,
                DispatchProxy.Create<IAuthorService, AuthorServiceProxy>(),
                rootService,
                LogManager.GetCurrentClassLogger(),
                DispatchProxy.Create<IEventAggregator, EventAggregatorProxy>());

            subject.Execute(new ImportDiscoveredAuthorCommand
            {
                ProviderId = "hc:456",
                RootFolderPath = root.Path,
                DiscoveredAuthorFolderPath = "/audiobooks/Discovered Author"
            });

            var config = ((AuthorLibraryServiceProxy)(object)libraryService).Config;
            Assert.Multiple(() =>
            {
                Assert.That(config.AudiobookMonitored, Is.False);
                Assert.That(config.AudiobookMonitorExistingMode, Is.EqualTo(MonitorTypes.Missing));
                Assert.That(config.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.New));
                Assert.That(config.EbookMonitored, Is.Null);
                Assert.That(config.EbookMonitorExistingMode, Is.Null);
                Assert.That(config.EbookMonitorNewItems, Is.Null);
            });
        }

        [Test]
        public void mixed_root_discovery_should_import_the_complete_side_without_the_incomplete_side()
        {
            var root = new RootFolder
            {
                Path = "/library".AsOsAgnostic(),
                FolderType = FolderType.Mixed
            };
            root.SetAudiobookSettings(new MediaTypeSettings
            {
                QualityProfileId = 10,
                MetadataProfileId = 20,
                Monitored = true
            });
            root.SetEbookSettings(new MediaTypeSettings
            {
                QualityProfileId = 11,
                Monitored = true
            });

            var libraryService = DispatchProxy.Create<IAuthorLibraryService, AuthorLibraryServiceProxy>();
            var rootService = DispatchProxy.Create<IRootFolderService, RootFolderServiceProxy>();
            ((RootFolderServiceProxy)(object)rootService).RootFolder = root;
            var subject = new ImportDiscoveredAuthorCommandHandler(
                libraryService,
                DispatchProxy.Create<IAuthorService, AuthorServiceProxy>(),
                rootService,
                LogManager.GetCurrentClassLogger(),
                DispatchProxy.Create<IEventAggregator, EventAggregatorProxy>());

            subject.Execute(new ImportDiscoveredAuthorCommand
            {
                ProviderId = "hc:789",
                RootFolderPath = root.Path,
                DiscoveredAuthorFolderPath = "/library/Discovered Author".AsOsAgnostic()
            });

            var config = ((AuthorLibraryServiceProxy)(object)libraryService).Config;
            Assert.Multiple(() =>
            {
                Assert.That(config.CreateAudiobook, Is.True);
                Assert.That(config.AudiobookRootFolderPath, Is.EqualTo(root.Path));
                Assert.That(config.CreateEbook, Is.False);
                Assert.That(config.EbookRootFolderPath, Is.Null);
                Assert.That(config.EbookQualityProfileId, Is.Null);
            });
        }
    }
}
