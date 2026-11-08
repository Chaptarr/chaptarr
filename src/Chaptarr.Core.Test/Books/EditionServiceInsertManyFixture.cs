using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Chaptarr.Core.Test.Common;
using Dapper;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.Messaging.Events;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    [NonParallelizable]
    public class EditionServiceInsertManyFixture
    {
        [TestCase(false)]
        [TestCase(true)]
        public void unique_edition_slug_constraint_uses_the_existing_retry_suffix_format(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var events = Proxy<IEventAggregator>((_, _) => null);
                var logger = LogManager.GetCurrentClassLogger();
                var author = new AuthorRepository(database, events, logger).Insert(new Author
                {
                    Name = "Slug fixture", CleanName = "slugfixture", SortName = "slugfixture", TitleSlug = "slug-fixture",
                    Path = @"C:\Books\Slug fixture".AsOsAgnostic(), Added = DateTime.UtcNow
                });
                var book = new BookRepository(database, events).Insert(new Book
                {
                    Title = "Slug fixture", CleanTitle = "slugfixture", TitleSlug = "slug-fixture", AuthorId = author.Id, Added = DateTime.UtcNow
                });
                // Exercise the existing compatibility retry under a unique-slug schema.
                // Current fresh databases intentionally allow cloned provider editions.
                using (var connection = database.OpenConnection())
                    connection.Execute("CREATE UNIQUE INDEX \"IX_Editions_TitleSlug\" ON \"Editions\" (\"TitleSlug\")");
                var repository = new EditionRepository(database, events);
                Edition New(string id, string slug) => new Edition
                {
                    BookId = book.Id, Title = book.Title, TitleSlug = slug, ForeignEditionId = "gr:" + id,
                    ReadingFormatId = 2, Language = "eng", Monitored = false
                };
                repository.InsertMany(new[] { New("9100", "slug-fixture"), New("9101", "slug-fixture_2") });
                var incoming = new List<Edition> { New("9200", "slug-fixture"), New("9201", "slug-fixture") };
                new EditionService(repository, events, logger).InsertMany(incoming);
                Assert.That(incoming.Select(edition => edition.TitleSlug), Is.EqualTo(new[] { "slug-fixture_3", "slug-fixture_4" }));
                Assert.That(repository.FindByBook(new[] { book.Id }).Select(edition => edition.TitleSlug),
                    Is.EquivalentTo(new[] { "slug-fixture", "slug-fixture_2", "slug-fixture_3", "slug-fixture_4" }));
            });
        }

        [Test]
        public void debug_logging_preserves_inserted_images_without_reloading_editions(
            [Values(false, true)] bool postgres, [Values(false, true)] bool foreignIdPresent,
            [Values(1, 8)] int editionCount)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var logFactory = new LogFactory();
                var memory = new MemoryTarget();
                var logging = new LoggingConfiguration();
                logging.AddRule(LogLevel.Debug, LogLevel.Fatal, memory);
                logFactory.Configuration = logging;
                var logger = logFactory.GetLogger("EditionInsertion");
                Assert.That(logger.IsDebugEnabled, Is.True);

                var events = Proxy<IEventAggregator>((_, _) => null);
                var author = new AuthorRepository(database, events, logger).Insert(new Author
                {
                    Name = "Insertion fixture author", CleanName = "insertionfixtureauthor", SortName = "insertionfixtureauthor",
                    TitleSlug = "insertion-fixture-author", HardcoverAuthorId = "hc:8100",
                    Path = @"C:\Books\Insertion fixture author".AsOsAgnostic(), Added = DateTime.UtcNow
                });
                var book = new BookRepository(database, events).Insert(new Book
                {
                    Title = "Insertion fixture book", CleanTitle = "insertionfixturebook", TitleSlug = "insertion-fixture-book",
                    HardcoverBookId = "hc:8101", MediaType = BookMediaType.Audiobook,
                    AuthorId = author.Id, Added = DateTime.UtcNow
                });

                var statements = new List<string>();
                var observed = Proxy<IMainDatabase>((method, args) =>
                    method.Name == nameof(IMainDatabase.OpenConnection)
                        ? CommandObserver.Wrap(database.OpenConnection(), command => statements.Add(command.CommandText), includeWrites: true)
                        : method.Invoke(database, args));
                var repository = new EditionRepository(observed, events);
                List<Edition> Batch(int start) => Enumerable.Range(start, editionCount).Select(id => new Edition
                {
                    BookId = book.Id, Title = book.Title, TitleSlug = $"insertion-edition-{id}",
                    ForeignEditionId = foreignIdPresent ? $"gr:{id}" : null, HardcoverEditionId = $"hc:edition:{id}",
                    ReadingFormatId = 2, Language = "eng", Monitored = id == start,
                    Images = new List<NzbDrone.Core.MediaCover.MediaCover>
                    {
                        new(MediaCoverTypes.Cover, $"https://covers.example.invalid/{id}.jpg"),
                        new(MediaCoverTypes.Banner, $"https://covers.example.invalid/{id}-banner.png")
                    }
                }).ToList();

                repository.InsertMany(Batch(8200));
                var writerStatementCount = statements.Count;
                statements.Clear();
                var editions = Batch(8300);
                Exception failure = null;
                var timer = Stopwatch.StartNew();
                try
                {
                    new EditionService(repository, events, logger).InsertMany(editions);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                timer.Stop();
                var insertStatements = statements.ToList();
                var stored = new EditionRepository(database, events).Get(editions.Select(edition => edition.Id)).ToList();
                TestContext.WriteLine($"{(postgres ? "PostgreSQL" : "SQLite")}: {editionCount} editions, foreign ID present={foreignIdPresent}, " +
                    $"repository={writerStatementCount} SQL commands, Debug service={insertStatements.Count} SQL commands, {timer.Elapsed.TotalMilliseconds:F1} ms, " +
                    $"persisted={stored.Count}, images={stored.Sum(edition => edition.Images.Count)}, failure={failure}");

                Assert.Multiple(() =>
                {
                    Assert.That(failure, Is.Null);
                    Assert.That(insertStatements, Has.Count.EqualTo(writerStatementCount));
                    Assert.That(insertStatements.Any(sql => sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) &&
                        sql.Contains("Editions", StringComparison.OrdinalIgnoreCase)), Is.False);
                    Assert.That(stored, Has.Count.EqualTo(editionCount));
                    foreach (var edition in editions)
                    {
                        var saved = stored.Single(row => row.Id == edition.Id);
                        Assert.That(saved.ForeignEditionId, Is.EqualTo(edition.ForeignEditionId));
                        Assert.That(saved.HardcoverEditionId, Is.EqualTo(edition.HardcoverEditionId));
                        Assert.That(saved.Images.Select(image => (image.CoverType, image.Url, image.Extension)),
                            Is.EqualTo(edition.Images.Select(image => (image.CoverType, image.Url, image.Extension))));
                    }
                });
            });
        }
    }
}
