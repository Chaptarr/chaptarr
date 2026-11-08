using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Books;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class EditionPinPolicyFixture
    {
        [Test]
        public void any_edition_ok_false_protects_the_monitored_edition_without_manual_add()
        {
            var book = new Book { Id = 1, AnyEditionOk = false };
            var pinned = new Edition { Id = 10, BookId = book.Id, Monitored = true, ManualAdd = false };
            var matched = new Edition { Id = 11, BookId = book.Id, Monitored = false, ManualAdd = false };

            var conflict = EditionPinPolicy.FindConflictingProtectedEdition(book, new List<Edition> { pinned, matched }, matched.Id);

            Assert.That(conflict?.Id, Is.EqualTo(pinned.Id));
            Assert.That(EditionPinPolicy.FindConflictingProtectedEdition(book, new List<Edition> { pinned, matched }, pinned.Id), Is.Null);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void manual_add_remains_protected_when_any_edition_is_otherwise_allowed(bool selected)
        {
            var book = new Book { Id = 1, AnyEditionOk = true };
            var preserved = new Edition { Id = 10, BookId = book.Id, Monitored = selected, ManualAdd = true };
            var matched = new Edition { Id = 11, BookId = book.Id };

            var conflict = EditionPinPolicy.FindConflictingProtectedEdition(book, new List<Edition> { preserved, matched }, matched.Id);

            Assert.That(conflict?.Id, Is.EqualTo(preserved.Id));
        }

        [Test]
        public void unpinned_book_allows_automatic_edition_switching()
        {
            var book = new Book { Id = 1, AnyEditionOk = true };
            var current = new Edition { Id = 10, BookId = book.Id, Monitored = true };
            var matched = new Edition { Id = 11, BookId = book.Id };

            Assert.That(EditionPinPolicy.FindConflictingProtectedEdition(book, new List<Edition> { current, matched }, matched.Id), Is.Null);
        }

        [Test]
        public void automation_can_select_only_when_no_user_pin_exists()
        {
            var automatic = new Book { Id = 1, AnyEditionOk = true };
            var automaticEdition = new Edition { Id = 10, BookId = automatic.Id, Monitored = true };
            var guiPinned = new Book { Id = 2, AnyEditionOk = false };
            var guiPinnedEdition = new Edition { Id = 20, BookId = guiPinned.Id, Monitored = true };
            var manuallyPreserved = new Book { Id = 3, AnyEditionOk = true };
            var manuallyPreservedEdition = new Edition { Id = 30, BookId = manuallyPreserved.Id, Monitored = true, ManualAdd = true };

            Assert.Multiple(() =>
            {
                Assert.That(EditionPinPolicy.CanAutomationSelectEdition(automatic, new[] { automaticEdition }), Is.True);
                Assert.That(EditionPinPolicy.CanAutomationSelectEdition(guiPinned, new[] { guiPinnedEdition }), Is.False);
                Assert.That(EditionPinPolicy.CanAutomationSelectEdition(manuallyPreserved, new[] { manuallyPreservedEdition }), Is.False);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void explicit_selection_releases_only_the_displaced_choice(bool manual)
        {
            var previous = new Edition { Id = 10, Monitored = true, ManualAdd = true };
            var selected = new Edition { Id = 11 };
            var retained = new Edition { Id = 12, ManualAdd = true };
            var editions = new List<Edition> { previous, selected, retained };
            EditionPinPolicy.ApplyExplicitSelection(editions, selected.Id, new HashSet<int> { previous.Id }, manual);
            Assert.Multiple(() =>
            {
                Assert.That(previous.Monitored, Is.False);
                Assert.That(previous.ManualAdd, Is.False);
                Assert.That(selected.Monitored, Is.True);
                Assert.That(selected.ManualAdd, Is.EqualTo(manual));
                Assert.That(retained.Monitored, Is.False);
                Assert.That(retained.ManualAdd, Is.True);
            });
        }

        [Test]
        public void automatic_selection_never_manufactures_a_preservation_pin()
        {
            var book = new Book { Id = 1, AnyEditionOk = false };
            var selected = new Edition { Id = 10, BookId = book.Id, Monitored = true, ManualAdd = true };
            var sibling = new Edition { Id = 11, BookId = book.Id, ManualAdd = true };

            EditionPinPolicy.MarkSelectionAsAutomatic(book, new[] { selected, sibling });

            Assert.Multiple(() =>
            {
                Assert.That(book.AnyEditionOk, Is.True);
                Assert.That(selected.ManualAdd, Is.False);
                Assert.That(sibling.ManualAdd, Is.False);
                Assert.That(selected.Monitored, Is.True, "the helper changes pin ownership, not the selected Edition");
            });
        }
    }
}
