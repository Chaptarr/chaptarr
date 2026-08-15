using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class MediaFileRepositoryAuthorPathEvidenceFixture
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
        public void should_return_only_mapped_paths_for_requested_author_and_media_type()
        {
            WithRepository(repository =>
            {
                var all = repository.GetMappedFilePathEvidenceByAuthor(10, "all");
                Assert.That(all.Select(file => file.Path), Is.EquivalentTo(new[]
                {
                    "/library/Author/Audio/one.m4b",
                    "/library/Author/Ebook/two.epub"
                }));
                Assert.That(all.All(file => file.EditionId > 0), Is.True);

                var audiobooks = repository.GetMappedFilePathEvidenceByAuthor(10, "audiobook");
                Assert.That(audiobooks.Select(file => file.Path), Is.EqualTo(new[]
                {
                    "/library/Author/Audio/one.m4b"
                }));

                var ebooks = repository.GetMappedFilePathEvidenceByAuthor(10, "ebook");
                Assert.That(ebooks.Select(file => file.Path), Is.EqualTo(new[]
                {
                    "/library/Author/Ebook/two.epub"
                }));
            });
        }

        private static void WithRepository(Action<MediaFileRepository> action)
        {
            var databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"bookfile_author_evidence_{Guid.NewGuid():N}.db");
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
                    connection.Execute(
                        @"CREATE TABLE ""Books"" (
                            ""Id"" INTEGER PRIMARY KEY,
                            ""AuthorId"" INTEGER NOT NULL
                          );
                          CREATE TABLE ""Editions"" (
                            ""Id"" INTEGER PRIMARY KEY,
                            ""BookId"" INTEGER NOT NULL
                          );
                          CREATE TABLE ""BookFiles"" (
                            ""Id"" INTEGER PRIMARY KEY,
                            ""Path"" TEXT NOT NULL,
                            ""MediaType"" TEXT NULL,
                            ""EditionId"" INTEGER NOT NULL
                          );");

                    connection.Execute(
                        @"INSERT INTO ""Books"" (""Id"", ""AuthorId"") VALUES (1, 10), (2, 20);
                          INSERT INTO ""Editions"" (""Id"", ""BookId"") VALUES (101, 1), (102, 1), (201, 2);
                          INSERT INTO ""BookFiles"" (""Id"", ""Path"", ""MediaType"", ""EditionId"") VALUES
                            (1, '/library/Author/Audio/one.m4b', 'audiobook', 101),
                            (2, '/library/Author/Ebook/two.epub', 'ebook', 102),
                            (3, '/library/Author/Unmapped/three.m4b', 'audiobook', 0),
                            (4, '/library/Other/four.m4b', 'audiobook', 201);");
                }

                var database = new Database("main", () =>
                {
                    var connection = new SqliteConnection(connectionString);
                    connection.Open();
                    return connection;
                });

                action(new MediaFileRepository(new MainDatabase(database), new StubEventAggregator()));
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
    }
}
