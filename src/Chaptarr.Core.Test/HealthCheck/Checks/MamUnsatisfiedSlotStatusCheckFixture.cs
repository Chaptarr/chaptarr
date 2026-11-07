using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Core.HealthCheck.Checks;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NzbDrone.Core.Localization;

namespace Chaptarr.Core.Test.HealthCheck.Checks
{
    [TestFixture]
    public class MamUnsatisfiedSlotStatusCheckFixture
    {
        private class IndexerFactoryProxy : DispatchProxy
        {
            public List<IndexerDefinition> Definitions { get; set; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    nameof(IIndexerFactory.All) => Definitions,
                    _ => throw new NotImplementedException(targetMethod?.Name)
                };
            }
        }

        private sealed class StubLocalizationService : ILocalizationService
        {
            public Dictionary<string, string> GetLocalizationDictionary() => new();
            public string GetCurrentLocale() => "en";
            public string GetLocalizedString(string phrase) => phrase + ": {0}";
            public string GetLocalizedString(string phrase, Dictionary<string, object> tokens) => GetLocalizedString(phrase);
        }

        private static MyAnonaMouseSettings FreshSettings(bool protect = true)
        {
            var now = DateTime.UtcNow;
            return new MyAnonaMouseSettings
            {
                ProtectUnsatisfiedSlots = protect,
                UnsatisfiedCount = 1,
                UnsatisfiedLimit = 50,
                UnsatisfiedSnapshotUtc = now.AddMinutes(-10),
                UnsatisfiedStatusRefreshedUtc = now.AddMinutes(-10)
            };
        }

        private static IndexerDefinition Definition(string name, MyAnonaMouseSettings settings, bool enable = true)
        {
            return new IndexerDefinition { Name = name, EnableRss = enable, Settings = settings };
        }

        private static NzbDrone.Core.HealthCheck.HealthCheck Run(params IndexerDefinition[] definitions)
        {
            var factory = DispatchProxy.Create<IIndexerFactory, IndexerFactoryProxy>();
            ((IndexerFactoryProxy)(object)factory).Definitions = new List<IndexerDefinition>(definitions);
            return new MamUnsatisfiedSlotStatusCheck(factory, new StubLocalizationService()).Check();
        }

        [Test]
        public void should_be_healthy_when_status_is_fresh()
        {
            Assert.That(Run(Definition("MAM", FreshSettings())).Type, Is.EqualTo(HealthCheckResult.Ok));
        }

        [Test]
        public void should_warn_naming_the_indexer_when_status_is_missing_or_stale()
        {
            var stale = FreshSettings();
            stale.UnsatisfiedStatusRefreshedUtc = DateTime.UtcNow.AddHours(-5);

            var missing = new MyAnonaMouseSettings { ProtectUnsatisfiedSlots = true };

            var result = Run(Definition("Stale MAM", stale), Definition("New MAM", missing), Definition("Healthy MAM", FreshSettings()));

            Assert.That(result.Type, Is.EqualTo(HealthCheckResult.Warning));
            Assert.That(result.Message, Does.Contain("Stale MAM").And.Contain("New MAM").And.Not.Contain("Healthy MAM"));
        }

        [Test]
        public void should_ignore_disabled_indexers_and_those_without_slot_protection()
        {
            var staleSettings = new MyAnonaMouseSettings { ProtectUnsatisfiedSlots = true };

            var result = Run(
                Definition("Disabled", staleSettings, enable: false),
                Definition("Unprotected", new MyAnonaMouseSettings { ProtectUnsatisfiedSlots = false }));

            Assert.That(result.Type, Is.EqualTo(HealthCheckResult.Ok));
        }
    }
}
