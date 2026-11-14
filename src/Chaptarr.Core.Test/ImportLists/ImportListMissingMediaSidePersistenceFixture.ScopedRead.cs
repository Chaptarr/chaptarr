using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using NLog;
using NLog.Config;
using NLog.Targets;
using Chaptarr.Core.Test.Common;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Exclusions;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.ImportLists
{
    public partial class ImportListMissingMediaSidePersistenceFixture
    {
        [TestCase(false, BookMediaType.Audiobook)]
        [TestCase(true, BookMediaType.Audiobook)]
        [TestCase(false, BookMediaType.Ebook)]
        [TestCase(true, BookMediaType.Ebook)]
        public void thin_identity_snapshot_preserves_book_isbn_exclusion_when_editions_are_empty(bool postgres, BookMediaType side)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var library = new StoredListLibrary(database);
                var requested = CatalogBook(side, "Excluded legacy book", "123");
                requested.Editions.Clear();
                library.Save(requested);
                using (var connection = database.OpenConnection())
                    connection.Execute("UPDATE \"Books\" SET \"ISBN10\" = '0441172717', \"ISBN13\" = '9780441172719' WHERE \"Id\" = @Id", new { requested.Id });

                var service = ImportListSyncServiceSearchOnAddFixture.BuildMissingMediaSideService(library.Author, requested, side, false,
                    library.AuthorService, library.Books, library.Editions, library.Pending, library.Roots, library.Queue);
                const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var lookup = typeof(ImportListSyncService).GetMethod("BuildImportListLocalLookup", flags)
                    .Invoke(service, new object[] { new HashSet<int> { requested.Id }, 0, null });
                var gate = typeof(ImportListSyncService).GetMethod("ShouldProcessBookReport", flags);
                var report = library.Report(requested, 0);
                var exclusions = new List<ImportListExclusion> { new() { ForeignId = "9780441172719", Name = requested.Title } };
                Assert.Multiple(() =>
                {
                    Assert.That(gate.Invoke(service, new object[] { report, new List<ImportListExclusion>(), lookup, null, null }), Is.True,
                        "The report must otherwise be eligible, not rejected because the fixture has no editions");
                    Assert.That(gate.Invoke(service, new object[] { report, exclusions, lookup, null, null }), Is.False,
                        "The actual list gate must see the legacy ISBN through its thin identity snapshot");
                    Assert.That(library.Commands, Is.Empty);
                    Assert.That(library.Calls.GetValueOrDefault(nameof(IBookService.GetImportListBookIdentities)), Is.EqualTo(1));
                });
            });
        }

        [TestCase(false, BookMediaType.Audiobook, 0)]
        [TestCase(false, BookMediaType.Audiobook, 1)]
        [TestCase(false, BookMediaType.Audiobook, 2)]
        [TestCase(false, BookMediaType.Ebook, 0)]
        [TestCase(false, BookMediaType.Ebook, 1)]
        [TestCase(false, BookMediaType.Ebook, 2)]
        [TestCase(true, BookMediaType.Audiobook, 0)]
        [TestCase(true, BookMediaType.Audiobook, 1)]
        [TestCase(true, BookMediaType.Audiobook, 2)]
        [TestCase(true, BookMediaType.Ebook, 0)]
        [TestCase(true, BookMediaType.Ebook, 1)]
        [TestCase(true, BookMediaType.Ebook, 2)]
        public void list_sync_uses_thin_identity_snapshot_and_warns_before_broad_edition_fallback(bool postgres, BookMediaType side, int failureMode)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var logFactory = new LogFactory();
                var messages = new MemoryTarget { Layout = "${level}|${message}" };
                var config = new LoggingConfiguration();
                config.AddRule(LogLevel.Debug, LogLevel.Fatal, messages);
                logFactory.Configuration = config;
                var library = new StoredListLibrary(database);
                var requested = CatalogBook(side, "Requested book", "123");
                requested.Overview = new string('x', 50000);
                requested.HardcoverBookId = "hc:9123";
                requested.RemoteProviderIds = new HashSet<string> { "gr:123", "hc:9123" };
                library.Save(requested);
                var unrelated = Enumerable.Range(1, 20).Select(index => CatalogBook(side, $"Other book {index}", (index + 1000).ToString())).ToList();
                foreach (var book in unrelated) library.Save(book);
                var editionReads = new List<int[]>();
                var editions = Proxy<IEditionService>((method, args) =>
                {
                    if (method.Name == nameof(IEditionService.GetEditionRowsByBook))
                        editionReads.Add(((IEnumerable<int>)args[0]).ToArray());
                    return method.Invoke(library.Editions, args);
                });
                var books = Proxy<IBookService>((method, args) =>
                {
                    if (method.Name == nameof(IBookService.GetAllBooks))
                        throw new AssertionException("List indexing must not read full book metadata or series links");
                    if (method.Name == nameof(IBookService.FindBookIdsByProviderIds) && failureMode != 0)
                    {
                        if (failureMode == 2) throw new InvalidOperationException("Injected lookup outage");
                        var keys = ((IEnumerable<(string Scope, string Provider, string Id)>)args[0]).ToList();
                        ((Action<(string Scope, string Provider, string Id)>)args[1])(keys.First());
                    }
                    var before = library.ReaderStatements.Count;
                    var result = method.Invoke(library.Books, args);
                    if (method.Name == nameof(IBookService.GetImportListBookIdentities))
                    {
                        Assert.That(library.ReaderStatements.Skip(before), Has.Count.EqualTo(1));
                        var snapshot = (List<Book>)result;
                        Assert.That(snapshot, Has.Count.EqualTo(21));
                        Assert.That(snapshot.All(book => book.Overview == null && book.LazySeriesLinks?.IsLoaded != true), Is.True);
                        Assert.That(BookIdentity.GetStableWorkProviderIdentityTokens(snapshot.Single(book => book.Id == requested.Id)),
                            Is.EquivalentTo(new[] { "gr:123", "hc:9123" }));
                    }
                    return result;
                });
                var service = ImportListSyncServiceSearchOnAddFixture.BuildMissingMediaSideService(library.Author, requested, side, false,
                    library.AuthorService, books, editions, library.Pending, library.Roots, library.Queue,
                    reports: new() { library.Report(requested, 0) }, addMissingAuthors: false, logger: logFactory.GetLogger("ListLookup"));
                service.Execute(new ImportListSyncCommand(1));
                Assert.That(editionReads, Has.Count.EqualTo(1));
                Assert.That(editionReads.Single(), Is.EquivalentTo(failureMode == 0
                    ? new[] { requested.Id } : new[] { requested.Id }.Concat(unrelated.Select(book => book.Id))));
                var fallbackWarnings = messages.Logs.Where(message => message.StartsWith("Warn|") && message.Contains("loading editions for all 21 books")).ToList();
                Assert.That(fallbackWarnings, Has.Count.EqualTo(failureMode == 0 ? 0 : 1));
                Assert.That(messages.Logs.Any(message => message.Contains("batch lookup of") && message.Contains("took")), Is.True);
                Assert.That(library.Calls.GetValueOrDefault(nameof(IBookService.GetImportListBookIdentities)), Is.EqualTo(1));
                Assert.That(library.Books.GetBook(requested.Id).Overview, Has.Length.EqualTo(50000), "Partial identity rows must never overwrite saved metadata.");
                Assert.That(library.Books.GetBook(requested.Id).IsMonitored(), Is.True);
                Assert.That(library.Books.GetExistingBooks(unrelated.Select(book => book.Id).ToList()).Any(book => book.IsMonitored()), Is.False);
                TestContext.WriteLine($"SCOPED_LIST_EDITIONS engine={(postgres ? "PostgreSQL" : "SQLite")} side={side} failureMode={failureMode} requestedBooks=1 catalogBooks=21 editionReadBooks={editionReads.Single().Length}");
            });
        }
    }
}
