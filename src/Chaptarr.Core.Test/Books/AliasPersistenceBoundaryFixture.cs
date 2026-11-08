using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Chaptarr.Core.Test.Common;
using Chaptarr.Core.Test.MetadataSource.BookInfo;
using Dapper;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore.Migration;
using NzbDrone.Core.MetadataSource.BookInfo;
using NzbDrone.Core.MetadataSource.BookInfo.V5;
using NzbDrone.Core.Messaging.Events;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    [NonParallelizable]
    public class AliasPersistenceBoundaryFixture
    {
        private const string Artifact = "System.Collections.Generic.List`1[System.String]";

        [Test]
        public void author_and_edition_clones_share_unknown_empty_and_rejected_input_semantics(
            [Values("unknown", "empty", "blank", "artifact", "mixed")] string input)
        {
            var ids = input switch
            {
                "unknown" => null,
                "empty" => new HashSet<string>(),
                "blank" => new HashSet<string> { " ", "\t" },
                "artifact" => new HashSet<string> { Artifact },
                _ => new HashSet<string> { " gr:8001 ", Artifact }
            };
            var edition = new Edition { RemoteProviderIds = ids };
            var book = new Book { Author = new Author { RemoteProviderIds = ids } };
            var copiedAuthor = new Author();
            copiedAuthor.UseMetadataFrom(book.Author);
            var copies = new[] { copiedAuthor.RemoteProviderIds, RefreshEntityCopy.CloneEdition(edition).RemoteProviderIds,
                RefreshEntityCopy.CloneBook(book, false).Author.RemoteProviderIds };
            foreach (var copy in copies)
            {
                if (input is "unknown" or "blank" or "artifact") Assert.That(copy, Is.Null);
                else Assert.That(copy, Is.EquivalentTo(input == "empty" ? Array.Empty<string>() : new[] { "gr:8001" }));
                if (copy != null) Assert.That(copy, Is.Not.SameAs(ids));
            }
        }

        [Test]
        public void rejected_edition_identity_artifact_does_not_throw_or_create_identity()
        {
            var edition = new Edition { ForeignEditionId = "hc:edition:2001", RemoteProviderIds = new HashSet<string> { Artifact, "gr:8001" } };
            Assert.That(BookEditionIdentity.GetEditionProviderIds(edition), Does.Contain("gr:8001").And.Not.Contain(Artifact));
        }

        [TestCase(null)]
        [TestCase("123")]
        public void goodreads_alias_order_does_not_select_primary_scalar(string explicitPrimary)
        {
            var payload = new V5Edition
            {
                Id = "hc:edition:2001", Title = "Fixture Book", GoodreadsEditionId = explicitPrimary,
                ProviderIdsAllCamel = new Dictionary<string, List<string>> { ["gr"] = new() { "gr:456", "gr:123" } }
            };
            var proxy = BookInfoProxyCanonicalLookupFixture.CreateProxy(null);
            var edition = (Edition)typeof(BookInfoProxy).GetMethod("ConvertV5EditionToDomain", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(proxy, new object[] { payload, BookMediaType.Audiobook });
            Assert.Multiple(() =>
            {
                Assert.That(edition.GoodreadsEditionId, Is.EqualTo(explicitPrimary == null ? null : (long?)123));
                Assert.That(edition.RemoteProviderIds, Does.Contain("gr:123").And.Contain("gr:456"));
                Assert.That(edition.ForeignEditionId, Is.EqualTo("hc:edition:2001-audiobook"));
            });
        }

        [TestCase("author", "goodreads", "8001", "gr:8001", false)]
        [TestCase("author", "goodreads", "8001", "gr:8001", true)]
        [TestCase("author", "hardcover", "8001", "hc:8001", false)]
        [TestCase("author", "hardcover", "8001", "hc:8001", true)]
        [TestCase("author", "open_library", "OL8001A", "ol:OL8001A", false)]
        [TestCase("author", "open_library", "OL8001A", "ol:OL8001A", true)]
        [TestCase("author", "google_books", "BOOK8001", "gb:BOOK8001", false)]
        [TestCase("author", "google_books", "BOOK8001", "gb:BOOK8001", true)]
        [TestCase("author", "audnexus", "b00alias01", "az:B00ALIAS01", false)]
        [TestCase("author", "audnexus", "b00alias01", "az:B00ALIAS01", true)]
        [TestCase("work", "goodreads", "8001", "gr:8001", false)]
        [TestCase("work", "goodreads", "8001", "gr:8001", true)]
        [TestCase("work", "hardcover", "8001", "hc:8001", false)]
        [TestCase("work", "hardcover", "8001", "hc:8001", true)]
        [TestCase("work", "open_library", "OL8001W", "ol:OL8001W", false)]
        [TestCase("work", "open_library", "OL8001W", "ol:OL8001W", true)]
        [TestCase("edition", "goodreads", "8001", "gr:8001", false)]
        [TestCase("edition", "goodreads", "8001", "gr:8001", true)]
        [TestCase("edition", "hardcover", "hc:edition:8001", "hc:edition:8001", false)]
        [TestCase("edition", "hardcover", "hc:edition:8001", "hc:edition:8001", true)]
        [TestCase("edition", "open_library", "OL8001M", "ol:OL8001M", false)]
        [TestCase("edition", "open_library", "OL8001M", "ol:OL8001M", true)]
        [TestCase("edition", "google_books", "BOOK8001", "gb:BOOK8001", false)]
        [TestCase("edition", "google_books", "BOOK8001", "gb:BOOK8001", true)]
        [TestCase("edition", "audible", "b00alias01", "az:B00ALIAS01", false)]
        [TestCase("edition", "audible", "b00alias01", "az:B00ALIAS01", true)]
        public void raw_provider_aliases_keep_attested_namespace_without_selecting_primary(
            string entity, string provider, string value, string expected, bool modern)
        {
            var map = modern ? new Dictionary<string, List<string>> { [provider] = new() { value } } : null;
            var legacy = modern ? null : new Dictionary<string, object> { [provider] = value };
            var proxy = BookInfoProxyCanonicalLookupFixture.CreateProxy(null);
            HashSet<string> aliases;
            if (entity == "author")
            {
                var payload = new V5AuthorResponse
                {
                    Author = new V5AuthorData { Id = "hc:9001", Name = "Fixture Author", SortName = "Fixture Author",
                        ProviderIdsAllCamel = map, LegacyProviderIds = legacy }
                };
                var author = (Author)typeof(BookInfoProxy).GetMethod("ConvertV5AuthorToDomain", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(proxy, new object[] { payload, null });
                aliases = author.RemoteProviderIds;
                Assert.That(author.GoodreadsAuthorId, Is.Null);
                Assert.That(author.HardcoverAuthorId, Is.EqualTo("hc:9001"));
            }
            else if (entity == "work")
            {
                var payload = new V5Book { Id = "hc:9002", BaseBookId = "hc:9002", Title = "Fixture Book",
                    GoodreadsWorkId = "gr:9003", HardcoverBookId = "hc:9002", OpenLibraryWorkId = "ol:OL9004W",
                    ProviderIdsAllCamel = map, LegacyProviderIdsCamel = legacy };
                var book = (Book)typeof(BookInfoProxy).GetMethod("ConvertV5BookToDomain", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(proxy, new object[] { payload, new Author { Name = "Fixture Author" }, BookMediaType.Audiobook });
                aliases = book.RemoteProviderIds;
            }
            else
            {
                var payload = new V5Edition { Id = "hc:edition:2001", Title = "Fixture Book",
                    ProviderIdsAllCamel = map, LegacyProviderIds = legacy };
                var edition = (Edition)typeof(BookInfoProxy).GetMethod("ConvertV5EditionToDomain", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(proxy, new object[] { payload, BookMediaType.Audiobook });
                aliases = edition.RemoteProviderIds;
                Assert.That(edition.GoodreadsEditionId, Is.Null);
            }

            Assert.That(aliases, Is.Not.Null);
            Assert.That(aliases, Does.Contain(expected));
            if (value != expected) Assert.That(aliases, Does.Not.Contain(value));
        }

        [TestCase("goodreads", "hc:8001")]
        [TestCase("unknown", "gr:8001")]
        [TestCase("goodreads", "gr:")]
        [TestCase("goodreads", "bad id")]
        [TestCase("goodreads", Artifact)]
        public void rejected_provider_alias_map_is_unknown_and_cannot_invent_identity(string provider, string value)
        {
            var payload = new V5Edition { Id = "hc:edition:2001", Title = "Fixture Book",
                ProviderIdsAllCamel = new Dictionary<string, List<string>> { [provider] = new() { value } } };
            var proxy = BookInfoProxyCanonicalLookupFixture.CreateProxy(null);
            var edition = (Edition)typeof(BookInfoProxy).GetMethod("ConvertV5EditionToDomain", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(proxy, new object[] { payload, BookMediaType.Audiobook });
            Assert.That(edition.RemoteProviderIds, Is.Null);
            Assert.That(edition.GoodreadsEditionId, Is.Null);
        }

        [Test]
        public void shared_alias_ingress_preserves_null_empty_and_blank_evidence()
        {
            var coercion = typeof(V5AuthorResponse).Assembly.GetType("NzbDrone.Core.MetadataSource.BookInfo.V5Resource.ProviderIdMapCoercion");
            var method = coercion.GetMethod("ToAliasSet", BindingFlags.Public | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);
            Assert.That(method.Invoke(null, new object[] { null }), Is.Null);
            Assert.That(method.Invoke(null, new object[] { new Dictionary<string, List<string>>() }), Is.Empty);
            Assert.That(method.Invoke(null, new object[] { new Dictionary<string, List<string>> { ["gr"] = new() { " ", "\t" } } }), Is.Null);
        }

        [Test]
        public void mixed_provider_alias_map_keeps_valid_values_and_empty_map_stays_empty()
        {
            var proxy = BookInfoProxyCanonicalLookupFixture.CreateProxy(null);
            var payload = new V5Edition { Id = "hc:edition:2001", Title = "Fixture Book",
                ProviderIdsAllCamel = new Dictionary<string, List<string>> { ["goodreads"] = new() { "8001", Artifact, "hc:8002" } } };
            var convert = typeof(BookInfoProxy).GetMethod("ConvertV5EditionToDomain", BindingFlags.Instance | BindingFlags.NonPublic);
            var edition = (Edition)convert.Invoke(proxy, new object[] { payload, BookMediaType.Audiobook });
            Assert.That(edition.RemoteProviderIds, Is.EquivalentTo(new[] { "gr:8001" }));
            payload.ProviderIdsAllCamel.Clear();
            edition = (Edition)convert.Invoke(proxy, new object[] { payload, BookMediaType.Audiobook });
            Assert.That(edition.RemoteProviderIds, Is.Empty);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void edition_alias_backfill_continues_after_malformed_collection_and_reports_aggregate(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var events = Proxy<IEventAggregator>((_, _) => null);
                var author = new AuthorRepository(database, events, LogManager.GetCurrentClassLogger()).Insert(new Author
                {
                    Name = "Fixture Author", CleanName = "fixtureauthor", TitleSlug = Guid.NewGuid().ToString("N"),
                    Path = @"C:\Audio\Fixture Author", Added = DateTime.UtcNow
                });
                var book = new BookRepository(database, events).Insert(new Book
                {
                    AuthorId = author.Id, Title = "Fixture Book", CleanTitle = "fixturebook",
                    TitleSlug = Guid.NewGuid().ToString("N"), Added = DateTime.UtcNow
                });
                using var connection = database.OpenConnection();
                connection.Execute(@"INSERT INTO ""Editions"" (""BookId"", ""Title"", ""ForeignEditionId"", ""GoodreadsEditionId"", ""Asins"", ""Monitored"")
                    VALUES (@BookId, 'Malformed collection', 'hc:edition:2001', 123, 'invalid-json', @Monitored),
                           (@BookId, 'Valid collection', 'hc:edition:2002', 456, '[""B00REGION2""]', @Monitored)", new { BookId = book.Id, Monitored = false });
                using var factory = new LogFactory();
                var target = new MemoryTarget { Layout = "${level}|${message}" };
                var configuration = new LoggingConfiguration(factory);
                configuration.AddRuleForAllLevels(target);
                factory.Configuration = configuration;
                add_import_list_monitoring_options.BackfillEditionAliases(connection, null, factory.GetLogger("alias-backfill"));
                add_import_list_monitoring_options.BackfillEditionAliases(connection, null, factory.GetLogger("alias-backfill"));
                var aliases = connection.Query<string>(@"SELECT ""Provider"" || ':' || ""NormalizedProviderId"" FROM ""ProviderAliasIndex"" WHERE ""EntityType"" = 'Edition'").ToList();
                Assert.Multiple(() =>
                {
                    Assert.That(aliases, Is.EquivalentTo(new[] { "hc:2001", "hc:edition:2001", "gr:123", "hc:2002", "hc:edition:2002", "gr:456", "az:B00REGION2" }));
                    Assert.That(target.Logs.Count(message => message.Contains("1 malformed ASIN collections and 0 failed editions out of 2 stored editions")), Is.EqualTo(2));
                    Assert.That(target.Logs.Any(message => message.Contains("2 stored editions and 7 attested identifiers")), Is.True);
                });
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void numeric_goodreads_column_lookup_survives_absent_alias_index_and_provider_collision(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var events = Proxy<IEventAggregator>((_, _) => null);
                var authors = new AuthorRepository(database, events, LogManager.GetCurrentClassLogger());
                var author = authors.Insert(new Author { Name = "Fixture Author", CleanName = "fixtureauthor", TitleSlug = Guid.NewGuid().ToString("N"), Path = @"C:\Audio\Fixture Author", Added = DateTime.UtcNow });
                var books = new BookRepository(database, events);
                var book = books.Insert(new Book { AuthorId = author.Id, Title = "Fixture Book", CleanTitle = "fixturebook", TitleSlug = Guid.NewGuid().ToString("N"), Added = DateTime.UtcNow });
                var editions = new EditionRepository(database, events);
                var intended = editions.Insert(new Edition { BookId = book.Id, Title = book.Title, TitleSlug = Guid.NewGuid().ToString("N"), ForeignEditionId = "hc:edition:2001-audiobook", GoodreadsEditionId = 44767458 });
                editions.Insert(new Edition { BookId = book.Id, Title = book.Title, TitleSlug = Guid.NewGuid().ToString("N"), ForeignEditionId = "hc:edition:44767458-audiobook" });
                using (var connection = database.OpenConnection()) connection.Execute(@"DELETE FROM ""ProviderAliasIndex"" WHERE ""EntityType"" = 'Edition'");
                var reopened = new EditionRepository(database, events);
                var results = reopened.FindAllByProviderIds("gr", new[] { "44767458", "44767458-audiobook", "44767459", "", "notnumeric", "hc:44767458" });
                Assert.Multiple(() =>
                {
                    Assert.That(results["44767458"].Select(edition => edition.Id), Is.EqualTo(new[] { intended.Id }));
                    Assert.That(results["44767458-audiobook"].Select(edition => edition.Id), Is.EqualTo(new[] { intended.Id }));
                    foreach (var invalid in new[] { "44767459", "", "notnumeric", "hc:44767458" }) Assert.That(results[invalid], Is.Empty);
                });
                Assert.That(reopened.FindAllByProviderIds("gr", new[] { Artifact })[Artifact], Is.Empty);
            });
        }
    }
}
