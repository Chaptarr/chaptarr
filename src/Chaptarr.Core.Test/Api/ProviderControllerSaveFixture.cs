using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Chaptarr.Api.V1;
using Chaptarr.Api.V1.DownloadClient;
using Chaptarr.Api.V1.ImportLists;
using Chaptarr.Api.V1.Indexers;
using Chaptarr.Api.V1.Metadata;
using Chaptarr.Api.V1.Notifications;
using Chaptarr.Http.ClientSchema;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Mvc;
using NUnit.Framework;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients.Deluge;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Extras.Metadata.Consumers.AudioBookShelf;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Goodreads;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Newznab;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Notifications.Webhook;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Parser;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture(typeof(DownloadClientResource), typeof(DownloadClientBulkResource), typeof(DownloadClientDefinition), typeof(DownloadClientResourceMapper))]
    [TestFixture(typeof(ImportListResource), typeof(ImportListBulkResource), typeof(ImportListDefinition), typeof(ImportListResourceMapper))]
    [TestFixture(typeof(IndexerResource), typeof(IndexerBulkResource), typeof(IndexerDefinition), typeof(IndexerResourceMapper))]
    [TestFixture(typeof(NotificationResource), typeof(NotificationBulkResource), typeof(NotificationDefinition), typeof(NotificationResourceMapper))]
    [TestFixture(typeof(MetadataResource), typeof(MetadataBulkResource), typeof(MetadataDefinition), typeof(MetadataResourceMapper))]
    public class ProviderControllerSaveFixture<TResource, TBulkResource, TDefinition, TMapper>
        where TResource : ProviderResource<TResource>, new()
        where TBulkResource : ProviderBulkResource<TBulkResource>, new()
        where TDefinition : ProviderDefinition, new()
        where TMapper : ProviderResourceMapper<TResource, TDefinition>, new()
    {
        private sealed class Controller : ProviderControllerBase<TResource, TBulkResource, IProvider, TDefinition>
        {
            public Controller(IProviderFactory<IProvider, TDefinition> factory)
                : base(factory, "provider", new TMapper(), new ProviderBulkResourceMapper<TBulkResource, TDefinition>())
            {
            }
        }

        private Controller _controller;
        private TDefinition _saved;
        private int _tests;
        private int _writes;
        private bool _failTest;

        [SetUp]
        public void SetUp()
        {
            typeof(SchemaBuilder).GetField("_localizationService", BindingFlags.NonPublic | BindingFlags.Static)
                .SetValue(null, Proxy<ILocalizationService>((_, args) => args[0]));
            var definitions = new ProviderDefinition[]
            {
                new DownloadClientDefinition { Enable = true, Settings = new DelugeSettings { Password = "saved-secret" } },
                new ImportListDefinition
                {
                    EnableAutomaticAdd = true,
                    Settings = new GoodreadsBookshelfImportListSettings { UserId = "123", BookshelfIds = new[] { "to-read" } }
                },
                new IndexerDefinition
                {
                    EnableRss = true,
                    Settings = new NewznabSettings { BaseUrl = "https://indexer.example", ApiKey = "saved-secret" }
                },
                new NotificationDefinition { OnGrab = true, Settings = new WebhookSettings { Url = "https://192.0.2.1/hook", Password = "saved-secret" } },
                new MetadataDefinition { Enable = true, Settings = new AudioBookShelfMetadataSettings() }
            };
            _saved = (TDefinition)Array.Find(definitions, definition => definition is TDefinition);
            _saved.Id = 1;
            _saved.Name = "Existing";
            _saved.Implementation = _saved.Settings.GetType().Name.Replace("Settings", string.Empty);
            _saved.ImplementationName = _saved.Implementation;
            _tests = _writes = 0;
            _failTest = false;
            _controller = new Controller(Proxy<IProviderFactory<IProvider, TDefinition>>((method, args) =>
            {
                switch (method.Name)
                {
                    case "Get": return StoredCopy();
                    case "All": return new List<TDefinition> { StoredCopy() };
                    case "SetProviderCharacteristics":
                        var definition = (TDefinition)args[0];
                        definition.ImplementationName = definition.Implementation;
                        definition.Message = null;
                        switch (definition)
                        {
                            case DownloadClientDefinition client:
                                client.Protocol = DownloadProtocol.Torrent;
                                break;
                            case ImportListDefinition list:
                                list.ListType = ImportListType.Goodreads;
                                list.MinRefreshInterval = TimeSpan.FromHours(12);
                                break;
                            case IndexerDefinition indexer:
                                indexer.Protocol = DownloadProtocol.Usenet;
                                indexer.SupportsRss = indexer.SupportsSearch = true;
                                break;
                            case NotificationDefinition notification:
                                foreach (var property in typeof(NotificationDefinition).GetProperties().Where(p => p.Name.StartsWith("SupportsOn")))
                                    property.SetValue(notification, true);
                                break;
                        }
                        return null;
                    case "Test":
                        _tests++;
                        return _failTest ? new ValidationResult(new[] { new ValidationFailure("", "Connection unavailable") }) : new ValidationResult();
                    case "Update":
                        _writes++;
                        _saved = (TDefinition)args[0];
                        return null;
                    default: throw new NotImplementedException(method.Name);
                }
            }));
        }

        private TDefinition StoredCopy()
        {
            // A repository read does not acquire the transient characteristics added to a GET response.
            var copy = new TDefinition();
            foreach (var property in typeof(TDefinition).GetProperties().Where(p => p.CanWrite))
                property.SetValue(copy, property.GetValue(_saved));
            return copy;
        }

        private TResource Request() => STJson.Deserialize<TResource>(STJson.ToJson(_controller.GetAll().Single()));

        [Test]
        public void unchanged_save_succeeds_without_testing_or_writing_even_when_provider_is_unavailable()
        {
            var settings = _saved.Settings.ToJson();
            _failTest = true;
            var result = _controller.UpdateProvider(Request());
            Assert.That(((ObjectResult)result.Result).StatusCode, Is.EqualTo(202));
            Assert.That(_tests, Is.Zero);
            Assert.That(_writes, Is.Zero);
            Assert.That(_saved.Settings.ToJson(), Is.EqualTo(settings));
        }

        [Test]
        public void changed_save_tests_then_writes()
        {
            var request = Request();
            request.Name = "Changed";
            _controller.UpdateProvider(request);
            Assert.That(_tests, Is.EqualTo(1));
            Assert.That(_writes, Is.EqualTo(1));
            Assert.That(_saved.Name, Is.EqualTo("Changed"));
        }

        [Test]
        public void failed_changed_save_does_not_write()
        {
            var request = Request();
            request.Name = "Changed";
            _failTest = true;
            Assert.Throws<ValidationException>(() => _controller.UpdateProvider(request));
            Assert.That(_tests, Is.EqualTo(1));
            Assert.That(_writes, Is.Zero);
            Assert.That(_saved.Name, Is.EqualTo("Existing"));
        }

        [Test]
        public void explicit_test_runs_each_time_without_writing()
        {
            _controller.Test(Request());
            _controller.Test(Request());
            Assert.That(_tests, Is.EqualTo(2));
            Assert.That(_writes, Is.Zero);
        }

        [Test]
        public void forced_changed_save_still_bypasses_connection_test()
        {
            var request = Request();
            request.Name = "Changed";
            _failTest = true;
            _controller.UpdateProvider(request, forceSave: true);
            Assert.That(_tests, Is.Zero);
            Assert.That(_writes, Is.EqualTo(1));
        }
    }
}
