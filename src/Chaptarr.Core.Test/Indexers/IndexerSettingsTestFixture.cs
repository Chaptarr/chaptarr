using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using Chaptarr.Api.V1.Indexers;
using Chaptarr.Http.ClientSchema;
using Chaptarr.Core.Test.Common;
using NzbDrone.Core.Messaging.Events;
using FluentValidation.Results;
using Newtonsoft.Json.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NzbDrone.Core.Indexers.Newznab;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Validation;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture]
    public class IndexerSettingsTestFixture
    {
        private IndexerDefinition _saved;
        private IndexerController _controller;
        private IIndexerFactory _factory;
        private CacheManager _cache;
        private int _tests;
        private int _writes;
        private bool _failTest;
        private Func<IndexerDefinition, ValidationResult> _testMamConnection;
        private readonly IndexerResourceMapper _mapper = new();
        private IndexerResource Request() => AsApiPayload(_mapper.ToResource(_saved));

        private static IndexerResource AsApiPayload(IndexerResource resource) => STJson.Deserialize<IndexerResource>(STJson.ToJson(resource));

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
            _testMamConnection = null;
            _cache = new CacheManager();
            _factory = Proxy<IIndexerFactory>((method, args) =>
            {
                switch (method.Name)
                {
                    case "Get": return _saved;
                    case "All": return new List<IndexerDefinition> { _saved };
                    case "SetProviderCharacteristics": return null;
                    case "Test":
                        _tests++;
                        var definition = (IndexerDefinition)args[0];
                        definition.SupportsNarratorMetadata = false;
                        if (definition.Settings is MyAnonaMouseSettings && _testMamConnection != null)
                        {
                            return _testMamConnection(definition);
                        }

                        if (definition.Settings is MyAnonaMouseSettings settings && !_failTest)
                        {
                            settings.MamId = "rotated-cookie";
                            settings.UserId = "42";
                            settings.UserClass = "VIP";
                            settings.IsVip = true;
                            settings.UnsatisfiedCount = 3;
                            settings.UnsatisfiedLimit = 50;
                            settings.UnsatisfiedSnapshotUtc = DateTime.UtcNow;
                            settings.UnsatisfiedStatusRefreshedUtc = DateTime.UtcNow;
                        }

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
            _controller = NewController();
        }

        private IndexerController NewController() => new(_factory, new DownloadClientExistsValidator(null), _cache);

        private MyAnonaMouseSettings UseMamIndexer()
        {
            var settings = new MyAnonaMouseSettings { BaseUrl = "https://mam.example", MamId = "saved-cookie" };
            _saved.Implementation = "MyAnonaMouse";
            _saved.ImplementationName = "MyAnonaMouse";
            _saved.Settings = settings;
            return settings;
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

        [TestCase(false)]
        [TestCase(true)]
        public void mam_save_reuses_successful_test_for_the_same_connection(bool creating)
        {
            UseMamIndexer().UnsatisfiedStatusRefreshedUtc = DateTime.UtcNow.AddHours(-1);
            var resource = Request();
            resource.Id = creating ? 0 : _saved.Id;
            resource.Name = creating ? "New MAM" : _saved.Name;
            resource.Fields.Single(field => field.Name == "mamId").Value = "new-cookie";
            var result = JObject.FromObject(_controller.Test(AsApiPayload(resource)));
            Assert.That(result.ToString(), Does.Not.Contain("rotated-cookie"));
            Assert.That(((MyAnonaMouseSettings)_saved.Settings).MamId, Is.EqualTo("saved-cookie"));
            Assert.That(_writes, Is.Zero);

            resource.Fields.Single(field => field.Name == "unsatisfiedSlotReserve").Value = 2;
            _failTest = true;
            if (creating)
            {
                NewController().CreateProvider(AsApiPayload(resource));
            }
            else
            {
                NewController().UpdateProvider(AsApiPayload(resource));
            }

            var stored = (MyAnonaMouseSettings)_saved.Settings;
            Assert.Multiple(() =>
            {
                Assert.That(_tests, Is.EqualTo(1));
                Assert.That(_writes, Is.EqualTo(1));
                Assert.That(stored.MamId, Is.EqualTo("rotated-cookie"));
                Assert.That(stored.UserId, Is.EqualTo("42"));
                Assert.That(stored.UnsatisfiedCount, Is.EqualTo(3));
                Assert.That(stored.UnsatisfiedSlotReserve, Is.EqualTo(2));
            });
        }

        [TestCase("mamId", "other-cookie")]
        [TestCase("baseUrl", "https://other.example")]
        [TestCase("mamSsl", false)]
        [TestCase("proxyId", 5)]
        public void mam_save_retests_if_the_connection_changes_after_a_successful_test(string field, object value)
        {
            UseMamIndexer();
            var resource = Request();
            resource.Fields.Single(item => item.Name == "mamId").Value = "new-cookie";
            _controller.Test(AsApiPayload(resource));

            if (field == "proxyId")
            {
                resource.ProxyId = (int)value;
            }
            else
            {
                resource.Fields.Single(item => item.Name == field).Value = value;
            }

            _failTest = true;
            Assert.Throws<FluentValidation.ValidationException>(() => NewController().UpdateProvider(AsApiPayload(resource)));
            Assert.That(_tests, Is.EqualTo(2));
            Assert.That(_writes, Is.Zero);
        }

        [Test]
        public void mam_unchanged_save_stores_a_newer_test_result_once()
        {
            var settings = UseMamIndexer();
            settings.UserClass = "User";
            settings.UnsatisfiedCount = 45;
            settings.UnsatisfiedLimit = 50;
            settings.UnsatisfiedSnapshotUtc = settings.UnsatisfiedStatusRefreshedUtc = DateTime.UtcNow.AddHours(-3);
            var resource = Request();
            Assert.That(resource.Fields.Single(field => field.Name == "mamId").Value.ToString(), Is.EqualTo("********"));

            _controller.Test(AsApiPayload(resource));
            Assert.That(_writes, Is.Zero);
            _failTest = true;
            NewController().UpdateProvider(AsApiPayload(resource));

            var stored = (MyAnonaMouseSettings)_saved.Settings;
            Assert.Multiple(() =>
            {
                Assert.That(_tests, Is.EqualTo(1));
                Assert.That(_writes, Is.EqualTo(1));
                Assert.That(stored.MamId, Is.EqualTo("rotated-cookie"));
                Assert.That(stored.UserId, Is.EqualTo("42"));
                Assert.That(stored.UserClass, Is.EqualTo("VIP"));
                Assert.That(stored.IsVip, Is.True);
                Assert.That(stored.UnsatisfiedCount, Is.EqualTo(3));
                Assert.That(stored.UnsatisfiedLimit, Is.EqualTo(50));
                Assert.That(stored.UnsatisfiedSnapshotUtc, Is.GreaterThan(settings.UnsatisfiedSnapshotUtc));
                Assert.That(stored.UnsatisfiedStatusRefreshedUtc, Is.GreaterThan(settings.UnsatisfiedStatusRefreshedUtc));
            });

            NewController().UpdateProvider(AsApiPayload(resource));
            Assert.That(_tests, Is.EqualTo(1));
            Assert.That(_writes, Is.EqualTo(1));
        }

        [Test]
        public void mam_disabled_save_keeps_a_passed_test_without_retesting()
        {
            UseMamIndexer();
            _saved.EnableRss = false;
            var resource = Request();

            _controller.Test(AsApiPayload(resource));
            NewController().UpdateProvider(AsApiPayload(resource));

            var stored = (MyAnonaMouseSettings)_saved.Settings;
            Assert.Multiple(() =>
            {
                Assert.That(_tests, Is.EqualTo(1));
                Assert.That(_writes, Is.EqualTo(1));
                Assert.That(_saved.Enable, Is.False);
                Assert.That(stored.MamId, Is.EqualTo("rotated-cookie"));
                Assert.That(stored.UnsatisfiedCount, Is.EqualTo(3));
                Assert.That(stored.UnsatisfiedStatusRefreshedUtc, Is.Not.Null);
            });
        }

        [TestCase(5)]
        [TestCase(180)]
        [TestCase(null)]
        public void mam_save_can_enable_protection_without_testing(int? summaryAgeMinutes)
        {
            var settings = UseMamIndexer();
            settings.ProtectUnsatisfiedSlots = false;
            if (summaryAgeMinutes is { } age)
            {
                settings.UnsatisfiedCount = 4;
                settings.UnsatisfiedLimit = 50;
                settings.UnsatisfiedSnapshotUtc = settings.UnsatisfiedStatusRefreshedUtc = DateTime.UtcNow.AddMinutes(-age);
            }

            var resource = Request();
            resource.Fields.Single(field => field.Name == "protectUnsatisfiedSlots").Value = true;

            NewController().UpdateProvider(AsApiPayload(resource));

            Assert.That(_tests, Is.Zero);
            Assert.That(_writes, Is.EqualTo(1));
            Assert.That(((MyAnonaMouseSettings)_saved.Settings).ProtectUnsatisfiedSlots, Is.True);
        }

        [TestCase("new-tested")]
        [TestCase("edited-tested")]
        [TestCase("priority-only")]
        [TestCase("new-cookie-untested")]
        [TestCase("reenable")]
        [TestCase("summary-warning")]
        public void mam_save_contacts_only_the_endpoints_needed_for_the_current_connection(string scenario)
        {
            UseMamIndexer();
            var requests = new List<string>();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var summary = scenario == "summary-warning"
                ? "not-json"
                : $"{{\"uid\":42,\"classname\":\"User\",\"created\":{now},\"unsat\":{{\"count\":3,\"limit\":50}}}}";
            var client = Proxy<IIndexerHttpClient>((_, args) =>
            {
                var request = (HttpRequest)args[0];
                var path = ((Uri)request.Url).AbsolutePath;
                requests.Add(path);
                Assert.That(request.Cookies["mam_id"], Is.EqualTo("new-cookie"));
                var content = path == "/jsonLoad.php" ? summary : "{\"data\":[]}";
                return Task.FromResult(new HttpResponse(request, new HttpHeader { ContentType = "application/json" }, content, HttpStatusCode.OK));
            });
            var clientFactory = Proxy<IIndexerHttpClientFactory>((_, _) => client);
            var logger = LogManager.GetCurrentClassLogger();
            var indexer = new MyAnonaMouse(clientFactory, null, null, null, new MyAnonaMouseAccountSummaryThrottle(logger), logger);
            _testMamConnection = definition =>
            {
                indexer.Definition = definition;
                return indexer.Test();
            };

            _saved.EnableRss = scenario != "reenable";
            var resource = Request();
            if (scenario == "priority-only")
            {
                resource.Priority++;
            }
            else if (scenario == "reenable")
            {
                resource.EnableRss = true;
            }
            else
            {
                resource.Fields.Single(item => item.Name == "mamId").Value = "new-cookie";
            }

            if (scenario == "new-tested")
            {
                resource.Id = 0;
            }

            if (scenario == "summary-warning")
            {
                resource.Fields.Single(item => item.Name == "protectUnsatisfiedSlots").Value = false;
            }

            if (scenario is "new-tested" or "edited-tested" or "summary-warning")
            {
                _controller.Test(AsApiPayload(resource));
                Assert.That(requests, Is.EqualTo(new[] { "/tor/js/loadSearchJSONbasic.php", "/jsonLoad.php" }));
            }

            if (resource.Id == 0)
            {
                NewController().CreateProvider(AsApiPayload(resource));
            }
            else
            {
                NewController().UpdateProvider(AsApiPayload(resource));
            }

            var expected = scenario is "priority-only" or "reenable"
                ? Array.Empty<string>()
                : new[] { "/tor/js/loadSearchJSONbasic.php", "/jsonLoad.php" };
            Assert.That(requests, Is.EqualTo(expected));
            Assert.That(_writes, Is.EqualTo(1));
            var stored = (MyAnonaMouseSettings)_saved.Settings;
            if (scenario == "new-tested")
            {
                Assert.That(stored.UserId, Is.EqualTo("42"));
                Assert.That(stored.UnsatisfiedCount, Is.EqualTo(3));
                Assert.That(stored.UnsatisfiedLimit, Is.EqualTo(50));
                Assert.That(stored.UnsatisfiedStatusRefreshedUtc, Is.Not.Null);
            }
            else if (scenario == "summary-warning")
            {
                Assert.That(stored.UnsatisfiedStatusRefreshedUtc, Is.Null);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void forced_cookie_update_preserves_mam_account_status(bool testedFirst)
        {
            var settings = UseMamIndexer();
            settings.UserId = "42";
            settings.IsVip = true;
            settings.UnsatisfiedCount = 4;
            settings.UnsatisfiedLimit = 50;
            settings.UnsatisfiedSnapshotUtc = settings.UnsatisfiedStatusRefreshedUtc =
                DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()).UtcDateTime;
            var resource = Request();
            if (testedFirst)
            {
                _controller.Test(AsApiPayload(resource));
            }

            resource.Fields.Single(field => field.Name == "mamId").Value = "mousetrap-cookie";

            NewController().UpdateProvider(AsApiPayload(resource), forceSave: true);

            var stored = (MyAnonaMouseSettings)_saved.Settings;
            Assert.Multiple(() =>
            {
                Assert.That(_tests, Is.EqualTo(testedFirst ? 1 : 0));
                Assert.That(_writes, Is.EqualTo(1));
                Assert.That(stored.MamId, Is.EqualTo("mousetrap-cookie"));
                Assert.That(stored.UserId, Is.EqualTo(settings.UserId));
                Assert.That(stored.IsVip, Is.True);
                Assert.That(stored.UnsatisfiedCount, Is.EqualTo(settings.UnsatisfiedCount));
                Assert.That(stored.UnsatisfiedLimit, Is.EqualTo(settings.UnsatisfiedLimit));
                Assert.That(stored.UnsatisfiedSnapshotUtc, Is.EqualTo(settings.UnsatisfiedSnapshotUtc));
                Assert.That(stored.UnsatisfiedStatusRefreshedUtc, Is.EqualTo(settings.UnsatisfiedStatusRefreshedUtc));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void mam_save_keeps_the_newer_server_summary_when_only_local_settings_change(bool testedFirst)
        {
            var settings = UseMamIndexer();
            var resource = Request();
            if (testedFirst)
            {
                _controller.Test(resource);
            }

            // The background refresh can complete after the user opens the modal.
            if (!testedFirst)
            {
                settings.MamId = "background-cookie";
            }
            settings.UserId = "42";
            settings.UserClass = "VIP";
            settings.IsVip = true;
            settings.UnsatisfiedCount = 4;
            settings.UnsatisfiedLimit = 50;
            settings.UnsatisfiedSnapshotUtc = DateTime.UtcNow;
            settings.UnsatisfiedStatusRefreshedUtc = DateTime.UtcNow.AddSeconds(1);
            _failTest = true;

            Assert.That(resource.Fields.Any(field => field.Name == "isVip"), Is.False);
            resource.Fields.Add(new Field { Name = "isVip", Value = false });
            resource.Fields.Single(field => field.Name == "manualGrabBuffer").Value = 2;
            NewController().UpdateProvider(AsApiPayload(resource));
            var stored = (MyAnonaMouseSettings)_saved.Settings;
            Assert.Multiple(() =>
            {
                Assert.That(_tests, Is.EqualTo(testedFirst ? 1 : 0));
                Assert.That(_writes, Is.EqualTo(1));
                Assert.That(stored.MamId, Is.EqualTo(testedFirst ? "saved-cookie" : "background-cookie"));
                Assert.That(stored.IsVip, Is.True);
                Assert.That(stored.UserClass, Is.EqualTo("VIP"));
                Assert.That(stored.UserId, Is.EqualTo("42"));
                Assert.That(stored.UnsatisfiedCount, Is.EqualTo(4));
                Assert.That(stored.ManualGrabBuffer, Is.EqualTo(2));
            });
        }

        [Test]
        public void mam_failed_test_does_not_authorize_save_with_changed_settings()
        {
            UseMamIndexer();
            var resource = Request();
            resource.Fields.Single(field => field.Name == "mamId").Value = "new-cookie";
            _controller.Test(AsApiPayload(resource));
            _failTest = true;

            Assert.Throws<FluentValidation.ValidationException>(() => NewController().Test(AsApiPayload(resource)));
            Assert.Throws<FluentValidation.ValidationException>(() => NewController().UpdateProvider(AsApiPayload(resource)));
            Assert.That(_tests, Is.EqualTo(3));
            Assert.That(_writes, Is.Zero);
        }

        [Test]
        public void mam_status_applies_and_copies_every_persisted_account_field()
        {
            var status = new MyAnonaMouseAccountStatus
            {
                UserId = "42",
                UserClass = "VIP",
                IsVip = true,
                UnsatisfiedCount = 3,
                UnsatisfiedLimit = 50,
                SnapshotCreatedUtc = DateTime.UnixEpoch.AddMinutes(1),
                RefreshedUtc = DateTime.UnixEpoch.AddMinutes(2),
                RotatedMamId = "rotated-cookie"
            };
            var expected = new Dictionary<string, object>
            {
                [nameof(MyAnonaMouseSettings.UserId)] = status.UserId,
                [nameof(MyAnonaMouseSettings.UserClass)] = status.UserClass,
                [nameof(MyAnonaMouseSettings.IsVip)] = status.IsVip,
                [nameof(MyAnonaMouseSettings.UnsatisfiedCount)] = status.UnsatisfiedCount,
                [nameof(MyAnonaMouseSettings.UnsatisfiedLimit)] = status.UnsatisfiedLimit,
                [nameof(MyAnonaMouseSettings.UnsatisfiedSnapshotUtc)] = status.SnapshotCreatedUtc,
                [nameof(MyAnonaMouseSettings.UnsatisfiedStatusRefreshedUtc)] = status.RefreshedUtc
            };
            var nonStatus = new[]
            {
                nameof(MyAnonaMouseSettings.SeedCriteria),
                nameof(MyAnonaMouseSettings.RejectBlocklistedTorrentHashesWhileGrabbing)
            };
            var properties = typeof(MyAnonaMouseSettings).GetProperties();
            var serverOnly = properties
                .Where(property => property.SetMethod?.IsPublic == true && property.GetCustomAttribute<FieldDefinitionAttribute>() == null)
                .Select(property => property.Name);
            Assert.That(serverOnly.Except(expected.Keys.Concat(nonStatus)), Is.Empty, "Classify each server-only property.");

            var source = new MyAnonaMouseSettings { MamId = "current-cookie", ManualGrabBuffer = 2 };
            source.ApplyAccountStatus(status);
            Assert.Multiple(() =>
            {
                foreach (var property in properties.Where(property => expected.ContainsKey(property.Name)))
                {
                    Assert.That(property.GetValue(source), Is.EqualTo(expected[property.Name]), property.Name);
                }

                Assert.That(source.MamId, Is.EqualTo("current-cookie"));
            });

            var target = new MyAnonaMouseSettings { MamId = "old-cookie", ManualGrabBuffer = 2 };
            target.CopyAccountStatusFrom(source);
            Assert.That(JToken.DeepEquals(JToken.Parse(STJson.ToJson(target)), JToken.Parse(STJson.ToJson(source))), Is.True);
        }
    }
}
