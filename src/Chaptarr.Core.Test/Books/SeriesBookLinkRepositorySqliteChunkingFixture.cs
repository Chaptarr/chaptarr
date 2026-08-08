using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class SeriesBookLinkRepositorySqliteChunkingFixture
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
            // TableMapping.Map() is normally invoked via DbFactory static ctor.
            // Integration tests that use repositories directly should ensure mapping/type handlers are registered.
            if (TableMapping.Mapper.TableMap.Count == 0)
            {
                TableMapping.Map();
            }
        }

        [TestCase(900)]
        [TestCase(901)]
        [TestCase(1800)]
        public void should_handle_large_book_id_sets_without_sqlite_variable_limit(int linkCount)
        {
            var databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"series_book_link_chunking_{linkCount}_{Guid.NewGuid():N}.db");
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
                    connection.Execute("PRAGMA journal_mode = MEMORY;");
                    connection.Execute("PRAGMA synchronous = OFF;");

                    connection.Execute(@"
                        CREATE TABLE ""Series"" (
                            ""Id"" INTEGER PRIMARY KEY,
                            ""Title"" TEXT NULL,
                            ""TitleSlug"" TEXT NULL,
                            ""Description"" TEXT NULL,
                            ""Numbered"" INTEGER NOT NULL DEFAULT 0,
                            ""WorkCount"" INTEGER NOT NULL DEFAULT 0,
                            ""PrimaryWorkCount"" INTEGER NOT NULL DEFAULT 0,
                            ""GoodreadsSeriesId"" TEXT NULL,
                            ""HardcoverSeriesId"" TEXT NULL,
                            ""OpenLibrarySeriesId"" TEXT NULL,
                            ""AmazonSeriesAsin"" TEXT NULL,
                            ""SeriesType"" TEXT NULL,
                            ""ParentSeriesId"" INTEGER NULL,
                            ""TotalBooks"" INTEGER NOT NULL DEFAULT 0,
                            ""PrimaryBooks"" INTEGER NOT NULL DEFAULT 0,
                            ""Narrator"" TEXT NULL,
                            ""BaseSeriesId"" TEXT NULL,
                            ""MediaType"" INTEGER NOT NULL DEFAULT 0,
                            ""InstanceNumber"" INTEGER NOT NULL DEFAULT 0,
                            ""PreferredNarratorId"" INTEGER NULL,
                            ""ProviderUrls"" TEXT NULL,
                            ""LastUpdated"" TEXT NULL,
                            ""Links"" TEXT NULL
                        );

                        CREATE TABLE ""SeriesBookLink"" (
                            ""Id"" INTEGER PRIMARY KEY,
                            ""Position"" TEXT NULL,
                            ""SeriesPosition"" INTEGER NOT NULL DEFAULT 0,
                            ""SeriesId"" INTEGER NOT NULL,
                            ""BookId"" INTEGER NOT NULL,
                            ""IsPrimary"" INTEGER NOT NULL DEFAULT 0,
                            ""SeriesInstanceType"" TEXT NULL,
                            ""IsInheritedLink"" INTEGER NOT NULL DEFAULT 0
                        );
                    ");

                    using (var tran = connection.BeginTransaction())
                    {
                        for (var i = 1; i <= linkCount; i++)
                        {
                            connection.Execute(
                                @"INSERT INTO ""Series"" (
                                    ""Id"", ""Title"", ""TitleSlug"", ""Description"", ""Numbered"", ""WorkCount"", ""PrimaryWorkCount"",
                                    ""GoodreadsSeriesId"", ""HardcoverSeriesId"", ""OpenLibrarySeriesId"", ""SeriesType"", ""ParentSeriesId"",
                                    ""TotalBooks"", ""PrimaryBooks"", ""Narrator"", ""BaseSeriesId"", ""InstanceNumber"",
                                    ""PreferredNarratorId"", ""ProviderUrls"", ""LastUpdated"", ""Links""
                                ) VALUES (
                                    @Id, @Title, @TitleSlug, @Description, @Numbered, @WorkCount, @PrimaryWorkCount,
                                    @GoodreadsSeriesId, @HardcoverSeriesId, @OpenLibrarySeriesId, @SeriesType, @ParentSeriesId,
                                    @TotalBooks, @PrimaryBooks, @Narrator, @BaseSeriesId, @InstanceNumber,
                                    @PreferredNarratorId, @ProviderUrls, @LastUpdated, @Links
                                );",
                                new
                                {
                                    Id = i,
                                    Title = $"Series {i}",
                                    TitleSlug = $"series-{i}",
                                    Description = string.Empty,
                                    Numbered = 0,
                                    WorkCount = 0,
                                    PrimaryWorkCount = 0,
                                    GoodreadsSeriesId = (string)null,
                                    HardcoverSeriesId = (string)null,
                                    OpenLibrarySeriesId = (string)null,
                                    SeriesType = (string)null,
                                    ParentSeriesId = (int?)null,
                                    TotalBooks = 0,
                                    PrimaryBooks = 0,
                                    Narrator = (string)null,
                                    BaseSeriesId = (string)null,
                                    InstanceNumber = 0,
                                    PreferredNarratorId = (int?)null,
                                    ProviderUrls = "{}",
                                    LastUpdated = (string)null,
                                    Links = "{}"
                                },
                                tran);

                            connection.Execute(
                                @"INSERT INTO ""SeriesBookLink"" (
                                    ""Id"", ""Position"", ""SeriesPosition"", ""SeriesId"", ""BookId"", ""IsPrimary"", ""SeriesInstanceType"", ""IsInheritedLink""
                                ) VALUES (
                                    @Id, @Position, @SeriesPosition, @SeriesId, @BookId, @IsPrimary, @SeriesInstanceType, @IsInheritedLink
                                );",
                                new
                                {
                                    Id = i,
                                    Position = "1",
                                    SeriesPosition = 1,
                                    SeriesId = i,
                                    BookId = i,
                                    IsPrimary = 1,
                                    SeriesInstanceType = "original",
                                    IsInheritedLink = 0
                                },
                                tran);
                        }

                        tran.Commit();
                    }
                }

                var database = new Database("main", () =>
                {
                    var conn = new SqliteConnection(connectionString);
                    conn.Open();
                    return conn;
                });

                var mainDatabase = new MainDatabase(database);
                var sut = new SeriesBookLinkRepository(mainDatabase, new StubEventAggregator());
                var bookIds = Enumerable.Range(1, linkCount).ToList();

                List<SeriesBookLink> links = null;
                Assert.DoesNotThrow(() => links = sut.GetLinksByBook(bookIds));

                Assert.That(links, Has.Count.EqualTo(linkCount));
                Assert.That(links.All(l => l.Series is { IsLoaded: true }), Is.True);
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
