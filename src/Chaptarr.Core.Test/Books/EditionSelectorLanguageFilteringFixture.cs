using System.Collections.Generic;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class EditionSelectorLanguageFilteringFixture
    {
        private static EditionSelector CreateSut()
        {
            return new EditionSelector(LogManager.GetCurrentClassLogger());
        }

        [Test]
        public void should_prefer_manual_pin_before_ratings()
        {
            var sut = CreateSut();

            var selected = sut.SelectBestEdition(
                new List<Edition>
                {
                    new Edition { Id = 1, Title = "Manual", Language = "eng", ReadingFormatId = 3, ManualAdd = true },
                    new Edition { Id = 2, Title = "Popular", Language = "eng", ReadingFormatId = 3, Ratings = new Ratings { Votes = 5000, Value = 4.8m } }
                },
                BookMediaType.Ebook);

            Assert.That(selected?.Id, Is.EqualTo(1));
        }

        [Test]
        public void should_select_single_upstream_filtered_candidate()
        {
            var sut = CreateSut();

            var selected = sut.SelectBestEdition(
                new List<Edition>
                {
                    new Edition { Id = 1, Title = "Dune English", Language = "eng", ReadingFormatId = 3, Ratings = new Ratings { Votes = 10, Value = 4.0m } }
                },
                BookMediaType.Ebook);

            Assert.That(selected?.Id, Is.EqualTo(1));
        }

        [Test]
        public void should_prefer_highest_rated_audiobook_after_native_format_filtering()
        {
            var sut = CreateSut();

            var selected = sut.SelectBestEdition(
                new List<Edition>
                {
                    new Edition
                    {
                        Id = 1,
                        Title = "Dune Rich Metadata",
                        Language = "eng",
                        ReadingFormatId = 2,
                        Overview = "Rich description",
                        Narrator = "Narrator One",
                        DurationSeconds = 36000,
                        Ratings = new Ratings { Votes = 10, Value = 4.9m }
                    },
                    new Edition
                    {
                        Id = 2,
                        Title = "Dune Popular Audio",
                        Language = "eng",
                        ReadingFormatId = 2,
                        Ratings = new Ratings { Votes = 5000, Value = 4.2m }
                    },
                    new Edition
                    {
                        Id = 3,
                        Title = "Dune Ebook",
                        Language = "eng",
                        ReadingFormatId = 3,
                        Ratings = new Ratings { Votes = 100000, Value = 4.8m }
                    }
                },
                BookMediaType.Audiobook);

            Assert.That(selected?.Id, Is.EqualTo(2));
        }

        [Test]
        public void should_prefer_highest_rated_ebook_after_native_format_filtering()
        {
            var sut = CreateSut();

            var selected = sut.SelectBestEdition(
                new List<Edition>
                {
                    new Edition
                    {
                        Id = 1,
                        Title = "Dune Popular Ebook",
                        Language = "eng",
                        ReadingFormatId = 3,
                        Ratings = new Ratings { Votes = 1200, Value = 4.1m }
                    },
                    new Edition
                    {
                        Id = 2,
                        Title = "Dune Rich Print",
                        Language = "eng",
                        ReadingFormatId = 1,
                        Ratings = new Ratings { Votes = 9000, Value = 4.8m }
                    },
                    new Edition
                    {
                        Id = 3,
                        Title = "Dune Better Ebook",
                        Language = "eng",
                        ReadingFormatId = 3,
                        Ratings = new Ratings { Votes = 5400, Value = 4.4m }
                    }
                },
                BookMediaType.Ebook);

            Assert.That(selected?.Id, Is.EqualTo(3));
        }
    }
}
