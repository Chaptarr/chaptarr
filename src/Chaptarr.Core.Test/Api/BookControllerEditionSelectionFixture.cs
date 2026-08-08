using System.Collections.Generic;
using Chaptarr.Api.V1.Books;
using Chaptarr.Http.Middleware;
using NUnit.Framework;
using NzbDrone.Core.Books;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class BookControllerEditionSelectionFixture
    {
        [Test]
        public void should_detect_explicit_edition_change_when_single_submitted_monitored_differs()
        {
            var submitted = new List<Edition>
            {
                new Edition { Id = 1, Monitored = false },
                new Edition { Id = 2, Monitored = true }
            };

            var changed = BookController.IsExplicitEditionSelectionChange(1, submitted);

            Assert.That(changed, Is.True);
        }


        [Test]
        public void should_pin_explicit_edition_change_only_for_id_route()
        {
            var submitted = new List<Edition>
            {
                new Edition { Id = 1, Monitored = false },
                new Edition { Id = 2, Monitored = true }
            };

            Assert.That(BookController.ShouldPinExplicitEditionSelection(true, 1, submitted), Is.True);
            Assert.That(BookController.ShouldPinExplicitEditionSelection(false, 1, submitted), Is.False);
        }

        [Test]
        public void should_skip_repair_for_partial_facade_compat_submission_that_omitted_current_edition()
        {
            var stored = new List<Edition>
            {
                new Edition { Id = 1, Monitored = true },
                new Edition { Id = 2, Monitored = false }
            };
            var submitted = new List<Edition>
            {
                new Edition { Id = 2, Monitored = false }
            };

            var skip = BookController.ShouldSkipEditionRepairForPartialFacadeCompat(
                pinExplicitEditionChange: false,
                facadeContext: new ReadarrFacadeContext("hc", "ebook", "/readarr/hc/ebook"),
                beforeMonitoredEditionId: 1,
                storedEditions: stored,
                submittedEditions: submitted);

            Assert.That(skip, Is.True);
        }

        [Test]
        public void should_not_skip_repair_for_human_route_or_full_facade_submission()
        {
            var stored = new List<Edition>
            {
                new Edition { Id = 1, Monitored = true },
                new Edition { Id = 2, Monitored = false }
            };
            var fullSubmitted = new List<Edition>
            {
                new Edition { Id = 1, Monitored = false },
                new Edition { Id = 2, Monitored = false }
            };
            var partialSubmitted = new List<Edition>
            {
                new Edition { Id = 2, Monitored = false }
            };
            var facadeContext = new ReadarrFacadeContext("hc", "ebook", "/readarr/hc/ebook");

            Assert.Multiple(() =>
            {
                Assert.That(BookController.ShouldSkipEditionRepairForPartialFacadeCompat(true, facadeContext, 1, stored, partialSubmitted), Is.False);
                Assert.That(BookController.ShouldSkipEditionRepairForPartialFacadeCompat(false, facadeContext, 1, stored, fullSubmitted), Is.False);
                Assert.That(BookController.ShouldSkipEditionRepairForPartialFacadeCompat(false, null, 1, stored, partialSubmitted), Is.False);
            });
        }

        [Test]
        public void should_not_detect_explicit_edition_change_when_single_submitted_monitored_is_unchanged()
        {
            var submitted = new List<Edition>
            {
                new Edition { Id = 1, Monitored = true },
                new Edition { Id = 2, Monitored = false }
            };

            var changed = BookController.IsExplicitEditionSelectionChange(1, submitted);

            Assert.That(changed, Is.False);
        }

        [Test]
        public void should_not_detect_explicit_edition_change_for_invalid_monitored_counts()
        {
            var none = new List<Edition>
            {
                new Edition { Id = 1, Monitored = false },
                new Edition { Id = 2, Monitored = false }
            };

            var multiple = new List<Edition>
            {
                new Edition { Id = 1, Monitored = true },
                new Edition { Id = 2, Monitored = true }
            };

            Assert.That(BookController.IsExplicitEditionSelectionChange(1, none), Is.False);
            Assert.That(BookController.IsExplicitEditionSelectionChange(1, multiple), Is.False);
        }

        [TestCase(BookMediaType.Audiobook, "audiobook", true)]
        [TestCase(BookMediaType.Ebook, "ebook", true)]
        [TestCase(BookMediaType.Audiobook, "ebook", false)]
        [TestCase(BookMediaType.Ebook, "audiobook", false)]
        public void should_use_specific_book_monitoring_only_for_matching_facade_media_type(
            BookMediaType bookMediaType,
            string facadeMediaType,
            bool expected)
        {
            var context = new ReadarrFacadeContext("hc", facadeMediaType, $"/{facadeMediaType}");

            Assert.That(
                BookController.ShouldApplyFacadeSpecificBookMonitoring(
                    context,
                    bookMediaType,
                    wasMonitored: false,
                    requestedMonitored: true),
                Is.EqualTo(expected));
        }

        [Test]
        public void should_not_use_specific_book_monitoring_outside_facade_or_without_false_to_true_change()
        {
            Assert.Multiple(() =>
            {
                Assert.That(
                    BookController.ShouldApplyFacadeSpecificBookMonitoring(
                        null,
                        BookMediaType.Audiobook,
                        wasMonitored: false,
                        requestedMonitored: true),
                    Is.False);
                Assert.That(
                    BookController.ShouldApplyFacadeSpecificBookMonitoring(
                        new ReadarrFacadeContext("hc", "audiobook", "/audiobook"),
                        BookMediaType.Audiobook,
                        wasMonitored: true,
                        requestedMonitored: true),
                    Is.False);
                Assert.That(
                    BookController.ShouldApplyFacadeSpecificBookMonitoring(
                        new ReadarrFacadeContext("hc", "audiobook", "/audiobook"),
                        BookMediaType.Audiobook,
                        wasMonitored: false,
                        requestedMonitored: false),
                    Is.False);
            });
        }
    }
}
