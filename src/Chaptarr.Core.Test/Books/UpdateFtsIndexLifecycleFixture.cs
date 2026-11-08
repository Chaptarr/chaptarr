using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Chaptarr.Core.Test.Common;
using Dapper;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    [NonParallelizable]
    public class UpdateFtsIndexLifecycleFixture
    {
        private static UpdateFtsCommandHandler Handler(IFtsMaintenanceService maintenance, IEditionFtsRepository repository, Logger logger)
        {
            var constructor = typeof(UpdateFtsCommandHandler).GetConstructors().Single();
            return (UpdateFtsCommandHandler)constructor.Invoke(constructor.GetParameters().Select(parameter =>
                parameter.ParameterType == typeof(IFtsMaintenanceService) ? (object)maintenance :
                parameter.ParameterType == typeof(IEditionFtsRepository) ? repository : logger).ToArray());
        }

        [TestCase(false, BookMediaType.Audiobook)]
        [TestCase(false, BookMediaType.Ebook)]
        [TestCase(true, BookMediaType.Audiobook)]
        [TestCase(true, BookMediaType.Ebook)]
        public void requests_should_not_rebuild_empty_indexes_and_existing_maintenance_should_restore_them(bool postgres, BookMediaType media)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var events = InterfaceProxy.Proxy<IEventAggregator>((_, _) => null);
                var authors = new BasicRepository<Author>(database, events);
                var books = new BasicRepository<Book>(database, events);
                var editions = new BasicRepository<Edition>(database, events);
                var author = authors.Insert(new Author { Name = "Recovery Reader", CleanName = "recoveryreader", Path = "/recovery", Added = DateTime.UtcNow });
                var targets = new Dictionary<BookMediaType, Book>();
                foreach (var side in new[] { BookMediaType.Audiobook, BookMediaType.Ebook })
                {
                    var book = books.Insert(new Book { AuthorId = author.Id, Title = "Recovery Story", CleanTitle = "recoverystory", MediaType = side, Added = DateTime.UtcNow });
                    editions.Insert(new Edition { BookId = book.Id, Title = book.Title, MatchingTitle = "recovery story" });
                    targets.Add(side, book);
                }

                using var connection = database.OpenConnection();
                var originalBindings = connection.Query<(int Id, int BookId)>("SELECT \"Id\", \"BookId\" FROM \"Editions\" ORDER BY \"Id\";").ToArray();
                if (!postgres) connection.Execute("DELETE FROM edition_fts;");
                var schemaVersion = postgres ? 0 : connection.ExecuteScalar<int>("PRAGMA schema_version;");
                using var factory = new LogFactory();
                var messages = new MemoryTarget { Layout = "${level} ${message}" };
                var logging = new LoggingConfiguration(factory);
                logging.AddRule(LogLevel.Debug, LogLevel.Fatal, messages);
                factory.Configuration = logging;
                var logger = factory.GetLogger("native-index-lifecycle");
                var readers = 0;
                var scalars = 0;
                var writes = 0;
                IDbConnection Open()
                {
                    var underlying = database.OpenConnection();
                    return InterfaceProxy.Proxy<IDbConnection>((method, args) =>
                    {
                        var result = method.Invoke(underlying, args);
                        if (result is not IDbCommand command) return result;
                        return InterfaceProxy.Proxy<IDbCommand>((call, values) =>
                        {
                            if (call.Name == nameof(IDbCommand.ExecuteReader)) readers++;
                            if (call.Name == nameof(IDbCommand.ExecuteScalar)) scalars++;
                            if (call.Name == nameof(IDbCommand.ExecuteNonQuery)) writes++;
                            return call.Invoke(command, values);
                        });
                    });
                }

                var observed = InterfaceProxy.Proxy<IMainDatabase>((method, args) => method.Name == nameof(IDatabase.OpenConnection) ? Open() : method.Invoke(database, args));
                var fts = new EditionFtsRepository(observed, logger);
                var first = fts.RecallBooks(author.Id, new[] { "Recovery" }, media);
                TestContext.Progress.WriteLine($"FTS_FIRST_REQUEST {database.DatabaseType} {media}: reader={readers}, scalar={scalars}, nonquery={writes}, results={first.Count}");
                if (!postgres)
                {
                    Assert.Multiple(() =>
                    {
                        Assert.That(first, Is.Empty);
                        Assert.That(writes, Is.Zero);
                        Assert.That(readers, Is.EqualTo(1));
                        Assert.That(scalars, Is.EqualTo(2));
                        Assert.That(connection.ExecuteScalar<int>("PRAGMA schema_version;"), Is.EqualTo(schemaVersion));
                        Assert.That(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM edition_fts;"), Is.Zero);
                        Assert.That(messages.Logs, Has.Some.Contains("Warn").And.Contains("empty").And.Contains("UpdateFts"));
                    });
                }
                else Assert.That(first.Select(row => row.BookId), Is.EqualTo(new[] { targets[media].Id }));

                var authorService = InterfaceProxy.Proxy<IAuthorService>((method, args) => method.Name switch
                {
                    nameof(IAuthorService.GetAllAuthors) => authors.All(),
                    nameof(IAuthorService.GetAuthor) => authors.Get((int)args[0]),
                    _ => throw new NotSupportedException(method.Name)
                });
                var maintenance = new FtsMaintenanceService(database, authorService, logger);
                var command = Handler(maintenance, fts, logger);
                if (media == BookMediaType.Audiobook) command.Handle(new ApplicationStartedEvent());
                else command.Execute(new UpdateFtsCommand());
                if (!postgres) Assert.That(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM edition_fts;"), Is.EqualTo(2));
                readers = 0;
                scalars = 0;
                writes = 0;
                for (var index = 0; index < 25; index++)
                    Assert.That(fts.RecallBooks(author.Id, new[] { "Recovery" }, media).Select(row => row.BookId), Is.EqualTo(new[] { targets[media].Id }));
                Assert.Multiple(() =>
                {
                    Assert.That(readers, Is.EqualTo(25));
                    Assert.That(scalars, Is.Zero);
                    Assert.That(writes, Is.Zero);
                    Assert.That(connection.Query<(int Id, int BookId)>("SELECT \"Id\", \"BookId\" FROM \"Editions\" ORDER BY \"Id\";"), Is.EqualTo(originalBindings));
                });
                TestContext.Progress.WriteLine($"FTS_WARM_REQUESTS {database.DatabaseType} {media}: calls=25, reader={readers}, scalar={scalars}, nonquery={writes}");
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void an_intentionally_empty_library_should_not_rebuild_on_request(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var connection = database.OpenConnection();
                var schemaVersion = postgres ? 0 : connection.ExecuteScalar<int>("PRAGMA schema_version;");
                var fts = new EditionFtsRepository(database, LogManager.GetCurrentClassLogger());
                Assert.That(fts.RecallBooks(null, new[] { "Recovery" }, BookMediaType.Ebook), Is.Empty);
                if (!postgres) Assert.That(connection.ExecuteScalar<int>("PRAGMA schema_version;"), Is.EqualTo(schemaVersion));
            });
        }
    }
}
