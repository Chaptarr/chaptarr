using System;
using System.IO;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class SeriesRepositoryMediaTypeLinkingFixture
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
        public void should_only_link_books_matching_series_media_type()
        {
            var databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"series_media_type_{Guid.NewGuid():N}.db");
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

                    connection.Execute(@"
	                        CREATE TABLE ""Series"" (
	                            ""Id"" INTEGER PRIMARY KEY,
	                            ""Title"" TEXT NOT NULL,
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

                        CREATE TABLE ""Books"" (
                            ""Id"" INTEGER PRIMARY KEY,
                            ""Title"" TEXT NOT NULL,
                            ""SeriesName"" TEXT NULL,
                            ""SeriesPosition"" TEXT NULL,
                            ""AuthorId"" INTEGER NOT NULL,
                            ""MediaType"" INTEGER NOT NULL
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

                    connection.Execute(@"
	                        INSERT INTO ""Series"" (
	                            ""Id"", ""Title"", ""TitleSlug"", ""Description"", ""Numbered"", ""WorkCount"", ""PrimaryWorkCount"",
	                            ""GoodreadsSeriesId"", ""HardcoverSeriesId"", ""OpenLibrarySeriesId"", ""AmazonSeriesAsin"", ""SeriesType"", ""ParentSeriesId"",
	                            ""TotalBooks"", ""PrimaryBooks"", ""Narrator"", ""BaseSeriesId"", ""InstanceNumber"",
	                            ""PreferredNarratorId"", ""ProviderUrls"", ""LastUpdated"", ""Links"", ""MediaType""
	                        ) VALUES
	                        (1, 'Shared Series', 'shared-series', '', 0, 0, 0, 'gr:12345', 'hc:shared-series', NULL, NULL, NULL, NULL, 0, 0, NULL, NULL, 0, NULL, '{}', NULL, '{}', 0),
	                        (2, 'Shared Series', 'shared-series', '', 0, 0, 0, 'gr:12345', 'hc:shared-series', NULL, NULL, NULL, NULL, 0, 0, NULL, NULL, 0, NULL, '{}', NULL, '{}', 1),
	                        (3, 'Shared Series - Narrator', 'shared-series-narrator', '', 0, 0, 0, 'gr:12345', 'hc:shared-series', NULL, NULL, NULL, NULL, 0, 0, 'Narrator Name', 'gr:12345', 1, 77, '{}', NULL, '{}', 0);
	                    ");

                    connection.Execute(@"
                        INSERT INTO ""Books"" (""Id"", ""Title"", ""SeriesName"", ""SeriesPosition"", ""AuthorId"", ""MediaType"") VALUES
                        (101, 'Shared Series Audio', 'Shared Series', '1', 34, 0),
                        (202, 'Shared Series Ebook', 'Shared Series', '2', 34, 1),
                        (303, 'Other Author Audio', 'Shared Series', '3', 999, 0);
                    ");

                    // Persist authoritative series membership via SeriesBookLink (no Books.SeriesName inference).
                    connection.Execute(@"
                        INSERT INTO ""SeriesBookLink"" (""Id"", ""Position"", ""SeriesPosition"", ""SeriesId"", ""BookId"", ""IsPrimary"", ""SeriesInstanceType"", ""IsInheritedLink"") VALUES
                        (1, '1', 1, 1, 101, 1, 'original', 0),
                        (2, '2', 2, 2, 202, 1, 'original', 0);
                    ");
                }

                var database = new Database("main", () =>
                {
                    var conn = new SqliteConnection(connectionString);
                    conn.Open();
                    return conn;
                });

                var mainDatabase = new MainDatabase(database);
                var eventAggregator = new StubEventAggregator();
                var linkRepository = new SeriesBookLinkRepository(mainDatabase, eventAggregator);
                var sut = new SeriesRepository(mainDatabase, eventAggregator, linkRepository, LogManager.GetLogger("test"));

                var result = sut.GetByAuthorId(34);

                var audiobookSeries = result.Single(s => s.Id == 1);
                var ebookSeries = result.Single(s => s.Id == 2);

                Assert.That(audiobookSeries.LinkItems.Select(l => l.BookId).ToList(), Is.EqualTo(new[] { 101 }));
                Assert.That(ebookSeries.LinkItems.Select(l => l.BookId).ToList(), Is.EqualTo(new[] { 202 }));

                Assert.That(sut.FindById("gr:12345", BookMediaType.Audiobook).Id, Is.EqualTo(1));
                Assert.That(sut.FindById("gr:12345", BookMediaType.Ebook).Id, Is.EqualTo(2));
                Assert.That(sut.FindById(new[] { "gr:12345" }.ToList(), BookMediaType.Audiobook).Single().Id, Is.EqualTo(1));
                Assert.That(sut.FindById(new[] { "gr:12345" }.ToList(), BookMediaType.Ebook).Single().Id, Is.EqualTo(2));
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
