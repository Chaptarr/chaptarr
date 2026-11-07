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
    public class BookRepositoryTitleSlugFixture
    {
        private sealed class StubEventAggregator : IEventAggregator
        {
            public void PublishEvent<TEvent>(TEvent @event)
                where TEvent : class, IEvent
            {
            }
        }

        private string _databasePath;
        private BookRepository _subject;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            if (TableMapping.Mapper.TableMap.Count == 0)
            {
                TableMapping.Map();
            }
        }

        [SetUp]
        public void SetUp()
        {
            _databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"book_slug_{Guid.NewGuid():N}.db");
            var connectionString = new SqliteConnectionStringBuilder { DataSource = _databasePath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();

            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                connection.Execute(@"CREATE TABLE ""Books"" (""Id"" INTEGER PRIMARY KEY, ""AuthorId"" INTEGER NOT NULL, ""TitleSlug"" TEXT);");
            }

            var database = new Database("main", () =>
            {
                var conn = new SqliteConnection(connectionString);
                conn.Open();
                return conn;
            });

            _subject = new BookRepository(new MainDatabase(database), new StubEventAggregator());
            SqliteConnection.ClearAllPools();
        }

        [TearDown]
        public void TearDown()
        {
            SqliteConnection.ClearAllPools();
            try
            {
                if (File.Exists(_databasePath))
                {
                    File.Delete(_databasePath);
                }
            }
            catch
            {
            }
        }

        private void Seed(params (int Id, int AuthorId, string Slug)[] rows)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString());
            connection.Open();
            foreach (var row in rows)
            {
                connection.Execute(@"INSERT INTO ""Books"" (""Id"", ""AuthorId"", ""TitleSlug"") VALUES (@Id, @AuthorId, @Slug);", new { row.Id, row.AuthorId, row.Slug });
            }
        }

        [Test]
        public void title_slugs_by_author_should_return_only_that_authors_non_empty_slugs_with_exact_values()
        {
            Seed((1, 1, "The-Book"), (2, 1, "the-book_2"), (3, 1, ""), (4, 1, null), (5, 2, "other-author"));

            var result = _subject.GetTitleSlugsByAuthorId(1);

            Assert.That(result.Select(r => r.Id), Is.EquivalentTo(new[] { 1, 2 }));
            Assert.That(result.Select(r => r.TitleSlug), Is.EquivalentTo(new[] { "The-Book", "the-book_2" }));
        }

        [Test]
        public void candidate_query_should_return_the_base_and_numbered_variants_case_insensitively_and_nothing_unrelated()
        {
            Seed(
                (1, 1, "the-book"),
                (2, 1, "THE-BOOK_2"),
                (3, 1, "the-book_10"),
                (4, 1, "the-book-two"),
                (5, 1, "the-bookx"),
                (6, 1, "unrelated"),
                (7, 1, ""),
                (8, 2, "the-book"));

            var result = _subject.GetTitleSlugsByAuthorId(1, new[] { "The-Book" });

            Assert.That(result.Select(r => r.Id), Is.SupersetOf(new[] { 1, 2, 3 }));
            Assert.That(result.Select(r => r.Id), Has.None.EqualTo(4));
            Assert.That(result.Select(r => r.Id), Has.None.EqualTo(5));
            Assert.That(result.Select(r => r.Id), Has.None.EqualTo(6));
            Assert.That(result.Select(r => r.Id), Has.None.EqualTo(8), "another author's slugs never count");
        }

        [Test]
        public void candidate_query_should_treat_like_wildcards_in_the_base_slug_literally()
        {
            Seed((1, 1, "axb"), (2, 1, "azzzb"), (3, 1, "a_b"), (4, 1, "a%b"), (5, 1, "a_b_2"));

            var underscore = _subject.GetTitleSlugsByAuthorId(1, new[] { "a_b" });
            var percent = _subject.GetTitleSlugsByAuthorId(1, new[] { "a%b" });

            Assert.That(underscore.Select(r => r.Id), Is.SupersetOf(new[] { 3, 5 }));
            Assert.That(underscore.Select(r => r.Id), Has.None.EqualTo(1));
            Assert.That(percent.Select(r => r.Id), Is.EquivalentTo(new[] { 4 }));
        }

        [Test]
        public void candidate_query_should_cover_every_candidate_in_one_call()
        {
            Seed((1, 1, "alpha"), (2, 1, "beta_3"), (3, 1, "gamma"));

            var result = _subject.GetTitleSlugsByAuthorId(1, new[] { "alpha", "beta", "delta" });

            Assert.That(result.Select(r => r.Id), Is.EquivalentTo(new[] { 1, 2 }));
        }

        [Test]
        public void candidate_query_should_fall_back_to_all_slugs_for_non_ascii_or_too_many_candidates()
        {
            Seed((1, 1, "ünder"), (2, 1, "plain"), (3, 1, "other"));

            var nonAscii = _subject.GetTitleSlugsByAuthorId(1, new[] { "Ünder" });
            var many = _subject.GetTitleSlugsByAuthorId(1, Enumerable.Range(0, 500).Select(i => $"slug-{i}").ToList());

            Assert.That(nonAscii.Select(r => r.Id), Is.EquivalentTo(new[] { 1, 2, 3 }), "case folding of non-ASCII differs between databases, so all slugs are returned");
            Assert.That(many.Select(r => r.Id), Is.EquivalentTo(new[] { 1, 2, 3 }));
        }
    }
}
