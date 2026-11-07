using System;
using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Test.Datastore
{
    [TestFixture]
    [NonParallelizable]
    public class PostgresOptionsFixture
    {
        private static readonly string[] EnvironmentVariables =
        {
            "Chaptarr__Postgres__Host",
            "Chaptarr__Postgres__Port",
            "Chaptarr__Postgres__User",
            "Chaptarr__Postgres__Password",
            "Readarr__Postgres__Host",
            "Readarr__Postgres__Port",
            "Readarr__Postgres__Password",
            "Audioarr__Postgres__Host",
            "Audioarr__Postgres__Port",
            "Audioarr__Postgres__User"
        };

        private Dictionary<string, string> _previousValues;

        [SetUp]
        public void SetUp()
        {
            _previousValues = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in EnvironmentVariables)
            {
                _previousValues[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, null);
            }
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var name in EnvironmentVariables)
            {
                Environment.SetEnvironmentVariable(name, _previousValues[name]);
            }
        }

        [Test]
        public void should_bind_chaptarr_postgres_values_with_per_setting_legacy_fallback()
        {
            Environment.SetEnvironmentVariable("Audioarr__Postgres__Host", "audioarr-db");
            Environment.SetEnvironmentVariable("Readarr__Postgres__Host", "readarr-db");
            Environment.SetEnvironmentVariable("Audioarr__Postgres__Port", "5434");
            Environment.SetEnvironmentVariable("Readarr__Postgres__Port", "5433");
            Environment.SetEnvironmentVariable("Chaptarr__Postgres__Port", "6543");
            Environment.SetEnvironmentVariable("Audioarr__Postgres__User", "audioarr-user");
            Environment.SetEnvironmentVariable("Readarr__Postgres__Password", "legacy-secret");
            Environment.SetEnvironmentVariable("Chaptarr__Postgres__Password", string.Empty);

            var options = PostgresOptions.GetOptions();

            Assert.Multiple(() =>
            {
                Assert.That(options.Host, Is.EqualTo("readarr-db"), "Readarr takes precedence over Audioarr when Chaptarr is unset");
                Assert.That(options.Port, Is.EqualTo(6543), "an explicit Chaptarr value takes precedence field by field");
                Assert.That(options.User, Is.EqualTo("audioarr-user"), "Audioarr fills a value absent from both higher-priority sections");
                Assert.That(options.Password, Is.Empty, "an explicitly empty Chaptarr secret clears the legacy secret");
            });
        }
    }
}
