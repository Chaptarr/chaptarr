using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dapper;
using Chaptarr.Core.Test.Common;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class MediaFileRepositoryReplacementFixture
    {
        private sealed class StubEventAggregator : IEventAggregator
        {
            public void PublishEvent<TEvent>(TEvent @event)
                where TEvent : class, IEvent
            {
            }
        }

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            if (TableMapping.Mapper.TableMap.Count == 0)
            {
                TableMapping.Map();
            }
        }

        [Test]
        public void replace_many_should_delete_old_rows_and_insert_new_rows_in_one_transaction()
        {
            WithRepository((repository, connectionString) =>
            {
                InsertPath(connectionString, 1, "/library/old.epub");
                var replacement = NewFile("/library/new.epub");

                repository.ReplaceMany(
                    new List<BookFile> { replacement },
                    new List<BookFile> { new() { Id = 1, Path = "/library/old.epub" } });

                Assert.That(replacement.Id, Is.GreaterThan(0));
                Assert.That(ReadPaths(connectionString), Is.EqualTo(new[] { "/library/new.epub" }));
            });
        }

        [Test]
        public void replace_many_should_restore_deleted_rows_and_reset_generated_ids_when_insert_fails()
        {
            WithRepository((repository, connectionString) =>
            {
                InsertPath(connectionString, 1, "/library/old.epub");
                InsertPath(connectionString, 2, "/library/conflict.epub");
                var replacement = NewFile("/library/conflict.epub");

                Assert.Throws<SqliteException>(() => repository.ReplaceMany(
                    new List<BookFile> { replacement },
                    new List<BookFile> { new() { Id = 1, Path = "/library/old.epub" } }));

                Assert.That(replacement.Id, Is.Zero);
                Assert.That(ReadPaths(connectionString), Is.EqualTo(new[]
                {
                    "/library/old.epub",
                    "/library/conflict.epub"
                }));
            });
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void replacement_should_commit_or_rollback_stale_row_updates_with_the_other_parts(bool postgres, bool fail)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var repository = new MediaFileRepository(database, new StubEventAggregator());
                BookFile Create(string path) => new()
                {
                    Path = path, DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow,
                    Quality = new QualityModel(Quality.MP3), MediaInfo = new MediaInfoModel()
                };
                var stale = repository.Insert(Create("/library/Part 01.mp3"));
                var old = repository.Insert(Create("/library/Old Part 02.mp3"));
                var conflict = repository.Insert(Create("/library/Occupied.mp3"));
                var staleId = stale.Id;
                stale.Size = 1234;
                var added = Create(fail ? conflict.Path : "/library/Part 02.mp3");

                void Commit() => repository.ReplaceMany(new List<BookFile> { stale, added }, new List<BookFile> { old });
                if (fail)
                {
                    Assert.That(Commit, Throws.Exception);
                    Assert.That(repository.Get(staleId).Size, Is.Zero);
                    Assert.That(repository.Get(old.Id).Path, Is.EqualTo(old.Path));
                    Assert.That(added.Id, Is.Zero);
                }
                else
                {
                    Commit();
                    Assert.That(repository.Get(staleId).Size, Is.EqualTo(1234));
                    Assert.That(repository.All().Select(file => file.Id), Does.Not.Contain(old.Id));
                    Assert.That(added.Id, Is.GreaterThan(0));
                }

                Assert.That(stale.Id, Is.EqualTo(staleId));
            });
        }

        private static BookFile NewFile(string path)
        {
            return new BookFile
            {
                Path = path,
                DateAdded = DateTime.UtcNow,
                Modified = DateTime.UtcNow,
                MediaType = "ebook",
                Part = 1
            };
        }

        private static void WithRepository(Action<MediaFileRepository, string> action)
        {
            var databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"bookfile_replace_{Guid.NewGuid():N}.db");
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString();

            try
            {
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    CreateBookFilesSchema(connection);
                    connection.Execute(@"CREATE UNIQUE INDEX ""IX_BookFiles_Path_Unique"" ON ""BookFiles"" (""Path"");");
                }

                var database = new Database("main", () =>
                {
                    var connection = new SqliteConnection(connectionString);
                    connection.Open();
                    return connection;
                });

                action(new MediaFileRepository(new MainDatabase(database), new StubEventAggregator()), connectionString);
            }
            finally
            {
                try
                {
                    if (File.Exists(databasePath))
                    {
                        File.Delete(databasePath);
                    }
                }
                catch
                {
                }
            }
        }

        private static void CreateBookFilesSchema(SqliteConnection connection)
        {
            var excluded = TableMapping.Mapper.ExcludeProperties(typeof(BookFile))
                .Select(property => property.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var columns = typeof(BookFile)
                .GetProperties()
                .Where(property => property.Name == nameof(ModelBase.Id) ||
                                   property.IsMappableProperty() && !excluded.Contains(property.Name))
                .GroupBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Select(property => property.Name == nameof(ModelBase.Id)
                    ? "\"Id\" INTEGER PRIMARY KEY AUTOINCREMENT"
                    : $"\"{property.Name}\" {GetSqliteColumnType(property.PropertyType)} NULL")
                .ToList();

            connection.Execute($"CREATE TABLE \"BookFiles\" ({string.Join(", ", columns)});");
        }

        private static string GetSqliteColumnType(Type propertyType)
        {
            var type = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
            return type.IsEnum || type == typeof(int) || type == typeof(long) || type == typeof(bool)
                ? "INTEGER"
                : "TEXT";
        }

        private static void InsertPath(string connectionString, int id, string path)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            connection.Execute("INSERT INTO \"BookFiles\" (\"Id\", \"Path\") VALUES (@id, @path);", new { id, path });
        }

        private static List<string> ReadPaths(string connectionString)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            return connection.Query<string>("SELECT \"Path\" FROM \"BookFiles\" ORDER BY \"Id\";").ToList();
        }
    }
}
