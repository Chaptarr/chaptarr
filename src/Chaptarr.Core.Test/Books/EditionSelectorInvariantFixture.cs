using System.Collections.Generic;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class EditionSelectorInvariantFixture
    {
        private static EditionSelector CreateSut()
        {
            return new EditionSelector(LogManager.GetCurrentClassLogger());
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_select_only_one_unsaved_edition(bool existingEdition)
        {
            var selected = new Edition { ReadingFormatId = 2, Ratings = new Ratings { Votes = 100 } };
            var editions = new List<Edition>
            {
                new Edition { Id = existingEdition ? 12 : 0, ReadingFormatId = 2, Ratings = new Ratings { Votes = 10 } },
                selected,
                new Edition { ReadingFormatId = 3, Ratings = new Ratings { Votes = 1000 } }
            };

            var sut = CreateSut();
            Assert.That(sut.EnsureSingleMonitoredEdition(editions, mediaType: BookMediaType.Audiobook), Is.True);
            Assert.That(editions.Count(e => e.Monitored), Is.EqualTo(1));
            Assert.That(editions.Single(e => e.Monitored), Is.SameAs(selected));
            Assert.That(sut.EnsureSingleMonitoredEdition(editions, mediaType: BookMediaType.Audiobook), Is.False);
        }

        [Test]
        public void should_keep_single_monitored_even_when_manual_add_exists()
        {
            var sut = CreateSut();

            var editions = new List<Edition>
            {
                new Edition { Id = 1, Monitored = false, ManualAdd = false },
                new Edition { Id = 2, Monitored = false, ManualAdd = true },
                new Edition { Id = 3, Monitored = true, ManualAdd = false }
            };

            var fileCounts = new Dictionary<int, int>
            {
                { 1, 0 },
                { 2, 0 },
                { 3, 10 }
            };

            sut.EnsureSingleMonitoredEdition(editions, fileCounts);

            Assert.That(editions.Single(e => e.Monitored).Id, Is.EqualTo(3));
        }

        [Test]
        public void should_pick_monitored_with_files_over_more_files_unmonitored()
        {
            var sut = CreateSut();

            var editions = new List<Edition>
            {
                new Edition { Id = 1, Monitored = false, ManualAdd = false },
                new Edition { Id = 2, Monitored = true, ManualAdd = false },
                new Edition { Id = 3, Monitored = false, ManualAdd = false }
            };

            var fileCounts = new Dictionary<int, int>
            {
                { 1, 0 },
                { 2, 1 },
                { 3, 10 }
            };

            sut.EnsureSingleMonitoredEdition(editions, fileCounts);

            Assert.That(editions.Single(e => e.Monitored).Id, Is.EqualTo(2));
        }

        [Test]
        public void should_use_normal_ranking_within_file_bearing_editions()
        {
            var sut = CreateSut();

            var editions = new List<Edition>
            {
                new Edition { Id = 10, Monitored = false, ManualAdd = false },
                new Edition { Id = 11, Monitored = false, ManualAdd = false },
                new Edition { Id = 12, Monitored = false, ManualAdd = false, Ratings = new Ratings { Votes = 100 } }
            };

            var fileCounts = new Dictionary<int, int>
            {
                { 10, 0 },
                { 11, 20 },
                { 12, 1 }
            };

            sut.EnsureSingleMonitoredEdition(editions, fileCounts);

            Assert.That(editions.Single(e => e.Monitored).Id, Is.EqualTo(12));
        }

        [Test]
        public void should_preserve_manual_intent_on_an_unsaved_edition()
        {
            var editions = new List<Edition>
            {
                new Edition { ReadingFormatId = 2, Ratings = new Ratings { Votes = 1000 } },
                new Edition { ReadingFormatId = 2, ManualAdd = true, Ratings = new Ratings { Votes = 1 } }
            };
            CreateSut().EnsureSingleMonitoredEdition(editions, mediaType: BookMediaType.Audiobook);
            Assert.That(editions.Single(edition => edition.Monitored), Is.SameAs(editions[1]));
        }

        [Test]
        public void should_keep_existing_monitored_when_no_files()
        {
            var sut = CreateSut();

            var editions = new List<Edition>
            {
                new Edition { Id = 5, Monitored = true, ManualAdd = false },
                new Edition { Id = 6, Monitored = false, ManualAdd = false }
            };

            sut.EnsureSingleMonitoredEdition(editions, fileCountsByEditionId: null);

            Assert.That(editions.Single(e => e.Monitored).Id, Is.EqualTo(5));
        }

        [Test]
        public void should_fall_back_to_lowest_id_when_no_manual_no_files_no_monitored()
        {
            var sut = CreateSut();

            var editions = new List<Edition>
            {
                new Edition { Id = 20, Monitored = false, ManualAdd = false },
                new Edition { Id = 7, Monitored = false, ManualAdd = false }
            };

            sut.EnsureSingleMonitoredEdition(editions, fileCountsByEditionId: null);

            Assert.That(editions.Single(e => e.Monitored).Id, Is.EqualTo(7));
        }

        [Test]
        public void should_use_native_format_then_ratings_when_repairing_broken_state_with_media_type()
        {
            var sut = CreateSut();

            var editions = new List<Edition>
            {
                new Edition { Id = 7, Monitored = false, ManualAdd = false, ReadingFormatId = 1, Ratings = new Ratings { Votes = 500, Value = 4.8m } },
                new Edition { Id = 20, Monitored = false, ManualAdd = false, ReadingFormatId = 3, Ratings = new Ratings { Votes = 100, Value = 4.0m } },
                new Edition { Id = 30, Monitored = false, ManualAdd = false, ReadingFormatId = 3, Ratings = new Ratings { Votes = 250, Value = 4.5m } }
            };

            sut.EnsureSingleMonitoredEdition(editions, fileCountsByEditionId: null, mediaType: BookMediaType.Ebook);

            Assert.That(editions.Single(e => e.Monitored).Id, Is.EqualTo(30));
        }

        [Test]
        public void should_keep_single_monitored_even_when_non_native_format_exists()
        {
            var sut = CreateSut();

            var editions = new List<Edition>
            {
                new Edition { Id = 1, Monitored = true, ManualAdd = false, ReadingFormatId = 3, Ratings = new Ratings { Votes = 1000, Value = 4.9m } },
                new Edition { Id = 2, Monitored = false, ManualAdd = false, ReadingFormatId = 2, Ratings = new Ratings { Votes = 10, Value = 4.0m } }
            };

            sut.EnsureSingleMonitoredEdition(editions, fileCountsByEditionId: null, mediaType: BookMediaType.Audiobook);

            Assert.That(editions.Single(e => e.Monitored).Id, Is.EqualTo(1));
        }

        [Test]
        public void should_keep_single_monitored_even_when_higher_ranked_fallback_exists()
        {
            var sut = CreateSut();

            var editions = new List<Edition>
            {
                new Edition { Id = 1, Monitored = true, ManualAdd = false, ReadingFormatId = 1, Ratings = new Ratings { Votes = 1000, Value = 4.9m } },
                new Edition { Id = 2, Monitored = false, ManualAdd = false, ReadingFormatId = 3, Ratings = new Ratings { Votes = 10, Value = 4.0m } }
            };

            sut.EnsureSingleMonitoredEdition(editions, fileCountsByEditionId: null, mediaType: BookMediaType.Audiobook);

            Assert.That(editions.Single(e => e.Monitored).Id, Is.EqualTo(1));
        }

        [Test]
        public void should_keep_existing_monitored_representative_when_it_is_best_available_format()
        {
            var sut = CreateSut();

            var editions = new List<Edition>
            {
                new Edition { Id = 1, Monitored = true, ManualAdd = false, ReadingFormatId = 3, Ratings = new Ratings { Votes = 10, Value = 4.0m } },
                new Edition { Id = 2, Monitored = false, ManualAdd = false, ReadingFormatId = 1, Ratings = new Ratings { Votes = 1000, Value = 4.9m } }
            };

            sut.EnsureSingleMonitoredEdition(editions, fileCountsByEditionId: null, mediaType: BookMediaType.Audiobook);

            Assert.That(editions.Single(e => e.Monitored).Id, Is.EqualTo(1));
        }

        [Test]
        public void should_use_native_format_ranking_to_repair_multiple_monitored()
        {
            var sut = CreateSut();

            var editions = new List<Edition>
            {
                new Edition { Id = 1, Monitored = true, ManualAdd = false, ReadingFormatId = 1, Ratings = new Ratings { Votes = 10, Value = 4.0m } },
                new Edition { Id = 2, Monitored = true, ManualAdd = false, ReadingFormatId = 3, Ratings = new Ratings { Votes = 1000, Value = 4.9m } }
            };

            sut.EnsureSingleMonitoredEdition(editions, fileCountsByEditionId: null, mediaType: BookMediaType.Ebook);

            Assert.That(editions.Single(e => e.Monitored).Id, Is.EqualTo(2));
        }

        [Test]
        public void should_tiebreak_multiple_manual_add_to_manual_and_monitored_first_else_lowest_id()
        {
            var sut = CreateSut();

            var editions = new List<Edition>
            {
                new Edition { Id = 2, Monitored = false, ManualAdd = true },
                new Edition { Id = 1, Monitored = true, ManualAdd = true },
                new Edition { Id = 3, Monitored = false, ManualAdd = false }
            };

            sut.EnsureSingleMonitoredEdition(editions, fileCountsByEditionId: null);

            Assert.That(editions.Single(e => e.Monitored).Id, Is.EqualTo(1));
        }
    }
}
