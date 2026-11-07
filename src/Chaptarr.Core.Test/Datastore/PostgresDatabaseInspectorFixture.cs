using System;
using Npgsql;
using NUnit.Framework;
using NzbDrone.Core.Datastore;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    [NonParallelizable]
    public class PostgresDatabaseInspectorFixture
    {
        private const string InspectorSchema = "chaptarrng_postgres_inspector_fixture";

        private string _baseConnectionString;
        private bool _ownsSchema;

        [SetUp]
        public void SetUp()
        {
            _baseConnectionString = Environment.GetEnvironmentVariable("CHAPTARRNG_POSTGRES_TEST_CONNECTION");
            if (string.IsNullOrWhiteSpace(_baseConnectionString))
            {
                Assert.Ignore("Set CHAPTARRNG_POSTGRES_TEST_CONNECTION to run the PostgreSQL database-inspector fixture.");
            }

            using var connection = new NpgsqlConnection(_baseConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT to_regnamespace(@schema) IS NOT NULL;";
            command.Parameters.AddWithValue("schema", InspectorSchema);
            if ((bool)command.ExecuteScalar())
            {
                Assert.Ignore("The PostgreSQL test server already contains the inspector fixture schema.");
            }

            command.Parameters.Clear();
            command.CommandText = "CREATE SCHEMA \"chaptarrng_postgres_inspector_fixture\";";
            command.ExecuteNonQuery();
            _ownsSchema = true;
        }

        [TearDown]
        public void TearDown()
        {
            if (string.IsNullOrWhiteSpace(_baseConnectionString) || !_ownsSchema)
            {
                return;
            }

            using var connection = new NpgsqlConnection(_baseConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP SCHEMA IF EXISTS \"chaptarrng_postgres_inspector_fixture\" CASCADE;";
            command.ExecuteNonQuery();
            _ownsSchema = false;
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_inspect_chaptarr_lineage_and_bookshelf_conversion_marker(bool addMarker)
        {
            using (var connection = new NpgsqlConnection(_baseConnectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE "chaptarrng_postgres_inspector_fixture"."Authors" (
                        "Id" integer PRIMARY KEY,
                        "AudiobookRootFolderPath" text,
                        "EbookRootFolderPath" text);
                    CREATE TABLE "chaptarrng_postgres_inspector_fixture"."Config" ("Key" text, "Value" text);
                    """;
                command.ExecuteNonQuery();
                if (addMarker)
                {
                    using var markerCommand = connection.CreateCommand();
                    markerCommand.CommandText = "INSERT INTO \"chaptarrng_postgres_inspector_fixture\".\"Config\" (\"Key\", \"Value\") VALUES ('BookshelfNGDatabaseMigrated', 'true');";
                    markerCommand.ExecuteNonQuery();
                }
            }

            var connectionBuilder = new NpgsqlConnectionStringBuilder(_baseConnectionString)
            {
                Pooling = false,
                SearchPath = InspectorSchema
            };

            var state = new PostgresDatabaseInspector().Inspect(connectionBuilder.ConnectionString);

            Assert.That(state.Exists, Is.True);
            Assert.That(state.Lineage, Is.EqualTo(DatabaseLineage.Chaptarr));
            Assert.That(state.HasBookshelfMigrationMarker, Is.EqualTo(addMarker));
        }

        [Test]
        public void should_not_mistake_a_matching_preference_for_a_migration_marker()
        {
            using (var connection = new NpgsqlConnection(_baseConnectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE "chaptarrng_postgres_inspector_fixture"."Authors" (
                        "Id" integer PRIMARY KEY,
                        "AudiobookRootFolderPath" text,
                        "EbookRootFolderPath" text);
                    CREATE TABLE "chaptarrng_postgres_inspector_fixture"."Config" ("Key" text, "Value" text);
                    INSERT INTO "chaptarrng_postgres_inspector_fixture"."Config" ("Key", "Value") VALUES ('BookshelfMatchCompatibility', 'true');
                    """;
                command.ExecuteNonQuery();
            }

            var connectionBuilder = new NpgsqlConnectionStringBuilder(_baseConnectionString)
            {
                Pooling = false,
                SearchPath = InspectorSchema
            };

            var state = new PostgresDatabaseInspector().Inspect(connectionBuilder.ConnectionString);

            Assert.That(state.Lineage, Is.EqualTo(DatabaseLineage.Chaptarr));
            Assert.That(state.HasBookshelfMigrationMarker, Is.False);
        }
    }
}
