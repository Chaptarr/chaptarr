using NUnit.Framework;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    public class SchemaScopedPostgresProcessorFixture
    {
        [TestCase(null, "staging")]
        [TestCase("", "staging")]
        [TestCase("   ", "staging")]
        [TestCase("custom", "staging")]
        public void should_use_the_active_schema_for_unqualified_queries(string schemaName, string defaultSchemaName)
        {
            var expected = string.IsNullOrWhiteSpace(schemaName) ? defaultSchemaName : schemaName;

            Assert.That(
                SchemaScopedPostgresProcessor.ResolveSchemaName(schemaName, defaultSchemaName),
                Is.EqualTo(expected));
        }
    }
}
