using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.MyAnonaMouse;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture]
    public class MyAnonaMouseSeedTimeFixture
    {
        [Test]
        public void should_allow_seed_time_to_be_cleared_and_persist_as_null()
        {
            var settings = new MyAnonaMouseSettings
            {
                SeedTimeHours = null
            };

            var json = STJson.ToJson(settings);
            Assert.That(json, Does.Not.Contain("seedTimeHours"));

            var roundTripped = STJson.Deserialize<MyAnonaMouseSettings>(json);

            Assert.That(roundTripped.SeedTimeHours, Is.Null);
            Assert.That(roundTripped.SeedCriteria.SeedTime, Is.Null);
        }

        [Test]
        public void default_definition_should_set_seed_time_hours_to_72()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var indexer = new MyAnonaMouse(null, null, null, null, null, logger);

            var definition = (IndexerDefinition)indexer.DefaultDefinitions.Single();
            var settings = (MyAnonaMouseSettings)definition.Settings;

            Assert.That(settings.SeedTimeHours, Is.EqualTo(72));
            Assert.That(definition.EnableRss, Is.True);
        }

        [Test]
        public void should_warn_when_mam_seed_time_is_below_site_requirement()
        {
            var settings = new MyAnonaMouseSettings
            {
                MamId = "test",
                SeedTimeHours = 24
            };

            var result = settings.Validate();

            Assert.That(result.Warnings.Any(w => w.ErrorMessage.Contains("Under 4320 leads to H&R")), Is.True);
        }
    }
}
