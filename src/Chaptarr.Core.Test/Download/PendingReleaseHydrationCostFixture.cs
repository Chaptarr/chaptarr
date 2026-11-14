using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download.Aggregation;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class PendingReleaseHydrationCostFixture
    {
        [TestCase(false, BookMediaType.Audiobook, 1)] [TestCase(true, BookMediaType.Audiobook, 1)]
        [TestCase(false, BookMediaType.Ebook, 1)] [TestCase(true, BookMediaType.Ebook, 1)]
        [TestCase(false, BookMediaType.Audiobook, 25)] [TestCase(true, BookMediaType.Audiobook, 25)]
        [TestCase(false, BookMediaType.Ebook, 25)] [TestCase(true, BookMediaType.Ebook, 25)]
        public void pending_monitoring_cleanup_should_batch_complete_native_hydration(bool postgres, BookMediaType media, int count)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var logger = LogManager.GetCurrentClassLogger();
                var noop = Proxy<IEventAggregator>((method, args) => null);
                var author = new AuthorRepository(database, noop, logger).Insert(new Author
                {
                    Name = "Test Writer", CleanName = "testwriter", SortName = "testwriter", TitleSlug = "test-writer", Added = DateTime.UtcNow,
                    Path = "/test-writer", AudiobookMonitored = true, EbookMonitored = true
                });
                var seedBooks = new BookRepository(database, noop);
                var seedEditions = new EditionRepository(database, noop);
                var seedAliases = new ProviderAliasSeed(database, noop);
                var pending = new PendingReleaseRepository(database, noop);
                var targets = new List<Book>();
                for (var index = 0; index < count; index++)
                {
                    var book = seedBooks.Insert(new Book { AuthorId = author.Id, Title = "Paper Lanterns", CleanTitle = "paperlanterns", TitleSlug = "target-" + index,
                        Added = DateTime.UtcNow, MediaType = media, HardcoverBookId = "hc:7000", BaseBookId = "hc:7000" });
                    seedAliases.SeedAliases("Book", book.Id, "work", new[] { "hc:7000" });
                    seedEditions.Insert(new Edition { BookId = book.Id, Title = book.Title, ForeignEditionId = "hc:edition:" + (8100 + index),
                        ReadingFormatId = media == BookMediaType.Audiobook ? 2 : 3, Format = media == BookMediaType.Audiobook ? "audiobook" : "ebook", Monitored = true });
                    targets.Add(book);
                    pending.Insert(new PendingRelease
                    {
                        AuthorId = author.Id, Title = "Test Writer - Paper Lanterns", Added = DateTime.UtcNow, Reason = PendingReleaseReason.Delay,
                        Release = new ReleaseInfo { Title = "Test Writer - Paper Lanterns", IndexerId = 7, PublishDate = DateTime.UtcNow },
                        ParsedBookInfo = new ParsedBookInfo { AuthorName = author.Name, BookTitle = book.Title, Quality = new QualityModel(media == BookMediaType.Audiobook ? Quality.M4B : Quality.EPUB) },
                        AdditionalInfo = new PendingReleaseAdditionalInfo { BookIds = new List<int> { book.Id } }
                    });
                }
                var sql = new List<(string Kind, string Text)>();
                var observed = Proxy<IMainDatabase>((method, args) =>
                {
                    if (method.Name != nameof(IMainDatabase.OpenConnection)) return method.Invoke(database, args);
                    var connection = database.OpenConnection();
                    return Proxy<IDbConnection>((connectionMethod, connectionArgs) =>
                    {
                        var result = connectionMethod.Invoke(connection, connectionArgs);
                        return result is IDbCommand command ? Proxy<IDbCommand>((commandMethod, commandArgs) =>
                        {
                            if (commandMethod.Name is nameof(IDbCommand.ExecuteReader) or nameof(IDbCommand.ExecuteNonQuery) or nameof(IDbCommand.ExecuteScalar))
                                sql.Add((commandMethod.Name, command.CommandText));
                            return commandMethod.Invoke(command, commandArgs);
                        }) : result;
                    });
                });
                var events = 0;
                var eventWriter = Proxy<IEventAggregator>((method, args) => { events++; return null; });
                var books = new BookRepository(observed, eventWriter);
                var authors = new AuthorRepository(observed, eventWriter, logger);
                var aliases = new ProviderAliasService(new ProviderAliasRepository(observed, eventWriter));
                var authorService = new AuthorService(authors, eventWriter, null, null, null, new CacheManager(), books, null, logger, null, null, null, observed, aliases);
                var editionService = new EditionService(new EditionRepository(observed, eventWriter), eventWriter, logger);
                var bookService = new BookService(books, editionService, eventWriter, authorService, null, null,
                    new SeriesBookLinkRepository(observed, eventWriter), null, logger, providerAliasService: aliases);
                var service = new PendingReleaseService(Proxy<IIndexerStatusService>((method, args) => new List<IndexerStatus>()),
                    new PendingReleaseRepository(observed, eventWriter), authorService,
                    Proxy<IParsingService>((method, args) => throw new InvalidOperationException("Unexpected title remap")),
                    null, null, null, Proxy<ICustomFormatCalculationService>((method, args) => new List<CustomFormat>()),
                    Proxy<IRemoteBookAggregationService>((method, args) => null), null, null, eventWriter, logger, bookService);
                var timer = Stopwatch.StartNew();
                var reports = service.GetPending();
                timer.Stop();
                var readers = sql.Count(item => item.Kind == nameof(IDbCommand.ExecuteReader));
                var scalars = sql.Count(item => item.Kind == nameof(IDbCommand.ExecuteScalar));
                var writes = sql.Count(item => item.Kind == nameof(IDbCommand.ExecuteNonQuery));
                TestContext.Progress.WriteLine($"C20_COMPLETE_SQL database={database.DatabaseType} media={media} contexts={count} total={sql.Count} readers={readers} scalars={scalars} nonqueries={writes} events={events} elapsedMs={timer.Elapsed.TotalMilliseconds:F3}");
                for (var index = 0; index < sql.Count; index++) TestContext.Progress.WriteLine($"C20_SQL[{index}] {sql[index].Kind}: {sql[index].Text.Replace('\r', ' ').Replace('\n', ' ')}");
                Assert.Multiple(() =>
                {
                    Assert.That(reports, Is.Empty); Assert.That(pending.All(), Is.Empty);
                    Assert.That(readers, Is.EqualTo(5)); Assert.That(scalars, Is.Zero); Assert.That(writes, Is.EqualTo(1)); Assert.That(events, Is.EqualTo(1));
                    Assert.That(sql.Any(item => item.Text.Contains("\"ProviderAliasIndex\"")), Is.False, "snapshots hydrate with their owning rows");
                    Assert.That(sql.Any(item => item.Text.Contains("\"Authors\"")), Is.True);
                    Assert.That(sql.Any(item => item.Text.Contains("\"Editions\"")), Is.True);
                });
            });
        }
    }
}
