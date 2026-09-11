using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using Chaptarr.Api.V1.RootFolders;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.RootFolders
{
    [TestFixture]
    public class RootFolderCreationDefaultsFixture
    {
        public class RecordingProxy : DispatchProxy
        {
            public Func<MethodInfo, object[], object> Handler { get; set; }

            protected override object Invoke(MethodInfo method, object[] args) => Handler(method, args);
        }

        private RootFolder _stored;
        private RootFolderService _service;

        private static T Proxy<T>(Func<MethodInfo, object[], object> handler)
            where T : class
        {
            var proxy = DispatchProxy.Create<T, RecordingProxy>();
            ((RecordingProxy)(object)proxy).Handler = handler;
            return proxy;
        }

        [SetUp]
        public void Setup()
        {
            _stored = null;
            var repository = Proxy<IRootFolderRepository>((method, args) =>
            {
                switch (method.Name)
                {
                    case "All":
                        return _stored == null ? new List<RootFolder>() : new List<RootFolder> { _stored };
                    case "Insert":
                    case "Update":
                        var root = (RootFolder)args[0];
                        root.Id = 1;
                        // Copy the persisted JSON, so assertions do not merely read the input object.
                        _stored = new RootFolder
                        {
                            Id = root.Id,
                            Name = root.Name,
                            Path = root.Path,
                            FolderType = root.FolderType,
                            AudiobookSettings = root.AudiobookSettings,
                            EbookSettings = root.EbookSettings
                        };
                        return method.ReturnType == typeof(void) ? null : root;
                    default:
                        throw new NotImplementedException(method.Name);
                }
            });

            var disk = Proxy<IDiskProvider>((method, args) => method.Name switch
            {
                nameof(IDiskProvider.FolderExists) => true,
                nameof(IDiskProvider.FolderWritable) => true,
                nameof(IDiskProvider.GetAvailableSpace) => 1000L,
                nameof(IDiskProvider.GetTotalSize) => 2000L,
                _ => throw new NotImplementedException(method.Name)
            });

            _service = new RootFolderService(
                repository,
                Proxy<IIngestQueueRepository>((method, args) => null),
                disk,
                Proxy<IManageCommandQueue>((method, args) => null),
                LogManager.GetCurrentClassLogger());
        }

        private static RootFolder DeserializeRoot(FolderType type, string fields = "")
        {
            var path = JsonSerializer.Serialize(@"C:\Library".AsOsAgnostic());
            return JsonSerializer.Deserialize<RootFolderResource>(
                $"{{\"name\":\"Library\",\"path\":{path},\"folderType\":{(int)type}{fields}}}",
                STJson.GetSerializerSettings()).ToModel();
        }

        [Test]
        public void new_root_defaults_should_round_trip_through_api_and_settings_json(
            [Values(FolderType.Mixed, FolderType.Audiobook, FolderType.Ebook)] FolderType type,
            [Values] bool withProfiles)
        {
            var fields = string.Empty;
            foreach (var side in new[] { "audiobook", "ebook" })
            {
                if (withProfiles && (type == FolderType.Mixed || (type == FolderType.Audiobook) == (side == "audiobook")))
                {
                    fields += $",\"{side}\":{{\"qualityProfileId\":2,\"metadataProfileId\":3}}";
                }
            }

            _service.Add(DeserializeRoot(type, fields));
            var response = JsonSerializer.SerializeToElement(_stored.ToResource(), STJson.GetSerializerSettings());
            foreach (var side in new[] { "audiobook", "ebook" })
            {
                var settings = side == "audiobook" ? _stored.GetAudiobookSettings() : _stored.GetEbookSettings();
                if (type != FolderType.Mixed && (type == FolderType.Audiobook) != (side == "audiobook"))
                {
                    Assert.That(settings, Is.Null, "Do not configure the unsupported media side");
                    continue;
                }

                Assert.That(settings, Is.Not.Null);
                Assert.Multiple(() =>
                {
                    Assert.That(settings.Monitored, Is.True);
                    Assert.That(settings.MonitorExistingMode, Is.EqualTo(MonitorTypes.None));
                    Assert.That(settings.MonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
                    Assert.That(settings.QualityProfileId, Is.EqualTo(withProfiles ? 2 : (int?)null));
                    Assert.That(response.GetProperty(side + "Monitored").GetBoolean(), Is.True);
                    Assert.That(response.GetProperty(side + "MonitorExistingMode").GetString(), Is.EqualTo("none"));
                    Assert.That(response.GetProperty(side + "MonitorNewItems").GetString(), Is.EqualTo("none"));
                    Assert.That(response.GetProperty(side).GetProperty("monitorNewItems").GetString(), Is.EqualTo("none"));
                });
            }
        }

        [Test]
        public void explicit_settings_on_each_media_side_should_win([Values] bool structured)
        {
            var fields = structured
                ? ",\"audiobook\":{\"monitored\":false,\"monitorExistingMode\":\"missing\",\"monitorNewItems\":\"new\"},\"ebook\":{\"monitored\":true,\"monitorExistingMode\":\"all\",\"monitorNewItems\":\"all\"}"
                : ",\"audiobookMonitored\":false,\"audiobookMonitorExistingMode\":\"missing\",\"audiobookMonitorNewItems\":\"new\",\"ebookMonitored\":true,\"ebookMonitorExistingMode\":\"all\",\"ebookMonitorNewItems\":\"all\"";
            var root = DeserializeRoot(FolderType.Mixed, fields);
            var audio = root.AudiobookSettings;
            var ebook = root.EbookSettings;

            _service.Add(root);

            Assert.That(_stored.AudiobookSettings, Is.EqualTo(audio));
            Assert.That(_stored.EbookSettings, Is.EqualTo(ebook));
        }

        [TestCase(FolderType.Audiobook, "audiobook")]
        [TestCase(FolderType.Ebook, "ebook")]
        public void legacy_explicit_all_should_remain_all(FolderType type, string side)
        {
            _service.Add(DeserializeRoot(type, $",\"{side}MonitorExisting\":1"));

            var settings = side == "audiobook" ? _stored.GetAudiobookSettings() : _stored.GetEbookSettings();
            Assert.That(settings.Monitored, Is.True);
            Assert.That(settings.MonitorExistingMode, Is.EqualTo(MonitorTypes.All));
            Assert.That(settings.MonitorNewItems, Is.EqualTo(NewItemMonitorTypes.All));
        }

        [TestCase(null)]
        [TestCase("{}")]
        [TestCase("{\"Monitored\":false,\"MonitorExistingMode\":\"All\",\"MonitorNewItems\":\"All\"}")]
        public void reading_and_updating_existing_roots_should_not_apply_creation_defaults(string settingsJson)
        {
            var root = DeserializeRoot(FolderType.Mixed);
            root.AudiobookSettings = settingsJson;
            root.EbookSettings = settingsJson;

            root.ToResource();
            _service.Update(root);
            _service.GetBestRootFolder(root.Path);

            Assert.That(_stored.AudiobookSettings, Is.EqualTo(settingsJson));
            Assert.That(_stored.EbookSettings, Is.EqualTo(settingsJson));
        }
    }
}
