using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Chaptarr.Api.V1;
using Chaptarr.Api.V1.Indexers;
using Chaptarr.Http.ClientSchema;
using Chaptarr.Core.Test.Common;
using NzbDrone.Core.Messaging.Events;
using FluentValidation.Results;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Newznab;
using NzbDrone.Core.Localization;
using NzbDrone.Core.ThingiProvider;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture]
    public class IndexerSettingsTestFixture
    {
        private sealed class Controller : ProviderControllerBase<IndexerResource, IndexerBulkResource, IIndexer, IndexerDefinition>
        {
            public Controller(IProviderFactory<IIndexer, IndexerDefinition> factory)
                : base(factory, "indexer", new IndexerResourceMapper(), new IndexerBulkResourceMapper()) { }
        }

        private IndexerDefinition _saved;
        private Controller _controller;
        private int _tests;
        private int _writes;
        private bool _failTest;
        private readonly IndexerResourceMapper _mapper = new();
        private IndexerResource Request() => STJson.Deserialize<IndexerResource>(STJson.ToJson(_mapper.ToResource(_saved)));

        [SetUp]
        public void SetUp()
        {
            typeof(SchemaBuilder).GetField("_localizationService", BindingFlags.NonPublic | BindingFlags.Static)
                .SetValue(null, Proxy<ILocalizationService>((_, args) => args[0]));
            _tests = _writes = 0;
            _failTest = false;
            _saved = new IndexerDefinition
            {
                Id = 1, Name = "Existing", Implementation = "Newznab", ImplementationName = "Newznab",
                EnableRss = true, SupportsSearch = true, SupportsRss = true,
                Settings = new NewznabSettings { BaseUrl = "https://indexer.example", ApiKey = "saved-secret", EnableNarratorMetadata = true }
            };
            var factory = Proxy<IProviderFactory<IIndexer, IndexerDefinition>>((method, args) =>
            {
                switch (method.Name)
                {
                    case "Get": return _saved;
                    case "All": return new List<IndexerDefinition> { _saved };
                    case "SetProviderCharacteristics": return null;
                    case "Test":
                        _tests++;
                        ((IndexerDefinition)args[0]).SupportsNarratorMetadata = false;
                        return _failTest ? new ValidationResult(new[] { new ValidationFailure("", "Connection unavailable") }) : new ValidationResult();
                    case "Create":
                        _writes++;
                        _saved = (IndexerDefinition)args[0];
                        _saved.Id = 1;
                        return _saved;
                    case "Update":
                        _writes++;
                        _saved = (IndexerDefinition)args[0];
                        return null;
                    default: throw new NotImplementedException(method.Name);
                }
            });
            _controller = new Controller(factory);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void capability_is_transient_and_saved_opt_in_survives_repository_roundtrip(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var repository = new IndexerRepository(database, Proxy<IEventAggregator>((_, _) => null));
                _saved.Id = 0;
                _saved.SupportsNarratorMetadata = false;
                repository.Insert(_saved);
                var stored = repository.Get(_saved.Id);
                Assert.That(stored.SupportsNarratorMetadata, Is.Null);
                Assert.That(((NewznabSettings)stored.Settings).EnableNarratorMetadata, Is.True);
                Assert.That(((NewznabSettings)stored.Settings).ApiKey, Is.EqualTo("saved-secret"));
            });
        }

        [TestCase(true)]
        [TestCase(false)]
        public void opening_and_unchanged_save_preserve_choice_and_never_test(bool enabled)
        {
            ((NewznabSettings)_saved.Settings).EnableNarratorMetadata = enabled;
            _failTest = true;
            var resource = STJson.Deserialize<IndexerResource>(STJson.ToJson(_controller.GetAll().Single()));
            Assert.That(_tests, Is.Zero);
            _controller.UpdateProvider(STJson.Deserialize<IndexerResource>(STJson.ToJson(resource)));
            Assert.That(_tests, Is.Zero);
            Assert.That(_writes, Is.Zero);
            Assert.That(((NewznabSettings)_saved.Settings).EnableNarratorMetadata, Is.EqualTo(enabled));
            Assert.That(((NewznabSettings)_saved.Settings).ApiKey, Is.EqualTo("saved-secret"));
        }

        [TestCase("apiKey")]
        [TestCase("baseUrl")]
        [TestCase("enableNarratorMetadata")]
        [TestCase("proxyId")]
        [TestCase("name")]
        public void changed_settings_are_tested_and_saved(string changed)
        {
            var resource = Request();
            switch (changed)
            {
                case "proxyId": resource.ProxyId = 5; break;
                case "name": resource.Name = "Changed"; break;
                default:
                    resource.Fields.Single(f => f.Name == changed).Value = changed switch
                    {
                        "apiKey" => "new-secret", "baseUrl" => "https://changed.example", _ => false
                    };
                    break;
            }

            _controller.UpdateProvider(STJson.Deserialize<IndexerResource>(STJson.ToJson(resource)));
            Assert.That(_tests, Is.EqualTo(1));
            Assert.That(_writes, Is.EqualTo(1));
            Assert.That(((NewznabSettings)_saved.Settings).ApiKey, Is.EqualTo(changed == "apiKey" ? "new-secret" : "saved-secret"));
        }

        [Test]
        public void manual_test_is_not_skipped_and_does_not_mutate_saved_settings()
        {
            var resource = Request();
            resource.Fields.Single(f => f.Name == "enableNarratorMetadata").Value = false;
            var result = JObject.FromObject(_controller.Test(STJson.Deserialize<IndexerResource>(STJson.ToJson(resource))));
            Assert.That(_tests, Is.EqualTo(1));
            Assert.That(_writes, Is.Zero);
            Assert.That(((NewznabSettings)_saved.Settings).EnableNarratorMetadata, Is.True);
            Assert.That(result["resource"]["SupportsNarratorMetadata"].Value<bool>(), Is.False);
            Assert.That(result.ToString(), Does.Not.Contain("saved-secret"));
        }

        [Test]
        public void unchanged_manual_test_is_deliberate_and_runs_every_time()
        {
            var resource = Request();
            _controller.Test(STJson.Deserialize<IndexerResource>(STJson.ToJson(resource)));
            _controller.Test(STJson.Deserialize<IndexerResource>(STJson.ToJson(resource)));
            Assert.That(_tests, Is.EqualTo(2));
            Assert.That(_writes, Is.Zero);
        }

        [Test]
        public void failed_changed_save_does_not_change_saved_settings()
        {
            var resource = Request();
            resource.Fields.Single(f => f.Name == "enableNarratorMetadata").Value = false;
            _failTest = true;
            Assert.Throws<FluentValidation.ValidationException>(() => _controller.UpdateProvider(resource));
            Assert.That(((NewznabSettings)_saved.Settings).EnableNarratorMetadata, Is.True);
            Assert.That(_writes, Is.Zero);
        }

        [Test]
        public void new_enabled_indexer_is_tested_on_save()
        {
            var resource = Request();
            resource.Id = 0;
            resource.Fields.Single(f => f.Name == "apiKey").Value = "new-secret";
            _controller.CreateProvider(STJson.Deserialize<IndexerResource>(STJson.ToJson(resource)));
            Assert.That(_tests, Is.EqualTo(1));
            Assert.That(_writes, Is.EqualTo(1));
        }
    }
}
