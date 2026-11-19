using System.Linq;
using Chaptarr.Api.V1.Books;
using NUnit.Framework;
using NzbDrone.Core.Books;

namespace Chaptarr.Core.Test.Api
{
    public partial class BookControllerDisplayMetadataUpdateFixture
    {
        [Test]
        public void microphone_adds_a_separate_exact_edition_without_changing_the_original(
            [Values(false, true)] bool postgres, [Values(false, true)] bool hasFiles,
            [Values(false, true)] bool pinned, [Values(false, true)] bool legacyIdentity)
        {
            WithEndpoint(postgres, false, !hasFiles, (_, books, editions, book, _) =>
            {
                book.AnyEditionOk = !pinned;
                books.Update(book);
                var original = book.Editions.Single(edition => edition.Monitored);
                original.ManualAdd = pinned;
                editions.Update(original);
                if (legacyIdentity)
                {
                    var requested = book.Editions.Single(edition => !edition.Monitored);
                    requested.ForeignEditionId = "gr:789-audiobook";
                    requested.HardcoverEditionId = null;
                    editions.Update(requested);
                }
            }, fileTest: (controller, writer, files, book) =>
            {
                var originalSelection = book.Editions.OrderBy(edition => edition.Id).Select(edition => (edition.Id, edition.Monitored, edition.ManualAdd)).ToArray();
                var originalFiles = files.GetFilesByBook(book.Id)
                    .Select(file => (file.Id, file.EditionId, file.Path, file.AudioProductionOverride)).ToArray();
                var requested = book.Editions.Single(edition => !edition.Monitored);
                var request = new AddWantedEditionRequest { EditionId = requested.Id };
                var result = controller.AddWantedEdition(book.Id, request);
                Assert.That(result.Value, Is.Not.Null, result.Result?.ToString());
                var copy = writer.GetBook(result.Value.Id);
                var originalAfter = writer.GetBook(book.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(copy.Id, Is.Not.EqualTo(book.Id));
                    Assert.That(copy.AnyEditionOk, Is.False);
                    Assert.That(copy.IsMonitored(), Is.True);
                    Assert.That(BookEditionIdentity.EditionsMatch(copy.Editions.Single(edition => edition.Monitored), requested), Is.True);
                    Assert.That(copy.Editions.Single(edition => edition.Monitored).ManualAdd, Is.True);
                    Assert.That(originalAfter.AnyEditionOk, Is.EqualTo(book.AnyEditionOk));
                    Assert.That(originalAfter.IsMonitored(), Is.EqualTo(book.IsMonitored()));
                    Assert.That(originalAfter.Editions.OrderBy(edition => edition.Id).Select(edition => (edition.Id, edition.Monitored, edition.ManualAdd)), Is.EqualTo(originalSelection));
                    Assert.That(files.GetFilesByBook(book.Id).Select(file => (file.Id, file.EditionId, file.Path, file.AudioProductionOverride)), Is.EqualTo(originalFiles));
                    Assert.That(files.GetFilesByBook(copy.Id), Is.Empty);
                });

                Assert.That(controller.AddWantedEdition(book.Id, request).Value.Id, Is.EqualTo(copy.Id));
                Assert.That(writer.GetBooksByAuthor(book.AuthorId), Has.Count.EqualTo(2));
            });
        }

        [Test]
        public void microphone_automatic_original_showing_requested_edition_returns_unchanged(
            [Values(false, true)] bool postgres, [Values(false, true)] bool hasFiles)
        {
            WithEndpoint(postgres, false, !hasFiles, null, fileTest: (controller, writer, files, book) =>
            {
                Assert.That(book.AnyEditionOk, Is.True);
                Assert.That(book.IsMonitored(), Is.False);
                var selected = book.Editions.Single(edition => edition.Monitored);
                var originalFiles = files.GetFilesByBook(book.Id)
                    .Select(file => (file.Id, file.EditionId, file.AudioProductionOverride)).ToArray();
                var result = controller.AddWantedEdition(book.Id, new AddWantedEditionRequest { EditionId = selected.Id });
                Assert.That(result.Value, Is.Not.Null, result.Result?.ToString());
                var after = writer.GetBook(book.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(result.Value.Id, Is.EqualTo(book.Id));
                    Assert.That(result.Value.ExistingCopy, Is.True);
                    Assert.That(writer.GetBooksByAuthor(book.AuthorId), Has.Count.EqualTo(1));
                    Assert.That(after.AnyEditionOk, Is.True);
                    Assert.That(after.IsMonitored(), Is.False);
                    Assert.That(after.Editions.Single(edition => edition.Monitored).ManualAdd, Is.False);
                    Assert.That(files.GetFilesByBook(book.Id).Select(file => (file.Id, file.EditionId, file.AudioProductionOverride)), Is.EqualTo(originalFiles));
                });
            });
        }

        [Test]
        public void microphone_targets_the_picked_edition_even_when_another_has_the_same_narrator([Values(false, true)] bool postgres)
        {
            WithEndpoint(postgres, false, true, (controller, books, editions, book, _) =>
            {
                var original = book.Editions.Single(edition => edition.Monitored);
                var requested = book.Editions.Single(edition => !edition.Monitored);
                requested.Narrator = original.Narrator;
                editions.Update(requested);
                var result = controller.AddWantedEdition(book.Id, new AddWantedEditionRequest { EditionId = requested.Id });
                Assert.That(result.Value, Is.Not.Null, result.Result?.ToString());
                Assert.That(result.Value.Id, Is.Not.EqualTo(book.Id));
                Assert.That(editions.FindByBook(new[] { result.Value.Id }).Single(edition => edition.Monitored).ForeignEditionId,
                    Is.EqualTo(requested.ForeignEditionId));
                Assert.That(books.Get(book.Id).AnyEditionOk, Is.True);
                Assert.That(editions.FindByBook(new[] { book.Id }).Single(edition => edition.Monitored).Id, Is.EqualTo(original.Id));
            });
        }

        [Test]
        public void editing_a_fileless_books_edition_changes_only_that_row([Values(false, true)] bool postgres)
        {
            WithEndpoint(postgres, false, true, (controller, books, editions, book, _) =>
            {
                var request = controller.GetBookById(book.Id).Value;
                var targetId = book.Editions.Single(edition => !edition.Monitored).Id;
                request.AnyEditionOk = false;
                request.Editions = book.Editions.ToResource();
                controller.UpdateBook(request);
                request.Editions.ForEach(edition => edition.Monitored = edition.Id == targetId);
                controller.UpdateBook(request);
                Assert.That(books.GetBooksByAuthorId(book.AuthorId).Select(row => row.Id), Is.EqualTo(new[] { book.Id }));
                Assert.That(editions.FindByBook(new[] { book.Id }).Single(edition => edition.Monitored).Id, Is.EqualTo(targetId));
                Assert.That(books.Get(book.Id).AnyEditionOk, Is.False);
            });
        }
    }
}
