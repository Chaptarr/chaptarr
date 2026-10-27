using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test.Common;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;

namespace Chaptarr.Core.Test.Books
{
    public partial class RefreshEditionServiceNullProviderIdFixture
    {
        [TestCase(false)]
        [TestCase(true)]
        public void conflicting_stable_edition_identities_must_warn_and_preserve_both_local_files(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var logFactory = new LogFactory();
                var memory = new MemoryTarget { Layout = "${level}|${message}" };
                var logging = new LoggingConfiguration();
                logging.AddRule(LogLevel.Warn, LogLevel.Fatal, memory);
                logFactory.Configuration = logging;
                var h = new Harness(database, logFactory.GetLogger(nameof(RefreshEditionServiceNullProviderIdFixture)));
                var first = h.Seed(null);
                var second = h.Seed(null);
                first.Monitored = false;
                first.HardcoverEditionId = "hc:edition:202";
                first.Title = "Saved Hardcover recording";
                first.Overview = "Saved Hardcover overview";
                second.GoodreadsEditionId = 303;
                second.Title = "Saved Goodreads recording";
                second.Overview = "Saved Goodreads overview";
                h.Editions.UpdateMany(new List<Edition> { first, second });
                var firstFile = h.AddFile(first, @"C:\Books\Saved Hardcover recording.m4b".AsOsAgnostic());
                var secondFile = h.AddFile(second, @"C:\Books\Saved Goodreads recording.m4b".AsOsAgnostic());
                var book = h.Load();
                var remote = RefreshEntityCopy.CloneBook(book, includeEditions: false);
                remote.Author = null;
                remote.Editions = new List<Edition>
                {
                    new() { HardcoverEditionId = first.HardcoverEditionId, GoodreadsEditionId = second.GoodreadsEditionId,
                        Title = "Conflicting incoming recording", Overview = "Conflicting incoming overview", ReadingFormatId = 2, Language = "eng" }
                };
                h.RefreshBooks.RefreshBookInfo(h.Load(), new List<Book> { remote }, new Author(), false);
                var refreshed = h.Load();
                TestContext.WriteLine($"{(postgres ? "PostgreSQL" : "SQLite")}: ambiguous remote stable IDs, local rows [{string.Join(", ", refreshed.Editions.Select(edition => $"{edition.Id}:{edition.HardcoverEditionId}:{edition.GoodreadsEditionId}:{edition.Title}"))}], files {firstFile.Id} -> {h.Files.Get(firstFile.Id).EditionId}, {secondFile.Id} -> {h.Files.Get(secondFile.Id).EditionId}");
                Assert.Multiple(() =>
                {
                    Assert.That(memory.Logs.Any(message => message.Contains("stable edition identities", StringComparison.OrdinalIgnoreCase)
                        && message.Contains("multiple local editions", StringComparison.OrdinalIgnoreCase)), Is.True, string.Join("\n", memory.Logs));
                    foreach (var expected in new[] { (Edition: first, File: firstFile), (Edition: second, File: secondFile) })
                    {
                        var stored = refreshed.Editions.SingleOrDefault(edition => edition.Id == expected.Edition.Id);
                        Assert.That(stored, Is.Not.Null);
                        Assert.That(h.Files.Get(expected.File.Id).EditionId, Is.EqualTo(expected.Edition.Id));
                        Assert.That(h.Files.Get(expected.File.Id).Path, Is.EqualTo(expected.File.Path));
                        if (stored == null) continue;
                        Assert.That(stored.BookId, Is.EqualTo(book.Id));
                        Assert.That(stored.ForeignEditionId, Is.Null);
                        Assert.That(stored.HardcoverEditionId, Is.EqualTo(expected.Edition.HardcoverEditionId));
                        Assert.That(stored.GoodreadsEditionId, Is.EqualTo(expected.Edition.GoodreadsEditionId));
                        Assert.That(stored.Title, Is.EqualTo(expected.Edition.Title));
                        Assert.That(stored.Overview, Is.EqualTo(expected.Edition.Overview));
                    }
                });
            });
        }

        [Test]
        public void blank_provider_ids_do_not_replace_file_attached_local_metadata(
            [Values(false, true)] bool postgres, [Values(null, "", " ")] string foreignId)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var local = h.Seed(foreignId);
                local.Title = "Saved recording";
                local.Overview = "Saved recording overview";
                h.Editions.UpdateMany(new List<Edition> { local });
                var file = h.AddFile(local);
                var book = h.Load();
                var remote = RefreshEntityCopy.CloneBook(book, includeEditions: false);
                remote.Author = null;
                remote.Editions = new List<Edition>
                {
                    new() { ForeignEditionId = foreignId, Title = "Unidentified incoming recording",
                        Overview = "Unidentified incoming overview", ReadingFormatId = 2, Language = "eng" }
                };
                h.RefreshBooks.RefreshBookInfo(h.Load(), new List<Book> { remote }, new Author(), false);
                var retained = h.Load().Editions.SingleOrDefault(edition => edition.Id == local.Id);
                TestContext.WriteLine($"{(postgres ? "PostgreSQL" : "SQLite")}: blank foreign ID '{foreignId ?? "<null>"}', retained local edition {retained?.Id}, title {retained?.Title}, file {file.Id} -> local edition {h.Files.Get(file.Id).EditionId}");
                Assert.Multiple(() =>
                {
                    Assert.That(retained, Is.Not.Null);
                    Assert.That(h.Files.Get(file.Id).EditionId, Is.EqualTo(local.Id));
                    Assert.That(h.Files.Get(file.Id).Path, Is.EqualTo(file.Path));
                    if (retained == null) return;
                    Assert.That(retained.BookId, Is.EqualTo(book.Id));
                    Assert.That(retained.ForeignEditionId, Is.EqualTo(foreignId));
                    Assert.That(retained.HardcoverEditionId, Is.Null);
                    Assert.That(retained.Title, Is.EqualTo(local.Title));
                    Assert.That(retained.Overview, Is.EqualTo(local.Overview));
                });
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void hardcover_only_refresh_must_keep_each_local_edition_and_file_with_its_own_metadata(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var first = h.Seed(null);
                var second = h.Seed(null);
                first.Monitored = false;
                first.HardcoverEditionId = "hc:edition:202";
                first.Title = "First recording";
                first.Asin = "B000SHARED";
                first.Overview = "First original overview";
                first.Narrator = "First Narrator";
                second.HardcoverEditionId = "hc:edition:203";
                second.Title = "Second recording";
                second.Asin = first.Asin;
                second.Overview = "Second original overview";
                second.Narrator = "Second Narrator";
                h.Editions.UpdateMany(new List<Edition> { first, second });
                var firstFile = h.AddFile(first, @"C:\Books\First recording.m4b".AsOsAgnostic());
                var secondFile = h.AddFile(second, @"C:\Books\Second recording.m4b".AsOsAgnostic());
                var book = h.Load();

                for (var repeat = 0; repeat < 2; repeat++)
                {
                    var remote = RefreshEntityCopy.CloneBook(book, includeEditions: false);
                    remote.Author = null;
                    remote.Editions = new List<Edition>
                    {
                        new() { HardcoverEditionId = first.HardcoverEditionId, Asin = first.Asin, Title = $"First recording refreshed {repeat}",
                            Overview = $"First refreshed overview {repeat}", ReadingFormatId = 2, Language = "eng", Narrator = first.Narrator },
                        new() { HardcoverEditionId = second.HardcoverEditionId, Asin = second.Asin, Title = $"Second recording refreshed {repeat}",
                            Overview = $"Second refreshed overview {repeat}", ReadingFormatId = 2, Language = "eng", Narrator = second.Narrator }
                    };
                    if (repeat == 0) remote.Editions.Reverse();
                    h.RefreshBooks.RefreshBookInfo(h.Load(), new List<Book> { remote }, new Author(), false);
                    var refreshed = h.Load();
                    TestContext.WriteLine($"{(postgres ? "PostgreSQL" : "SQLite")}: refresh {repeat}, remote order [{string.Join(", ", remote.Editions.Select(edition => edition.HardcoverEditionId))}], local rows [{string.Join(", ", refreshed.Editions.Select(edition => $"{edition.Id}:{edition.HardcoverEditionId}:{edition.Title}"))}]");
                    TestContext.WriteLine($"Files: {firstFile.Id} -> local edition {h.Files.Get(firstFile.Id).EditionId}; {secondFile.Id} -> local edition {h.Files.Get(secondFile.Id).EditionId}");
                    Assert.Multiple(() =>
                    {
                        Assert.That(refreshed.Id, Is.EqualTo(book.Id));
                        Assert.That(refreshed.AuthorId, Is.EqualTo(book.AuthorId));
                        Assert.That(refreshed.Editions.Select(edition => edition.Id), Is.EquivalentTo(new[] { first.Id, second.Id }));
                        foreach (var expected in new[] { (Edition: first, File: firstFile, Label: "First"), (Edition: second, File: secondFile, Label: "Second") })
                        {
                            var stored = refreshed.Editions.SingleOrDefault(edition => edition.Id == expected.Edition.Id);
                            Assert.That(stored, Is.Not.Null, "A file-attached Hardcover edition must retain its local row.");
                            Assert.That(h.Files.Get(expected.File.Id).EditionId, Is.EqualTo(expected.Edition.Id));
                            Assert.That(h.Files.Get(expected.File.Id).Path, Is.EqualTo(expected.File.Path));
                            if (stored == null) continue;
                            Assert.That(stored.BookId, Is.EqualTo(book.Id));
                            Assert.That(stored.HardcoverEditionId, Is.EqualTo(expected.Edition.HardcoverEditionId));
                            Assert.That(stored.ForeignEditionId, Is.Null);
                            Assert.That(stored.Title, Is.EqualTo($"{expected.Label} recording refreshed {repeat}"));
                            Assert.That(stored.Overview, Is.EqualTo($"{expected.Label} refreshed overview {repeat}"));
                            Assert.That(stored.Narrator, Is.EqualTo(expected.Edition.Narrator));
                        }
                    });
                }
            });
        }
    }
}
