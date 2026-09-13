using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Reflection;
using Dapper;
using FluentValidation;
using Microsoft.Data.Sqlite;
using Npgsql;
using NLog;
using NzbDrone.Common.Extensions;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Exclusions;

namespace Chaptarr.Core.Test.ImportLists
{
    [TestFixture]
    public class ImportListExclusionServiceFixture
    {
        private sealed class DatabaseHarness : IDisposable
        {
            private readonly bool _postgres;
            private readonly string _sqliteConnectionString;
            private readonly string _postgresAdminConnectionString;
            private readonly string _postgresConnectionString;
            private readonly string _postgresSchema;
            private readonly string _sqlitePath;

            public DatabaseHarness(bool postgres)
            {
                _postgres = postgres;
                if (TableMapping.Mapper.TableMap.Count == 0)
                {
                    TableMapping.Map();
                }

                if (postgres)
                {
                    _postgresAdminConnectionString = Environment.GetEnvironmentVariable("CHAPTARR_TEST_POSTGRES_CONNECTION_STRING");
                    if (string.IsNullOrWhiteSpace(_postgresAdminConnectionString))
                    {
                        Assert.Ignore("Set CHAPTARR_TEST_POSTGRES_CONNECTION_STRING to exercise PostgreSQL exclusion persistence");
                    }

                    _postgresSchema = "exclusion_test_" + Guid.NewGuid().ToString("N");
                    using (var connection = new NpgsqlConnection(_postgresAdminConnectionString))
                    {
                        connection.Open();
                        connection.Execute($"CREATE SCHEMA \"{_postgresSchema}\"");
                    }

                    var connectionBuilder = new NpgsqlConnectionStringBuilder(_postgresAdminConnectionString)
                    {
                        SearchPath = _postgresSchema,
                        Pooling = false
                    };
                    _postgresConnectionString = connectionBuilder.ConnectionString;
                    using var schemaConnection = new NpgsqlConnection(_postgresConnectionString);
                    schemaConnection.Open();
                    schemaConnection.Execute(@"CREATE TABLE ""ImportListExclusions"" (
                        ""Id"" SERIAL PRIMARY KEY,
                        ""ForeignId"" TEXT NOT NULL,
                        ""Name"" TEXT NOT NULL,
                        ""MediaType"" INTEGER NULL)");
                    return;
                }

                var relativePath = $@"chaptarr\import-list-exclusion-{Guid.NewGuid():N}.db".AsOsAgnostic();
                _sqlitePath = Path.Combine(Path.GetTempPath(), relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(_sqlitePath));
                var sqliteBuilder = new SqliteConnectionStringBuilder
                {
                    DataSource = _sqlitePath,
                    Pooling = false
                };
                _sqliteConnectionString = sqliteBuilder.ConnectionString;
                using var sqliteConnection = new SqliteConnection(_sqliteConnectionString);
                sqliteConnection.Open();
                sqliteConnection.Execute(@"CREATE TABLE ""ImportListExclusions"" (
                    ""Id"" INTEGER PRIMARY KEY AUTOINCREMENT,
                    ""ForeignId"" TEXT NOT NULL,
                    ""Name"" TEXT NOT NULL,
                    ""MediaType"" INTEGER NULL)");
            }

            public ImportListExclusionService CreateService()
            {
                var database = new Database("author-exclusion-fixture", OpenConnection);
                var mainDatabase = new MainDatabase(database);
                var repository = new ImportListExclusionRepository(mainDatabase, null);
                return new ImportListExclusionService(repository, LogManager.GetCurrentClassLogger());
            }

            private IDbConnection OpenConnection()
            {
                IDbConnection connection = _postgres
                    ? new NpgsqlConnection(_postgresConnectionString)
                    : new SqliteConnection(_sqliteConnectionString);
                connection.Open();
                return connection;
            }

            public void Dispose()
            {
                if (_postgres)
                {
                    using var connection = new NpgsqlConnection(_postgresAdminConnectionString);
                    connection.Open();
                    connection.Execute($"DROP SCHEMA IF EXISTS \"{_postgresSchema}\" CASCADE");
                    return;
                }

                if (File.Exists(_sqlitePath))
                {
                    File.Delete(_sqlitePath);
                }
            }
        }

        private class RepositoryProxy : DispatchProxy
        {
            public List<ImportListExclusion> Existing { get; set; } = new List<ImportListExclusion>();
            public List<ImportListExclusion> Inserted { get; } = new List<ImportListExclusion>();
            public List<int> DeletedIds { get; } = new List<int>();
            public int InsertManyCalls { get; private set; }
            public int DeleteManyCalls { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case nameof(IImportListExclusionRepository.All):
                        return Existing;

                    case nameof(IImportListExclusionRepository.InsertMany):
                        InsertManyCalls++;
                        Inserted.AddRange((IEnumerable<ImportListExclusion>)args[0]);
                        return null;

                    case nameof(IImportListExclusionRepository.DeleteMany):
                        DeleteManyCalls++;
                        DeletedIds.AddRange((IEnumerable<int>)args[0]);
                        return null;

                    default:
                        throw new NotImplementedException($"Test proxy does not implement IImportListExclusionRepository.{targetMethod?.Name}");
                }
            }
        }

        [Test]
        public void should_batch_author_exclusion_provider_ids_and_skip_existing_ids()
        {
            var repository = DispatchProxy.Create<IImportListExclusionRepository, RepositoryProxy>();
            var repoProxy = (RepositoryProxy)(object)repository;
            repoProxy.Existing = new List<ImportListExclusion>
            {
                new ImportListExclusion { Id = 1, ForeignId = "gr:2", Name = "Existing" }
            };
            var service = new ImportListExclusionService(repository, LogManager.GetCurrentClassLogger());

            var author = new Author
            {
                Name = "Test Author",
                HardcoverAuthorId = "hc:1",
                GoodreadsAuthorId = "gr:2"
            };

            service.AddAuthorExclusions(new[] { author });

            Assert.That(repoProxy.InsertManyCalls, Is.EqualTo(1));
            Assert.That(repoProxy.Inserted.Select(e => e.ForeignId), Is.EqualTo(new[] { "hc:1" }));
            Assert.That(repoProxy.Inserted[0].Name, Is.EqualTo("Test Author"));
        }

        [Test]
        public void author_exclusion_requires_provider_identity_and_never_uses_local_row_id()
        {
            var repository = DispatchProxy.Create<IImportListExclusionRepository, RepositoryProxy>();
            var repoProxy = (RepositoryProxy)(object)repository;
            var service = new ImportListExclusionService(repository, LogManager.GetCurrentClassLogger());

            var error = Assert.Throws<ValidationException>(() => service.AddAuthorExclusions(new[]
            {
                new Author { Id = 991, Name = "Local-only author" }
            }));

            Assert.That(error.Errors.Single().ErrorCode, Is.EqualTo("AUTHOR_PROVIDER_ID_REQUIRED"));
            Assert.That(repoProxy.InsertManyCalls, Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void author_and_book_exclusions_persist_across_connections_with_provider_and_format_scope(bool postgres)
        {
            using var database = new DatabaseHarness(postgres);
            var writer = database.CreateService();
            var author = new Author
            {
                Name = "Frank Herbert",
                GoodreadsAuthorId = "gr:58"
            };
            writer.AddAuthorExclusions(new[] { author });

            // Fixture-only collision case: same numeric payload, different provider identity.
            var dune = new Book
            {
                Title = "Dune",
                MediaType = BookMediaType.Audiobook,
                HardcoverBookId = "hc:58",
                Author = author
            };
            writer.HandleAsync(new BookDeletedEvent(
                dune,
                deleteFiles: false,
                addImportListExclusion: true,
                applyToBothFormats: false));

            // A new service/repository uses new physical connections and observes committed rows.
            var reopened = database.CreateService();
            var exclusions = reopened.All();
            var authorExclusion = exclusions.Single(row => row.ForeignId == "gr:58");
            var bookExclusion = exclusions.Single(row => row.ForeignId == "hc:58");

            Assert.Multiple(() =>
            {
                Assert.That(exclusions, Has.Count.EqualTo(2));
                Assert.That(authorExclusion.Name, Is.EqualTo("Frank Herbert"));
                Assert.That(authorExclusion.MediaType, Is.Null);
                Assert.That(bookExclusion.Name, Is.EqualTo("Frank Herbert - Dune"));
                Assert.That(bookExclusion.MediaType, Is.EqualTo(BookMediaType.Audiobook));
                Assert.That(ImportListSyncService.MatchesExclusionId("gr:58", bookExclusion.ForeignId), Is.False);
                Assert.That(ImportListSyncService.MatchesExclusionId("gr:58", authorExclusion.ForeignId), Is.True);
                Assert.That(ImportListExclusionBookMatcher.AppliesToBook(bookExclusion, dune), Is.True);
                Assert.That(ImportListExclusionBookMatcher.AppliesToBook(bookExclusion, new Book
                {
                    Title = "Dune",
                    MediaType = BookMediaType.Ebook,
                    HardcoverBookId = "hc:58",
                    Author = author
                }), Is.False);
            });

            // A fresh service can repeat the author batch without creating another row.
            reopened.AddAuthorExclusions(new[] { author });
            var idempotentRead = database.CreateService().All();
            Assert.That(idempotentRead, Has.Count.EqualTo(2));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void author_exclusion_batch_with_unknown_provider_identity_writes_no_rows(bool postgres)
        {
            using var database = new DatabaseHarness(postgres);
            var service = database.CreateService();
            var error = Assert.Throws<ValidationException>(() => service.AddAuthorExclusions(new[]
            {
                new Author { Name = "Frank Herbert", GoodreadsAuthorId = "gr:58" },
                new Author { Id = 991, Name = "Local-only author" }
            }));

            Assert.That(error.Errors.Single().ErrorCode, Is.EqualTo("AUTHOR_PROVIDER_ID_REQUIRED"));
            Assert.That(database.CreateService().All(), Is.Empty);
        }

        [Test]
        public void should_batch_book_exclusion_scope_replacement_when_applying_to_both_formats()
        {
            var repository = DispatchProxy.Create<IImportListExclusionRepository, RepositoryProxy>();
            var repoProxy = (RepositoryProxy)(object)repository;
            repoProxy.Existing = new List<ImportListExclusion>
            {
                new ImportListExclusion { Id = 10, ForeignId = "gr:123", Name = "Existing Audio", MediaType = BookMediaType.Audiobook },
                new ImportListExclusion { Id = 11, ForeignId = "gr:123", Name = "Existing Ebook", MediaType = BookMediaType.Ebook }
            };
            var service = new ImportListExclusionService(repository, LogManager.GetCurrentClassLogger());

            var book = new Book
            {
                Title = "Test Book",
                MediaType = BookMediaType.Audiobook,
                GoodreadsWorkId = "gr:123",
                Author = new Author { Name = "Test Author" }
            };

            service.HandleAsync(new BookDeletedEvent(book, deleteFiles: false, addImportListExclusion: true, applyToBothFormats: true));

            Assert.That(repoProxy.DeleteManyCalls, Is.EqualTo(1));
            Assert.That(repoProxy.DeletedIds, Is.EquivalentTo(new[] { 10, 11 }));
            Assert.That(repoProxy.InsertManyCalls, Is.EqualTo(1));
            Assert.That(repoProxy.Inserted, Has.Count.EqualTo(1));
            Assert.That(repoProxy.Inserted[0].ForeignId, Is.EqualTo("gr:123"));
            Assert.That(repoProxy.Inserted[0].MediaType, Is.Null);
        }
    }
}
