using System.Collections.Generic;
using Chaptarr.Api.V1.ManualImport;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.MediaFiles.BookImport.Manual;
using NUnit.Framework;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class ManualImportResourceMapperFixture
    {
        [Test]
        public void should_set_foreign_edition_id_to_best_guess()
        {
            var monitoredEdition = new Edition
            {
                Id = 2,
                Monitored = true,
                ForeignEditionId = "hc:edition:2"
            };

            var otherEdition = new Edition
            {
                Id = 1,
                Monitored = false,
                ForeignEditionId = "hc:edition:1"
            };

            var book = new Book
            {
                Title = "Test",
                Author = new Author(),
                Editions = new List<Edition> { otherEdition, monitoredEdition }
            };

            var item = new ManualImportItem
            {
                Id = 1,
                Book = book,
                Edition = null
            };

            var resource = item.ToResource();

            Assert.Multiple(() =>
            {
                Assert.That(resource.EditionId, Is.EqualTo(2));
                Assert.That(resource.ForeignEditionId, Is.EqualTo("hc:edition:2"));
            });
        }

        [Test]
        public void should_prefer_explicit_edition_over_book_level_guess()
        {
            var monitoredEdition = new Edition
            {
                Id = 2,
                Monitored = true,
                ForeignEditionId = "hc:edition:2"
            };

            var book = new Book
            {
                Title = "Test",
                Author = new Author(),
                Editions = new List<Edition> { monitoredEdition }
            };

            var item = new ManualImportItem
            {
                Id = 1,
                Book = book,
                Edition = new Edition
                {
                    Id = 99,
                    ForeignEditionId = "hc:edition:99"
                }
            };

            var resource = item.ToResource();

            Assert.Multiple(() =>
            {
                Assert.That(resource.EditionId, Is.EqualTo(99));
                Assert.That(resource.ForeignEditionId, Is.EqualTo("hc:edition:99"));
            });
        }

        [Test]
        public void should_map_suggested_book_and_edition_labels()
        {
            var item = new ManualImportItem
            {
                Id = 1,
                SuggestedForeignAuthorId = "hc:123",
                SuggestedAuthorName = "Suggested Author",
                SuggestedForeignBookId = "hc:work-1",
                SuggestedBookTitle = "Suggested Book",
                SuggestedForeignEditionId = "hc:edition-1",
                SuggestedEditionTitle = "Suggested Edition"
            };

            var resource = item.ToResource();

            Assert.Multiple(() =>
            {
                Assert.That(resource.ForeignEditionId, Is.EqualTo("hc:edition-1"));
                Assert.That(resource.SuggestedForeignAuthorId, Is.EqualTo("hc:123"));
                Assert.That(resource.SuggestedAuthorName, Is.EqualTo("Suggested Author"));
                Assert.That(resource.SuggestedForeignBookId, Is.EqualTo("hc:work-1"));
                Assert.That(resource.SuggestedBookTitle, Is.EqualTo("Suggested Book"));
                Assert.That(resource.SuggestedForeignEditionId, Is.EqualTo("hc:edition-1"));
                Assert.That(resource.SuggestedEditionTitle, Is.EqualTo("Suggested Edition"));
            });
        }

        [Test]
        public void should_not_expose_fake_foreign_edition_id_as_provider_identity()
        {
            var item = new ManualImportItem
            {
                Id = 1,
                Book = new Book
                {
                    Title = "Test",
                    Author = new Author(),
                    Editions = new List<Edition>
                    {
                        new Edition
                        {
                            Id = 99,
                            Monitored = true,
                            ForeignEditionId = "0_edition",
                            Isbn13 = "9780000000000"
                        }
                    }
                }
            };

            var resource = item.ToResource();

            Assert.Multiple(() =>
            {
                Assert.That(resource.EditionId, Is.EqualTo(99));
                Assert.That(resource.ForeignEditionId, Is.Null);
            });
        }

        [Test]
        public void should_carry_tags_to_resource_for_file_details()
        {
            var item = new ManualImportItem
            {
                Id = 1,
                Tags = new Dictionary<string, List<string>>
                {
                    ["TITLE"] = new List<string> { "The Test Book" },
                    ["ARTIST"] = new List<string> { "Test Author" }
                }
            };

            var resource = item.ToResource();

            Assert.Multiple(() =>
            {
                Assert.That(resource.Tags, Is.Not.Null);
                Assert.That(resource.Tags["TITLE"], Is.EqualTo(new[] { "The Test Book" }));
                Assert.That(resource.Tags["ARTIST"], Is.EqualTo(new[] { "Test Author" }));
            });
        }

        [Test]
        public void should_expose_calculated_custom_formats_to_the_interactive_import_resource()
        {
            var item = new ManualImportItem
            {
                Id = 1,
                CustomFormats = new List<CustomFormat>
                {
                    new()
                    {
                        Id = 7,
                        Name = "Preferred Narrators",
                        AppliesTo = CustomFormatMediaType.Audiobook
                    }
                }
            };

            var resource = item.ToResource();

            Assert.That(resource.CustomFormats, Has.Count.EqualTo(1));
            Assert.That(resource.CustomFormats[0].Id, Is.EqualTo(7));
            Assert.That(resource.CustomFormats[0].Name, Is.EqualTo("Preferred Narrators"));
            Assert.That(resource.CustomFormats[0].AppliesTo, Is.EqualTo(CustomFormatMediaType.Audiobook));
        }
    }
}
