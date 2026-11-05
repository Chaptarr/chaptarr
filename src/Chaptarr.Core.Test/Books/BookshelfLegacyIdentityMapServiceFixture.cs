using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Moq;
using NLog;
using NzbDrone.Core.Configuration;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class BookshelfLegacyIdentityMapServiceFixture
    {
        private string _databasePath;

        [SetUp]
        public void SetUp()
        {
            _databasePath = Path.Combine(Path.GetTempPath(), $"bookshelf-identity-map-{Guid.NewGuid():N}.db");
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
CREATE TABLE BookshelfLegacyIdentityMap (
    Source TEXT NOT NULL,
    EntityType TEXT NOT NULL,
    SourceId INTEGER NOT NULL,
    TargetId INTEGER NOT NULL,
    MediaType INTEGER NULL
);
INSERT INTO BookshelfLegacyIdentityMap VALUES ('BookshelfNG', 'Book', 30, 30, 0);
INSERT INTO BookshelfLegacyIdentityMap VALUES ('BookshelfNG', 'Book', 30, 31, 1);
INSERT INTO BookshelfLegacyIdentityMap VALUES ('BookshelfNG', 'Book', 32, 32, 1);
INSERT INTO BookshelfLegacyIdentityMap VALUES ('BookshelfNG', 'AuthorMetadata', 10, 20, NULL);
INSERT INTO BookshelfLegacyIdentityMap VALUES ('OtherSource', 'Book', 30, 900, 0);";
            command.ExecuteNonQuery();
        }

        [TearDown]
        public void TearDown()
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(_databasePath))
            {
                File.Delete(_databasePath);
            }
        }

        private string ConnectionString => new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Pooling = false
        }.ConnectionString;

        [Test]
        public void should_resolve_a_split_source_book_to_both_or_the_requested_format()
        {
            var database = DispatchProxy.Create<IMainDatabase, MainDatabaseProxy>();
            ((MainDatabaseProxy)(object)database).ConnectionString = ConnectionString;
            var subject = new BookshelfLegacyIdentityMapService(database);

            var allFormats = subject.ResolveBookIds(new[] { 30, 99 });
            var ebooks = subject.ResolveBookIds(new[] { 30, 99 }, BookMediaType.Ebook);
            var audiobooks = subject.ResolveBookIds(new[] { 30 }, BookMediaType.Audiobook);

            Assert.Multiple(() =>
            {
                Assert.That(allFormats, Is.EqualTo(new[] { 30, 31, 99 }));
                Assert.That(ebooks, Is.EqualTo(new[] { 31, 99 }));
                Assert.That(audiobooks, Is.EqualTo(new[] { 30 }));
            });
        }

        [Test]
        public void should_return_only_requested_bookshelf_source_mappings()
        {
            var database = DispatchProxy.Create<IMainDatabase, MainDatabaseProxy>();
            ((MainDatabaseProxy)(object)database).ConnectionString = ConnectionString;
            var subject = new BookshelfLegacyIdentityMapService(database);

            var mappings = subject.GetMappings("Book", new[] { 30 });

            Assert.That(mappings.Select(mapping => (mapping.Source, mapping.SourceId, mapping.TargetId, mapping.MediaType)), Is.EqualTo(new[]
            {
                ("BookshelfNG", 30, 30, (int?)0),
                ("BookshelfNG", 30, 31, (int?)1)
            }));
        }

        [Test]
        public void should_resolve_legacy_author_metadata_id_from_target_author_id()
        {
            var database = DispatchProxy.Create<IMainDatabase, MainDatabaseProxy>();
            ((MainDatabaseProxy)(object)database).ConnectionString = ConnectionString;
            var subject = new BookshelfLegacyIdentityMapService(database);

            var mappings = subject.GetMappingsByTargetIds("AuthorMetadata", new[] { 20 });

            Assert.That(mappings.Select(mapping => (mapping.Source, mapping.SourceId, mapping.TargetId, mapping.MediaType)), Is.EqualTo(new[]
            {
                ("BookshelfNG", 10, 20, (int?)null)
            }));
        }

        [Test]
        public void should_preserve_source_media_and_target_order_when_querying_targets()
        {
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
INSERT INTO BookshelfLegacyIdentityMap VALUES ('BookshelfNG', 'Book', 9, 101, 1);
INSERT INTO BookshelfLegacyIdentityMap VALUES ('BookshelfNG', 'Book', 8, 102, NULL);
INSERT INTO BookshelfLegacyIdentityMap VALUES ('BookshelfNG', 'Book', 8, 101, 1);
INSERT INTO BookshelfLegacyIdentityMap VALUES ('BookshelfNG', 'Book', 8, 103, 0);";
            command.ExecuteNonQuery();
            var database = DispatchProxy.Create<IMainDatabase, MainDatabaseProxy>();
            ((MainDatabaseProxy)(object)database).ConnectionString = ConnectionString;
            var mappings = new BookshelfLegacyIdentityMapService(database)
                .GetMappingsByTargetIds("bOoK", new[] { 103, 101, 102 });
            Assert.That(mappings.Select(mapping => (mapping.SourceId, mapping.MediaType, mapping.TargetId)), Is.EqualTo(new[]
            {
                (8, (int?)null, 102), (8, (int?)0, 103), (8, (int?)1, 101), (9, (int?)1, 101)
            }));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void should_register_refreshed_format_counterparts_only_for_migrated_libraries(bool compatibility)
        {
            var database = DispatchProxy.Create<IMainDatabase, MainDatabaseProxy>();
            ((MainDatabaseProxy)(object)database).ConnectionString = ConnectionString;
            var identities = new BookshelfLegacyIdentityMapService(database);
            var config = new Mock<IConfigService>();
            config.SetupGet(value => value.BookshelfMatchCompatibility).Returns(compatibility);
            var subject = new BookshelfRefreshIdentityMapService(database, null, config.Object, identities, LogManager.GetCurrentClassLogger());
            var books = new List<Book>
            {
                new Book { Id = 32, AuthorId = 1, MediaType = BookMediaType.Ebook, HardcoverBookId = "hc:10" },
                new Book { Id = 33, AuthorId = 1, MediaType = BookMediaType.Audiobook, HardcoverBookId = "hc:10" },
                new Book { Id = 34, AuthorId = 1, MediaType = BookMediaType.Ebook, HardcoverBookId = "hc:11" },
                new Book { Id = 35, AuthorId = 1, MediaType = BookMediaType.Audiobook, HardcoverBookId = "hc:11" }
            };
            subject.RegisterBooks(books);
            subject.RegisterBooks(books);
            var added = identities.GetMappingsByTargetIds("Book", new[] { 33, 34, 35 });
            Assert.That(added.Count, Is.EqualTo(compatibility ? 3 : 0));
            if (compatibility)
            {
                Assert.That(identities.ResolveBookIds(new[] { 32 }), Is.EquivalentTo(new[] { 32, 33 }));
                var newId = added.Single(mapping => mapping.TargetId == 34).SourceId;
                Assert.That(newId, Is.EqualTo(34));
                Assert.That(identities.ResolveBookIds(new[] { newId }), Is.EquivalentTo(new[] { 34, 35 }));
                Assert.That(identities.ResolveBookIds(new[] { newId }, BookMediaType.Ebook), Is.EqualTo(new[] { 34 }));
            }
        }

        [Test]
        public void should_not_combine_ambiguous_pockets_or_cross_author_work_ids()
        {
            var database = DispatchProxy.Create<IMainDatabase, MainDatabaseProxy>();
            ((MainDatabaseProxy)(object)database).ConnectionString = ConnectionString;
            var identities = new BookshelfLegacyIdentityMapService(database);
            var config = new Mock<IConfigService>();
            config.SetupGet(value => value.BookshelfMatchCompatibility).Returns(true);
            var subject = new BookshelfRefreshIdentityMapService(database, null, config.Object, identities, LogManager.GetCurrentClassLogger());
            subject.RegisterBooks(new[]
            {
                new Book { Id = 40, AuthorId = 1, MediaType = BookMediaType.Ebook, HardcoverBookId = "hc:11" },
                new Book { Id = 41, AuthorId = 1, MediaType = BookMediaType.Ebook, HardcoverBookId = "hc:11" },
                new Book { Id = 42, AuthorId = 1, MediaType = BookMediaType.Audiobook, HardcoverBookId = "hc:11" },
                new Book { Id = 43, AuthorId = 2, MediaType = BookMediaType.Ebook, HardcoverBookId = "hc:11" }
            });
            Assert.That(identities.GetMappingsByTargetIds("Book", new[] { 40, 41, 42 }), Is.Empty);
            Assert.That(identities.GetMappingsByTargetIds("Book", new[] { 43 }), Has.Count.EqualTo(1));
        }

        [Test]
        public void should_preserve_conflicting_source_identities_and_avoid_new_legacy_id_collisions()
        {
            using (var connection = new SqliteConnection(ConnectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO BookshelfLegacyIdentityMap VALUES ('BookshelfNG', 'Book', 40, 100, 0)";
                command.ExecuteNonQuery();
            }

            var database = DispatchProxy.Create<IMainDatabase, MainDatabaseProxy>();
            ((MainDatabaseProxy)(object)database).ConnectionString = ConnectionString;
            var identities = new BookshelfLegacyIdentityMapService(database);
            var config = new Mock<IConfigService>();
            config.SetupGet(value => value.BookshelfMatchCompatibility).Returns(true);
            var subject = new BookshelfRefreshIdentityMapService(database, null, config.Object, identities, LogManager.GetCurrentClassLogger());
            subject.RegisterBooks(new[]
            {
                new Book { Id = 30, AuthorId = 1, MediaType = BookMediaType.Audiobook, HardcoverBookId = "hc:10" },
                new Book { Id = 32, AuthorId = 1, MediaType = BookMediaType.Ebook, HardcoverBookId = "hc:10" },
                new Book { Id = 40, AuthorId = 1, MediaType = BookMediaType.Ebook, HardcoverBookId = "hc:11" },
                new Book { Id = 41, AuthorId = 1, MediaType = BookMediaType.Audiobook, HardcoverBookId = "hc:11" }
            });
            Assert.That(identities.GetMappingsByTargetIds("Book", new[] { 30, 32 }).Select(mapping => mapping.SourceId),
                Is.EqualTo(new[] { 30, 32 }), "Conflicting imported identities must remain separate");
            var assigned = identities.GetMappingsByTargetIds("Book", new[] { 40, 41 });
            Assert.That(assigned.Select(mapping => mapping.SourceId).Distinct(), Is.EqualTo(new[] { 42 }));
            Assert.That(identities.ResolveBookIds(new[] { 40 }), Is.EqualTo(new[] { 100 }), "Existing legacy ID must remain addressable");
            Assert.That(identities.ResolveBookIds(new[] { 42 }), Is.EqualTo(new[] { 41, 40 }));
        }

        private class MainDatabaseProxy : DispatchProxy
        {
            public string ConnectionString { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IDatabase.OpenConnection))
                {
                    var connection = new SqliteConnection(ConnectionString);
                    connection.Open();
                    return connection;
                }

                throw new NotImplementedException($"Test proxy does not implement {targetMethod?.Name}");
            }
        }
    }
}
