using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using NzbDrone.Core.Datastore;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    public class BookshelfPostgresDatabaseSelectorFixture
    {
        private Dictionary<string, PostgresDatabaseState> _states;

        [SetUp]
        public void SetUp()
        {
            _states = new Dictionary<string, PostgresDatabaseState>
            {
                [BookshelfPostgresDatabaseSelector.ChaptarrMainDb] = State(false, DatabaseLineage.Empty),
                [BookshelfPostgresDatabaseSelector.BookshelfMainDb] = State(false, DatabaseLineage.Empty)
            };
        }

        private PostgresDatabaseState Inspect(string database) => _states[database];

        private static PostgresDatabaseState State(
            bool exists,
            DatabaseLineage lineage,
            bool hasBookshelfMigrationMarker = false)
        {
            return new PostgresDatabaseState(exists, lineage, hasBookshelfMigrationMarker);
        }

        private PostgresDatabaseSelection Resolve(
            bool mainExplicit = false,
            bool logExplicit = false,
            bool cacheExplicit = false,
            string mainDb = BookshelfPostgresDatabaseSelector.ChaptarrMainDb,
            string logDb = BookshelfPostgresDatabaseSelector.ChaptarrLogDb,
            string cacheDb = BookshelfPostgresDatabaseSelector.ChaptarrCacheDb)
        {
            return BookshelfPostgresDatabaseSelector.Resolve(
                mainDb,
                logDb,
                cacheDb,
                mainExplicit,
                logExplicit,
                cacheExplicit,
                Inspect);
        }

        [Test]
        public void should_keep_chaptarr_defaults_for_a_fresh_install()
        {
            var selection = Resolve();

            Assert.That(selection.MainDb, Is.EqualTo(BookshelfPostgresDatabaseSelector.ChaptarrMainDb));
            Assert.That(selection.LogDb, Is.EqualTo(BookshelfPostgresDatabaseSelector.ChaptarrLogDb));
            Assert.That(selection.CacheDb, Is.EqualTo(BookshelfPostgresDatabaseSelector.ChaptarrCacheDb));
        }

        [Test]
        public void should_select_bookshelf_defaults_when_legacy_main_is_the_only_supported_database()
        {
            _states[BookshelfPostgresDatabaseSelector.BookshelfMainDb] = State(true, DatabaseLineage.BookshelfNG);

            var selection = Resolve();

            Assert.That(selection.MainDb, Is.EqualTo(BookshelfPostgresDatabaseSelector.BookshelfMainDb));
            Assert.That(selection.LogDb, Is.EqualTo(BookshelfPostgresDatabaseSelector.BookshelfLogDb));
            Assert.That(selection.CacheDb, Is.EqualTo(BookshelfPostgresDatabaseSelector.BookshelfCacheDb));
        }

        [Test]
        public void should_keep_using_converted_legacy_database_defaults_after_restart()
        {
            _states[BookshelfPostgresDatabaseSelector.BookshelfMainDb] =
                State(true, DatabaseLineage.Chaptarr, hasBookshelfMigrationMarker: true);

            var selection = Resolve();

            Assert.That(selection.MainDb, Is.EqualTo(BookshelfPostgresDatabaseSelector.BookshelfMainDb));
            Assert.That(selection.LogDb, Is.EqualTo(BookshelfPostgresDatabaseSelector.BookshelfLogDb));
            Assert.That(selection.CacheDb, Is.EqualTo(BookshelfPostgresDatabaseSelector.BookshelfCacheDb));
        }

        [Test]
        public void should_infer_legacy_side_databases_from_a_bookshelf_schema_on_an_explicit_main_database()
        {
            _states["bookshelf-custom-main"] = State(true, DatabaseLineage.BookshelfNG);

            var selection = Resolve(mainExplicit: true, mainDb: "bookshelf-custom-main");

            Assert.That(selection.MainDb, Is.EqualTo("bookshelf-custom-main"));
            Assert.That(selection.LogDb, Is.EqualTo(BookshelfPostgresDatabaseSelector.BookshelfLogDb));
            Assert.That(selection.CacheDb, Is.EqualTo(BookshelfPostgresDatabaseSelector.BookshelfCacheDb));
        }

        [Test]
        public void should_preserve_explicit_database_names_after_bookshelf_conversion()
        {
            _states[BookshelfPostgresDatabaseSelector.ChaptarrMainDb] =
                State(true, DatabaseLineage.Chaptarr, hasBookshelfMigrationMarker: true);

            var selection = Resolve(
                mainExplicit: true,
                logExplicit: true,
                mainDb: BookshelfPostgresDatabaseSelector.ChaptarrMainDb,
                logDb: "operator-log-db");

            Assert.That(selection.MainDb, Is.EqualTo(BookshelfPostgresDatabaseSelector.ChaptarrMainDb));
            Assert.That(selection.LogDb, Is.EqualTo("operator-log-db"));
            Assert.That(selection.CacheDb, Is.EqualTo(BookshelfPostgresDatabaseSelector.BookshelfCacheDb));
        }

        [TestCase(DatabaseLineage.Chaptarr)]
        [TestCase(DatabaseLineage.BookshelfNG)]
        [TestCase(DatabaseLineage.Unsupported)]
        public void should_fail_closed_when_default_database_is_unsupported_and_legacy_database_contains_data(DatabaseLineage legacyLineage)
        {
            _states[BookshelfPostgresDatabaseSelector.ChaptarrMainDb] = State(true, DatabaseLineage.Unsupported);
            _states[BookshelfPostgresDatabaseSelector.BookshelfMainDb] = State(true, legacyLineage);

            Assert.Throws<InvalidDataException>(() => Resolve());
        }

        [Test]
        public void should_fail_closed_when_empty_default_has_an_unsupported_legacy_database()
        {
            _states[BookshelfPostgresDatabaseSelector.BookshelfMainDb] = State(true, DatabaseLineage.Unsupported);

            Assert.Throws<InvalidDataException>(() => Resolve());
        }
    }
}
