using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.MediaFiles;

namespace Chaptarr.Core.Test.Books
{
    public partial class BookServiceWantedNarratorSeriesLinksFixture
    {
        private static Book SeedEbookWanted(BookRepository books, EditionRepository editions, AuthorRepository authors, bool pinned)
        {
            var author = authors.All().Single();
            author.EbookRootFolderPath = @"C:\Ebooks".AsOsAgnostic();
            author.EbookQualityProfileId = 1; author.EbookMetadataProfileId = 1;
            authors.Update(author);
            var book = books.Insert(new Book
            {
                AuthorId = author.Id, Title = "Pin Diagnostic Work", CleanTitle = "pindiagnosticwork", TitleSlug = "pin-diagnostic-work",
                HardcoverBookId = "hc:1001", MediaType = BookMediaType.Ebook, EbookMonitored = true, AnyEditionOk = !pinned, Added = DateTime.UtcNow
            });
            book.Editions = new[] { 201, 202 }.Select(id => editions.Insert(new Edition
            {
                BookId = book.Id, Title = "Pin Diagnostic Edition " + id, ForeignEditionId = "hc:edition:" + id,
                HardcoverEditionId = "hc:edition:" + id, ReadingFormatId = id == 201 ? 3 : 1, Language = "eng",
                Narrator = "Shared catalog credit", NarratorNames = new List<string> { "Shared catalog credit" },
                Monitored = id == 201, ManualAdd = pinned && id == 201
            })).ToList();
            return book;
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ebook_writer_uses_later_satisfying_pin_when_first_exact_copy_has_conflicting_protection(bool postgres)
        {
            WithWantedWriter(postgres, (writer, books, editions, authors, _) =>
            {
                var first = SeedEbookWanted(books, editions, authors, true);
                first.AnyEditionOk = false; books.Update(first);
                first.Editions[0].Monitored = false;
                first.Editions[1].Monitored = true; first.Editions[1].ManualAdd = true;
                editions.UpdateMany(first.Editions);
                var second = books.Insert(new Book
                {
                    AuthorId = first.AuthorId, Title = first.Title, CleanTitle = first.CleanTitle, TitleSlug = "valid-pin-copy",
                    HardcoverBookId = first.HardcoverBookId, MediaType = BookMediaType.Ebook, EbookMonitored = true,
                    AnyEditionOk = false, Added = DateTime.UtcNow
                });
                var existingPin = editions.Insert(new Edition
                {
                    BookId = second.Id, Title = "Requested edition", ForeignEditionId = "hc:edition:202", HardcoverEditionId = "hc:edition:202",
                    ReadingFormatId = 3, Language = "eng", Monitored = true, ManualAdd = true
                });
                var request = writer.GetBook(first.Id).Editions.Single(edition => edition.Id == first.Editions[1].Id);
                var failures = new List<Exception>();
                var result = writer.AddWantedEditions(new List<Edition> { request }, (_, error) => failures.Add(error));
                Assert.Multiple(() =>
                {
                    Assert.That(failures, Is.Empty);
                    Assert.That(result[request.Id].Id, Is.EqualTo(second.Id));
                    Assert.That(books.All(), Has.Count.EqualTo(2));
                    Assert.That(BookEditionIdentity.GetMonitoredEdition(writer.GetBook(second.Id)).Id, Is.EqualTo(existingPin.Id));
                    Assert.That(writer.GetBook(first.Id).Editions.Count(edition => edition.ManualAdd), Is.EqualTo(2));
                });
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ebook_writer_selects_exact_identity_despite_equal_catalog_cast_and_keeps_repeat_pin(bool postgres)
        {
            WithWantedWriter(postgres, (writer, books, editions, authors, _) =>
            {
                var source = SeedEbookWanted(books, editions, authors, false);
                var requested = writer.GetBook(source.Id).Editions.Single(edition => edition.Id == source.Editions[1].Id);
                for (var pass = 0; pass < 3; pass++)
                {
                    var failures = new List<Exception>();
                    var result = writer.AddWantedEditions(new List<Edition> { requested }, (_, error) => failures.Add(error));
                    var stored = writer.GetBook(source.Id);
                    Assert.Multiple(() =>
                    {
                        Assert.That(failures, Is.Empty);
                        Assert.That(result[requested.Id].Id, Is.EqualTo(source.Id));
                        Assert.That(books.All(), Has.Count.EqualTo(1));
                        Assert.That(stored.MediaType, Is.EqualTo(BookMediaType.Ebook));
                        Assert.That(stored.AnyEditionOk, Is.False);
                        Assert.That(BookEditionIdentity.GetMonitoredEdition(stored).Id, Is.EqualTo(requested.Id));
                        Assert.That(BookEditionIdentity.GetMonitoredEdition(stored).ManualAdd, Is.True);
                        Assert.That(stored.Editions.Count(edition => edition.Monitored), Is.EqualTo(1));
                    });
                }
            });
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void ebook_writer_refuses_conflicting_pin_or_files_without_copy_or_overwrite(bool postgres, bool filesPresent)
        {
            WithWantedWriter(postgres, (writer, books, editions, authors, files) =>
            {
                var source = SeedEbookWanted(books, editions, authors, !filesPresent);
                var before = writer.GetBook(source.Id);
                var selected = BookEditionIdentity.GetMonitoredEdition(before);
                var requested = before.Editions.Single(edition => edition.Id == source.Editions[1].Id);
                if (filesPresent) files.FilesByBookId[source.Id] = new List<BookFile>
                {
                    new() { Id = 70, EditionId = selected.Id, Edition = selected, Path = @"C:\Ebooks\Pin Diagnostic Work\book.epub".AsOsAgnostic(), MediaType = "ebook" }
                };
                var failures = new List<(int EditionId, Exception Error)>();
                var result = writer.AddWantedEditions(new List<Edition> { requested }, (edition, error) => failures.Add((edition.Id, error)));
                var stored = writer.GetBook(source.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(result, Is.Empty);
                    Assert.That(failures.Select(failure => failure.EditionId), Is.EqualTo(new[] { requested.Id }));
                    Assert.That(books.All(), Has.Count.EqualTo(1));
                    Assert.That(editions.All(), Has.Count.EqualTo(2));
                    Assert.That(BookEditionIdentity.GetMonitoredEdition(stored).Id, Is.EqualTo(selected.Id));
                    Assert.That(stored.AnyEditionOk, Is.EqualTo(before.AnyEditionOk));
                    Assert.That(stored.Editions.Single(edition => edition.Id == selected.Id).ManualAdd, Is.EqualTo(selected.ManualAdd));
                    Assert.That(files.FilesByBookId.Values.SelectMany(list => list).Select(file => (file.Id, file.EditionId)),
                        Is.EqualTo(filesPresent ? new[] { (70, selected.Id) } : Array.Empty<(int, int)>()));
                });
                TestContext.WriteLine($"{(postgres ? "PostgreSQL" : "SQLite")}: Ebook request {requested.ForeignEditionId} refused ({(filesPresent ? "file" : "pin")}); original local book {source.Id}/edition {selected.Id} retained, no copy.");
            });
        }
    }
}
