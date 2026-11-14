using System;
using System.Collections.Generic;
using System.Data;
using System.Text.RegularExpressions;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using Npgsql;
using NzbDrone.Common.Extensions;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class MediaFileOwnershipFixture
    {
        [TestCase(false)]
        [TestCase(true)]
        public void inventory_and_unmapped_record_cleanup_preserve_other_owners_on_both_engines(bool postgres)
        {
            var pgConnection = Environment.GetEnvironmentVariable("CHAPTARR_TEST_POSTGRES_CONNECTION_STRING");
            if (postgres && string.IsNullOrWhiteSpace(pgConnection))
            {
                Assert.Ignore("Set CHAPTARR_TEST_POSTGRES_CONNECTION_STRING to exercise PostgreSQL temporary tables");
            }

            if (TableMapping.Mapper.TableMap.Count == 0)
            {
                TableMapping.Map();
            }

            IDbConnection Open()
            {
                IDbConnection connection = postgres
                    ? new NpgsqlConnection(pgConnection)
                    : new SqliteConnection("Data Source=:memory:;Pooling=False");
                connection.Open();
                try
                {
                    // Session-local tables shadow application names; no library data is modified.
                    CreateSchema<Author>(connection);
                    CreateSchema<Book>(connection);
                    CreateSchema<Edition>(connection);
                    CreateSchema<BookFile>(connection);
                    connection.Execute(@"INSERT INTO ""Authors"" (""Id"", ""Name"") VALUES (10, 'Selected Author'), (20, 'Other Author');
                        INSERT INTO ""Books"" (""Id"", ""AuthorId"") VALUES (1, 10), (2, 20);
                        INSERT INTO ""Editions"" (""Id"", ""BookId"") VALUES (11, 1), (12, 1), (21, 2);");
                    var rows = new[]
                    {
                        (1, 11, @"C:\audio\A\book.m4b", (string)null),
                        (2, 12, @"C:\ebooks\A\book.epub", @"C:\audio\A\copy.epub"),
                        (3, 21, @"C:\audio\A\other-author.epub", (string)null),
                        (4, 0, @"C:\audio\A\unmapped.m4b", (string)null),
                        (5, 21, @"C:\audio\B\unrelated.m4b", (string)null),
                        (6, 21, @"C:\audio\B\replica-owner.epub", @"C:\audio\A\managed.epub"),
                        (7, 21, @"C:\audio\AB\prefix-lookalike.epub", (string)null)
                    };
                    connection.Execute(@"INSERT INTO ""BookFiles"" (""Id"", ""EditionId"", ""Path"", ""ReplicaPaths"")
                        VALUES (@Id, @EditionId, @Path, @Replicas)", rows.Select(row => new
                    {
                        Id = row.Item1, EditionId = row.Item2, Path = row.Item3.AsOsAgnostic(),
                        Replicas = row.Item1 == 3 ? null : row.Item4 == null ? "[]" : System.Text.Json.JsonSerializer.Serialize(new[] { row.Item4.AsOsAgnostic() })
                    }));
                    connection.Execute(@"UPDATE ""BookFiles"" SET ""CalibreId"" = 7 WHERE ""Id"" = 2;");
                    return connection;
                }
                catch
                {
                    connection.Dispose();
                    throw;
                }
            }

            using var connection = Open();
            var lease = AuthorDeletionSafetyFixture.Proxy<IDbConnection>((method, args) =>
                method.Name == nameof(IDisposable.Dispose) ? null : method.Invoke(connection, args));
            var database = AuthorDeletionSafetyFixture.Proxy<IMainDatabase>((method, args) => method.Name switch
            {
                nameof(IMainDatabase.OpenConnection) => lease,
                "get_DatabaseType" => postgres ? DatabaseType.PostgreSQL : DatabaseType.SQLite,
                _ => throw new NotImplementedException(method.Name)
            });
            var repository = new MediaFileRepository(database, null);
            var files = repository.GetFilePathOwnership(new[] { 10 }, new[] { @"C:\audio\A".AsOsAgnostic() });
            Assert.That(files, Has.Count.EqualTo(5));
            Assert.That(files.Single(file => file.Id == 2).CalibreId, Is.EqualTo(7));
            Assert.That(files.Single(file => file.Id == 1).Author.Id, Is.EqualTo(10));
            Assert.That(files.Single(file => file.Id == 1).Author.Name, Is.EqualTo("Selected Author"));
            Assert.That(files.Single(file => file.Id == 2).Author.Id, Is.EqualTo(10));
            Assert.That(files.Single(file => file.Id == 2).ReplicaPaths, Is.EqualTo(new[] { @"C:\audio\A\copy.epub".AsOsAgnostic() }));
            Assert.That(files.Single(file => file.Id == 3).Author.Id, Is.EqualTo(20));
            Assert.That(files.Single(file => file.Id == 3).Author.Name, Is.EqualTo("Other Author"));
            Assert.That(files.Single(file => file.Id == 4).Author, Is.Null);

            Assert.That(files.Select(file => file.Id), Does.Contain(6), "Replicas must pull their outside owner into the scoped inventory");
            Assert.That(files.Select(file => file.Id), Does.Not.Contain(5).And.Not.Contain(7), "Unrelated folders and partial prefixes are not inventory");
            Assert.That(repository.GetFilePathOwnership(Array.Empty<int>(), Array.Empty<string>()), Is.Empty);

            var purgedPaths = new List<string>();
            var ingest = AuthorDeletionSafetyFixture.Proxy<IIngestQueueRepository>((method, args) =>
            {
                if (method.Name != nameof(IIngestQueueRepository.PurgePaths)) throw new NotImplementedException(method.Name);
                purgedPaths.AddRange((IEnumerable<string>)args[0]);
                return method.ReturnType == typeof(void) ? null : 1;
            });
            var events = AuthorDeletionSafetyFixture.Proxy<IEventAggregator>((method, args) =>
                throw new InvalidOperationException("Record-only cleanup must not publish physical deletion events"));
            var service = new MediaFileService(repository, events, ingest, null, LogManager.GetCurrentClassLogger());
            service.DeleteRecordsForAuthorDeletion(files.Where(file => file.Author == null).ToList());
            Assert.That(connection.Query<int>(@"SELECT ""Id"" FROM ""BookFiles"" ORDER BY ""Id"""), Is.EqualTo(new[] { 1, 2, 3, 5, 6, 7 }));
            Assert.That(purgedPaths, Is.EqualTo(new[] { @"C:\audio\A\unmapped.m4b".AsOsAgnostic() }));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void book_file_lookup_should_preserve_ownership_and_use_edition_selectivity(bool postgres)
        {
            var pgConnection = Environment.GetEnvironmentVariable("CHAPTARR_TEST_POSTGRES_CONNECTION_STRING");
            if (postgres && string.IsNullOrWhiteSpace(pgConnection))
            {
                Assert.Ignore("Set CHAPTARR_TEST_POSTGRES_CONNECTION_STRING to exercise PostgreSQL book-file lookup");
            }

            if (TableMapping.Mapper.TableMap.Count == 0)
            {
                TableMapping.Map();
            }

            using var connection = postgres
                ? (IDbConnection)new NpgsqlConnection(pgConnection)
                : new SqliteConnection("Data Source=:memory:;Pooling=False");
            connection.Open();
            CreateSchema<Author>(connection);
            CreateSchema<Book>(connection);
            CreateSchema<Edition>(connection);
            CreateSchema<BookFile>(connection);
            connection.Execute(@"
CREATE INDEX ""IX_Editions_BookId"" ON ""Editions"" (""BookId"");
CREATE INDEX ""IX_BookFiles_EditionId"" ON ""BookFiles"" (""EditionId"");
INSERT INTO ""Authors"" (""Id"", ""Name"") VALUES (10, 'Shirtaloon'), (20, 'Frank Herbert');
INSERT INTO ""Books"" (""Id"", ""AuthorId"", ""Title"") VALUES
    (49184, 10, 'He Who Fights with Monsters 1'),
    (49185, 10, 'He Who Fights with Monsters 2'),
    (49186, 20, 'Dune'),
    (49187, 9999, 'Unattributed Book'),
    (49188, 10, 'Fileless HWFWM shell');
INSERT INTO ""Editions"" (""Id"", ""BookId"", ""Title"", ""Monitored"") VALUES
    (4918401, 49184, 'He Who Fights with Monsters 1', TRUE),
    (4918402, 49184, 'He Who Fights with Monsters 1 unmonitored copy', FALSE),
    (4918501, 49185, 'He Who Fights with Monsters 2', TRUE),
    (4918601, 49186, 'Dune', TRUE),
    (4918701, 49187, 'Unattributed Edition', TRUE),
    (4918801, 49188, 'Fileless Edition', TRUE),
    (4918405, 49999, 'Missing Book Edition', TRUE);
INSERT INTO ""BookFiles"" (""Id"", ""EditionId"", ""Path"") VALUES
    (1, 4918401, @targetAudio),
    (2, 4918402, @targetUnmonitoredCopy),
    (3, 4918501, @sameAuthorSibling),
    (4, 4918601, @otherAuthor),
    (5, 0, @unmapped),
    (6, 49999, @orphanEdition),
    (7, 4918405, @missingBook),
    (8, 4918701, @missingAuthor);",
                new
                {
                    targetAudio = @"C:\Library\Shirtaloon\He Who Fights with Monsters 1.m4b".AsOsAgnostic(),
                    targetUnmonitoredCopy = @"C:\Library\Shirtaloon\He Who Fights with Monsters 1 unmonitored copy.epub".AsOsAgnostic(),
                    sameAuthorSibling = @"C:\Library\Shirtaloon\He Who Fights with Monsters 2.m4b".AsOsAgnostic(),
                    otherAuthor = @"C:\Library\Frank Herbert\Dune.m4b".AsOsAgnostic(),
                    unmapped = @"C:\Library\Unmapped\unmapped.m4b".AsOsAgnostic(),
                    orphanEdition = @"C:\Library\Orphans\orphan-edition.m4b".AsOsAgnostic(),
                    missingBook = @"C:\Library\Orphans\missing-book.m4b".AsOsAgnostic(),
                    missingAuthor = @"C:\Library\Unattributed\missing-author.m4b".AsOsAgnostic()
                });

            var captured = new CapturedCommand();
            var lease = AuthorDeletionSafetyFixture.Proxy<IDbConnection>((method, args) =>
            {
                if (method.Name == nameof(IDisposable.Dispose))
                {
                    return null;
                }

                var result = method.Invoke(connection, args);
                if (result is IDbCommand command)
                {
                    return AuthorDeletionSafetyFixture.Proxy<IDbCommand>((commandMethod, commandArgs) =>
                    {
                        if (commandMethod.Name == nameof(IDbCommand.ExecuteReader))
                        {
                            captured.Capture(command);
                        }

                        return commandMethod.Invoke(command, commandArgs);
                    });
                }

                return result;
            });
            var observed = lease;
            var database = AuthorDeletionSafetyFixture.Proxy<IMainDatabase>((method, args) => method.Name switch
            {
                nameof(IMainDatabase.OpenConnection) => observed,
                "get_DatabaseType" => postgres ? DatabaseType.PostgreSQL : DatabaseType.SQLite,
                _ => throw new NotImplementedException(method.Name)
            });
            var repository = new MediaFileRepository(database, null);

            var targetFiles = repository.GetFilesByBook(49184);
            Assert.That(targetFiles.Select(file => file.Id), Is.EquivalentTo(new[] { 1, 2 }));
            foreach (var file in targetFiles)
            {
                Assert.That(file.LazyEdition.IsLoaded, Is.True);
                Assert.That(file.LazyEdition.Value.LazyBook.IsLoaded, Is.True);
                Assert.That(file.LazyEdition.Value.Book.Id, Is.EqualTo(49184));
                Assert.That(file.LazyEdition.Value.Book.Author.Id, Is.EqualTo(10));
                Assert.That(file.LazyAuthor.IsLoaded, Is.True);
                Assert.That(file.LazyAuthor.Value.Id, Is.EqualTo(10));
            }

            var missingAuthorFile = repository.GetFilesByBook(49187).Single();
            Assert.That(missingAuthorFile.Id, Is.EqualTo(8));
            Assert.That(missingAuthorFile.LazyEdition.IsLoaded, Is.True);
            Assert.That(missingAuthorFile.LazyEdition.Value.Book.Id, Is.EqualTo(49187));
            Assert.That(missingAuthorFile.LazyAuthor.IsLoaded, Is.True);
            Assert.That(missingAuthorFile.LazyAuthor.Value, Is.Null);
            Assert.That(missingAuthorFile.LazyEdition.Value.Book.Author, Is.Null);

            Assert.That(repository.GetFilesByBook(49188), Is.Empty, "A target book with no files remains empty");
            Assert.That(repository.GetFilesByBook(49999), Is.Empty, "An edition whose book row is missing remains excluded");
            Assert.That(captured.CommandText, Is.Not.Null);

            if (!postgres)
            {
                var explainSql = Regex.Replace(captured.CommandText, "@[A-Za-z0-9_]+", "49184");
                var plan = connection.Query<SqliteQueryPlanRow>("EXPLAIN QUERY PLAN " + explainSql).ToList();
                var details = string.Join(Environment.NewLine, plan.Select(row => row.Detail));
                Assert.That(plan.Any(row => row.Detail.Contains("SEARCH") && row.Detail.Contains("IX_Editions_BookId")), Is.True, details);
                Assert.That(plan.Any(row => row.Detail.Contains("SEARCH") && row.Detail.Contains("IX_BookFiles_EditionId")), Is.True, details);
            }
        }

        private sealed class CapturedCommand
        {
            public string CommandText { get; private set; }

            public void Capture(IDbCommand command)
            {
                if (command.CommandText.Contains("\"BookFiles\"", StringComparison.Ordinal))
                {
                    CommandText = command.CommandText;
                }
            }
        }

        private static void CreateSchema<T>(IDbConnection connection)
            where T : ModelBase
        {
            var excluded = TableMapping.Mapper.ExcludeProperties(typeof(T))
                .Select(property => property.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var tableName = TableMapping.Mapper.TableNameMapping(typeof(T));
            var columns = typeof(T)
                .GetProperties()
                .Where(property => property.Name == nameof(ModelBase.Id) ||
                                   property.IsMappableProperty() && !excluded.Contains(property.Name))
                .GroupBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Select(property => property.Name == nameof(ModelBase.Id)
                    ? "\"Id\" INTEGER PRIMARY KEY"
                    : $"\"{property.Name}\" {(connection is NpgsqlConnection && (Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType) == typeof(bool) ? "BOOLEAN" : GetSqliteColumnType(property.PropertyType))} NULL")
                .ToList();

            connection.Execute($"CREATE TEMP TABLE \"{tableName}\" ({string.Join(", ", columns)});");
        }

        private static string GetSqliteColumnType(Type propertyType)
        {
            var type = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
            return type.IsEnum || type == typeof(int) || type == typeof(long) || type == typeof(bool)
                ? "INTEGER"
                : "TEXT";
        }

        private sealed class SqliteQueryPlanRow
        {
            public string Detail { get; set; }
        }
    }
}
