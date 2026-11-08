using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Chaptarr.Core.Test.Common;
using Dapper;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    public class WhereBuilderUnaryFixture
    {
        public sealed class UnaryRow : ModelBase
        {
            public bool Enabled { get; set; }
            public string Name { get; set; }
            public int Value { get; set; }
            public int? NullableValue { get; set; }
            public bool? NullableEnabled { get; set; }
        }

        private sealed class Repository : BasicRepository<UnaryRow>
        {
            public Repository(IMainDatabase database)
                : base(database, InterfaceProxy.Proxy<IEventAggregator>((_, _) => null))
            {
            }

            public List<UnaryRow> Matching(Expression<Func<UnaryRow, bool>> predicate) => Query(predicate);
        }

        [OneTimeSetUp]
        public void RegisterModel()
        {
            TableMapping.Mapper.Entity<UnaryRow>("UnaryRows").RegisterModel();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void negated_conditions_select_the_complement_on_the_real_database(bool postgres)
        {
            WithRepository(postgres, (repository, _) =>
            {
                var excluded = new List<int> { 1, 3 };
                Assert.That(repository.Matching(row => !row.Enabled).Select(row => row.Id), Is.EquivalentTo(new[] { 2, 3 }));
                Assert.That(repository.Matching(row => !(row.Value == 1)).Select(row => row.Id), Is.EquivalentTo(new[] { 2, 3 }));
                Assert.That(repository.Matching(row => !(row.Enabled || row.Name == null)).Select(row => row.Id), Is.EqualTo(new[] { 2 }));
                Assert.That(repository.Matching(row => !excluded.Contains(row.Id)).Select(row => row.Id), Is.EqualTo(new[] { 2 }));
                Assert.That(repository.Matching(row => !row.Name.StartsWith("Al")).Select(row => row.Id), Is.EqualTo(new[] { 2 }));
                Assert.That(repository.Matching(row => !(row.Name == null)).Select(row => row.Id), Is.EquivalentTo(new[] { 1, 2 }));
                Assert.That(repository.Matching(row => (int?)row.Value == 2).Select(row => row.Id), Is.EqualTo(new[] { 2 }), "Existing nullable casts still work");
                Assert.That(() => repository.Matching(row => -row.Value == -1),
                    Throws.TypeOf<NotSupportedException>().With.Message.Contains("Negate"), "Unsupported unary operations must never silently change the predicate");
                Assert.That(() => repository.Matching(row => ~row.Value == -2),
                    Throws.TypeOf<NotSupportedException>().With.Message.Contains("Int32"));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void negation_preserves_sql_null_semantics_instead_of_linq_to_objects_semantics(bool postgres)
        {
            WithRepository(postgres, (repository, rows) =>
            {
                // SQL WHERE keeps only TRUE: NOT UNKNOWN is UNKNOWN, so NULL rows are excluded.
                // Callers that need NULL rows must include an explicit null condition.
                Assert.That(repository.Matching(row => !(row.NullableValue == 1)).Select(row => row.Id), Is.EqualTo(new[] { 2 }));
                Assert.That(rows.Where(row => !(row.NullableValue == 1)).Select(row => row.Id), Is.EqualTo(new[] { 2, 3 }),
                    "C# nullable equality has different semantics; the database predicate intentionally follows SQL");
                Assert.That(repository.Matching(row => !row.NullableEnabled == true).Select(row => row.Id), Is.EqualTo(new[] { 2 }));
                Assert.That(repository.Matching(row => !(row.NullableEnabled == true)).Select(row => row.Id), Is.EqualTo(new[] { 2 }));
                Assert.That(repository.Matching(row => row.NullableValue == null || !(row.NullableValue == 1)).Select(row => row.Id),
                    Is.EquivalentTo(new[] { 2, 3 }));
                Assert.That(repository.Matching(row => !(row.NullableValue == null)).Select(row => row.Id), Is.EquivalentTo(new[] { 1, 2 }));
            });
        }

        private static void WithRepository(bool postgres, Action<Repository, UnaryRow[]> test)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var rows = new[]
                {
                    new UnaryRow { Id = 1, Enabled = true, Name = "Alpha", Value = 1, NullableValue = 1, NullableEnabled = true },
                    new UnaryRow { Id = 2, Enabled = false, Name = "Beta", Value = 2, NullableValue = 2, NullableEnabled = false },
                    new UnaryRow { Id = 3, Enabled = false, Name = null, Value = 3 }
                };
                using (var connection = database.OpenConnection())
                {
                    connection.Execute("CREATE TABLE \"UnaryRows\" (\"Id\" INTEGER PRIMARY KEY, \"Enabled\" BOOLEAN NOT NULL, \"Name\" TEXT, \"Value\" INTEGER NOT NULL, \"NullableValue\" INTEGER, \"NullableEnabled\" BOOLEAN)");
                    connection.Execute("INSERT INTO \"UnaryRows\" (\"Id\", \"Enabled\", \"Name\", \"Value\", \"NullableValue\", \"NullableEnabled\") VALUES (@Id, @Enabled, @Name, @Value, @NullableValue, @NullableEnabled)", rows);
                }
                test(new Repository(database), rows);
            });
        }
    }
}
