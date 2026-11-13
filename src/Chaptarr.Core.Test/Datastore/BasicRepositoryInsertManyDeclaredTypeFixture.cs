using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    public class BasicRepositoryInsertManyDeclaredTypeFixture
    {
        private sealed class HandlerColumnModel : ModelBase
        {
            // Handler registered for the base type Command; the value's runtime type is a derived command.
            public Command Body { get; set; }

            // Handlers registered for the interface and the concrete type.
            public IDictionary<string, string> Extra { get; set; }
            public HashSet<int> Ids { get; set; }

            public string Name { get; set; }
        }

        private string _databasePath;
        private string _connectionString;
        private BasicRepository<HandlerColumnModel> _repository;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            if (TableMapping.Mapper.TableMap.Count == 0)
            {
                TableMapping.Map();
            }

            TableMapping.Mapper.Entity<HandlerColumnModel>("HandlerColumnModels").RegisterModel();
        }

        [SetUp]
        public void SetUp()
        {
            _databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"insertmany_{Guid.NewGuid():N}.db");
            _connectionString = new SqliteConnectionStringBuilder { DataSource = _databasePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();

            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                connection.Execute(@"CREATE TABLE ""HandlerColumnModels"" (""Id"" INTEGER PRIMARY KEY AUTOINCREMENT, ""Body"" TEXT NULL, ""Extra"" TEXT NULL, ""Ids"" TEXT NULL, ""Name"" TEXT NULL);");
            }

            var database = new Database("insertmany-test", () =>
            {
                var connection = new SqliteConnection(_connectionString);
                connection.Open();
                return connection;
            });

            _repository = new BasicRepository<HandlerColumnModel>(database, new StubEventAggregator());
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

        private List<dynamic> Rows()
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            return connection.Query(@"SELECT ""Id"", ""Body"", ""Extra"", ""Ids"", ""Name"" FROM ""HandlerColumnModels"" ORDER BY ""Id""").ToList();
        }

        [Test]
        public void insert_many_should_apply_the_base_type_handler_to_a_derived_runtime_type()
        {
            var models = new List<HandlerColumnModel>
            {
                new HandlerColumnModel { Name = "a", Body = new RefreshAuthorCommand { AuthorId = 11 } },
                new HandlerColumnModel { Name = "b", Body = new RefreshAuthorCommand { AuthorId = 12 } }
            };

            _repository.InsertMany(models);

            var rows = Rows();
            Assert.That(rows, Has.Count.EqualTo(2));
            Assert.That((string)rows[0].Body, Does.Match("(?i)\"authorId\"\\s*:\\s*11"));
            Assert.That((string)rows[1].Body, Does.Match("(?i)\"authorId\"\\s*:\\s*12"));
            Assert.That(models.Select(m => m.Id), Is.All.GreaterThan(0));

            var reloaded = _repository.All().OrderBy(m => m.Id).ToList();
            Assert.That(reloaded[0].Body, Is.TypeOf<RefreshAuthorCommand>());
            Assert.That(((RefreshAuthorCommand)reloaded[1].Body).AuthorId, Is.EqualTo(12));
        }

        [Test]
        public void insert_many_should_store_the_same_value_as_single_insert()
        {
            var single = new HandlerColumnModel { Name = "single", Body = new RefreshAuthorCommand { AuthorId = 5 } };
            _repository.Insert(single);

            _repository.InsertMany(new List<HandlerColumnModel>
            {
                new HandlerColumnModel { Name = "batch", Body = new RefreshAuthorCommand { AuthorId = 5 } }
            });

            var rows = Rows();
            Assert.That((string)rows[1].Body, Is.EqualTo((string)rows[0].Body));
        }

        [Test]
        public void insert_many_should_keep_handling_columns_whose_runtime_type_has_its_own_handler_and_nulls()
        {
            _repository.InsertMany(new List<HandlerColumnModel>
            {
                new HandlerColumnModel
                {
                    Name = "full",
                    Body = new RefreshAuthorCommand { AuthorId = 1 },
                    Extra = new Dictionary<string, string> { ["k"] = "v" },
                    Ids = new HashSet<int> { 3, 4 }
                },
                new HandlerColumnModel { Name = "empty" }
            });

            var rows = Rows();
            Assert.That((string)rows[0].Extra, Does.Contain("\"k\""));
            Assert.That((string)rows[0].Ids, Does.Contain("3"));
            Assert.That((object)rows[1].Body, Is.Null);
            Assert.That((object)rows[1].Extra, Is.Null);
            Assert.That((object)rows[1].Ids, Is.Null);
        }

        private sealed class StubEventAggregator : IEventAggregator
        {
            public void PublishEvent<TEvent>(TEvent @event)
                where TEvent : class, IEvent
            {
            }
        }

        [Test]
        public void every_concrete_command_type_should_have_the_command_handler_registered()
        {
            // Guards against a command type being added that the discovery in CommandConverter silently skips
            // (another assembly, or a name that does not end in 'Command'): its batch insert would fail again on Postgres.
            var commandTypes = typeof(Command).Assembly.GetTypes()
                .Where(t => typeof(Command).IsAssignableFrom(t) && t.IsClass && !t.IsAbstract && !t.IsGenericTypeDefinition)
                .ToList();

            Assert.That(commandTypes, Is.Not.Empty);

            var notRegistered = commandTypes.Where(t => !SqlMapper.HasTypeHandler(t)).Select(t => t.FullName).ToList();

            Assert.That(notRegistered, Is.Empty, "command types without the Dapper command type handler: " + string.Join(", ", notRegistered));
        }
    }
}
