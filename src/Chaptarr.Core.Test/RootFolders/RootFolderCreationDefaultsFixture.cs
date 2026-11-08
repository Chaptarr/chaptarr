using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Chaptarr.Api.V1.RootFolders;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
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

        private IDiskProvider _disk;
        private bool _writable;
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
            _writable = true;
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

            _disk = Proxy<IDiskProvider>((method, args) => method.Name switch
            {
                nameof(IDiskProvider.FolderExists) => true,
                nameof(IDiskProvider.FolderWritable) => _writable,
                nameof(IDiskProvider.GetAvailableSpace) => 1000L,
                nameof(IDiskProvider.GetTotalSize) => 2000L,
                _ => throw new NotImplementedException(method.Name)
            });

            _service = new RootFolderService(
                repository,
                Proxy<IIngestQueueRepository>((method, args) => null),
                _disk,
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
        public void inaccessible_root_has_a_typed_validation_error_and_is_not_saved()
        {
            _writable = false;
            var error = Assert.Throws<FluentValidation.ValidationException>(() => _service.Add(DeserializeRoot(FolderType.Audiobook)));
            Assert.That(error.Errors, Has.Some.Matches<FluentValidation.Results.ValidationFailure>(failure =>
                failure.PropertyName == "Path" && failure.ErrorCode == "RootFolderNotWritable"));
            Assert.That(_stored, Is.Null);
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

        [TestCase(false, BookMediaType.Audiobook)]
        [TestCase(true, BookMediaType.Audiobook)]
        [TestCase(false, BookMediaType.Ebook)]
        [TestCase(true, BookMediaType.Ebook)]
        [NonParallelizable]
        public void persisted_root_choices_should_survive_api_read_modify_write(bool postgres, BookMediaType side)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var roots = new RootFolderRepository(database, Proxy<IEventAggregator>((method, args) => null));
                var service = new RootFolderService(roots, Proxy<IIngestQueueRepository>((method, args) => null),
                    _disk, Proxy<IManageCommandQueue>((method, args) => null), LogManager.GetCurrentClassLogger());
                var choices = new (bool? Monitored, MonitorTypes? Initial, NewItemMonitorTypes? Future)[]
                {
                    (false, null, NewItemMonitorTypes.None),
                    (true, null, NewItemMonitorTypes.New),
                    (null, MonitorTypes.All, NewItemMonitorTypes.All),
                    (true, MonitorTypes.None, null),
                    (null, null, null),
                    (false, MonitorTypes.None, NewItemMonitorTypes.None),
                    (true, MonitorTypes.All, NewItemMonitorTypes.All),
                    (true, MonitorTypes.Missing, NewItemMonitorTypes.New),
                    (false, MonitorTypes.Existing, NewItemMonitorTypes.None)
                };
                var rootIndex = 0;
                foreach (var type in new[] { FolderType.Mixed, side == BookMediaType.Audiobook ? FolderType.Audiobook : FolderType.Ebook })
                {
                    foreach (var choice in choices)
                    {
                        var root = new RootFolder { Name = "Configured root", Path = Path.Combine(@"C:\Library".AsOsAgnostic(), (++rootIndex).ToString()), FolderType = type };
                        var settings = new MediaTypeSettings { QualityProfileId = 2, MetadataProfileId = 3,
                            Monitored = choice.Monitored, MonitorExistingMode = choice.Initial, MonitorNewItems = choice.Future };
                        if (side == BookMediaType.Audiobook) root.SetAudiobookSettings(settings);
                        else root.SetEbookSettings(settings);
                        roots.Insert(root);
                        var saved = roots.Get(root.Id);
                        var response = JsonSerializer.Serialize(saved.ToResource(), STJson.GetSerializerSettings());
                        var request = JsonSerializer.Deserialize<RootFolderResource>(response, STJson.GetSerializerSettings());
                        request.Name = "Updated root";
                        service.Update(request.ToModel());
                        var updated = roots.Get(root.Id);
                        var actual = side == BookMediaType.Audiobook ? updated.GetAudiobookSettings() : updated.GetEbookSettings();
                        var resolved = new RootFolderSettingsResolver(null).ResolveSettings(updated, side);
                        Assert.Multiple(() =>
                        {
                            Assert.That(actual.Monitored, Is.EqualTo(choice.Monitored));
                            Assert.That(actual.MonitorExistingMode, Is.EqualTo(choice.Initial));
                            Assert.That(actual.MonitorNewItems, Is.EqualTo(choice.Future));
                            Assert.That(resolved.IsConfigured, Is.True);
                            Assert.That(resolved.MonitorExistingMode, Is.EqualTo(choice.Initial));
                            Assert.That(actual.QualityProfileId, Is.EqualTo(2));
                            Assert.That(actual.MetadataProfileId, Is.EqualTo(3));
                            Assert.That(updated.Name, Is.EqualTo("Updated root"));
                            var opposite = side == BookMediaType.Audiobook ? updated.GetEbookSettings() : updated.GetAudiobookSettings();
                            Assert.That(opposite?.Monitored, Is.Null);
                            Assert.That(opposite?.MonitorExistingMode, Is.Null);
                            Assert.That(opposite?.MonitorNewItems, Is.Null);
                            Assert.That(opposite?.QualityProfileId, Is.Null);
                            Assert.That(opposite?.MetadataProfileId, Is.Null);
                        });
                    }
                }
            });
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        [NonParallelizable]
        public void persisted_legacy_root_inputs_should_use_the_existing_conversion(bool postgres, bool structured)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var roots = new RootFolderRepository(database, Proxy<IEventAggregator>((method, args) => null));
                var service = new RootFolderService(roots, Proxy<IIngestQueueRepository>((method, args) => null),
                    _disk, Proxy<IManageCommandQueue>((method, args) => null), LogManager.GetCurrentClassLogger());
                foreach (var side in new[] { "audiobook", "ebook" })
                {
                    foreach (var existing in new[] { 0, 1, 2 })
                    {
                        foreach (var future in new[] { false, true })
                        {
                            var fields = structured
                                ? $",\"{side}\":{{\"qualityProfileId\":2,\"metadataProfileId\":3,\"monitorExisting\":{existing},\"monitorFuture\":{future.ToString().ToLowerInvariant()}}}"
                                : $",\"{side}QualityProfileId\":2,\"{side}MetadataProfileId\":3,\"{side}MonitorExisting\":{existing},\"{side}MonitorFuture\":{future.ToString().ToLowerInvariant()}";
                            var type = side == "audiobook" ? FolderType.Audiobook : FolderType.Ebook;
                            var root = DeserializeRoot(type, fields);
                            root.Path = Path.Combine(root.Path, $"{side}-{existing}-{future}");
                            roots.Insert(root);
                            var response = JsonSerializer.Serialize(roots.Get(root.Id).ToResource(), STJson.GetSerializerSettings());
                            var request = JsonSerializer.Deserialize<RootFolderResource>(response, STJson.GetSerializerSettings());
                            service.Update(request.ToModel());
                            var updated = roots.Get(root.Id);
                            var settings = side == "audiobook" ? updated.GetAudiobookSettings() : updated.GetEbookSettings();
                            var expectedInitial = existing == 1 ? MonitorTypes.All : existing == 2 || future ? MonitorTypes.None : (MonitorTypes?)null;
                            var expectedFuture = existing == 1 ? NewItemMonitorTypes.All : future ? NewItemMonitorTypes.New : existing == 2 ? NewItemMonitorTypes.None : (NewItemMonitorTypes?)null;
                            Assert.Multiple(() =>
                            {
                                Assert.That(settings.Monitored, Is.EqualTo(existing != 0 || future));
                                Assert.That(settings.MonitorExistingMode, Is.EqualTo(expectedInitial));
                                Assert.That(settings.MonitorNewItems, Is.EqualTo(expectedFuture));
                                Assert.That(side == "audiobook" ? updated.GetEbookSettings() : updated.GetAudiobookSettings(), Is.Null);
                            });
                        }
                    }
                }
            });
        }
    }
}
