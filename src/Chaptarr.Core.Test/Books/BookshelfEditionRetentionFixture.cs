using System;
using System.Collections.Generic;
using System.Linq;
using Moq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Profiles.Metadata;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class BookshelfEditionRetentionFixture
    {
        private sealed class RefreshService : RefreshBookService
        {
            public RefreshService(IConfigService config)
                : base(null, null, null, null, null, null, null, null, null, null, null,
                    new EditionSelector(LogManager.GetCurrentClassLogger()), new EditionMetadataProfileFilter(null),
                    null, LogManager.GetCurrentClassLogger(), config)
            {
            }

            public List<Edition> Editions(Book local, Book remote) => GetRemoteChildren(local, remote);

            public Tuple<Edition, List<Edition>> Match(List<Edition> local, Edition remote) => GetMatchingExistingChildren(local, remote);
        }

        [TestCase(true, true)]
        [TestCase(false, false)]
        public void should_match_imported_provider_alias_only_in_compatibility_mode(bool compatibility, bool expected)
        {
            var config = new Mock<IConfigService>();
            config.SetupGet(value => value.BookshelfMatchCompatibility).Returns(compatibility);
            var service = new RefreshService(config.Object);
            var imported = new Edition { Id = 12, ForeignEditionId = "hc:42", HardcoverEditionId = "42", Isbn13 = "9780000000001" };
            var local = new List<Edition> { imported };
            var remote = new Edition { ForeignEditionId = "hc:edition:42", HardcoverEditionId = "42" };

            Assert.That(service.Match(local, new Edition { ForeignEditionId = "hc:edition:43", HardcoverEditionId = "43", Isbn13 = imported.Isbn13 }).Item1,
                Is.Null, "Shared ISBNs must not merge different provider editions");
            Assert.That(service.Match(local, remote).Item1, expected ? Is.SameAs(imported) : Is.Null);
            Assert.That(service.Match(local, remote).Item1, Is.Null, "An edition cannot be consumed twice");
            Assert.That(service.Match(local, new Edition { ForeignEditionId = "hc:edition:43", HardcoverEditionId = "43", Isbn13 = imported.Isbn13 }).Item1,
                Is.Null, "Shared ISBNs must not merge different provider editions");
        }

        [Test]
        public void should_not_guess_between_ambiguous_imported_aliases()
        {
            var config = new Mock<IConfigService>();
            config.SetupGet(value => value.BookshelfMatchCompatibility).Returns(true);
            var local = new List<Edition>
            {
                new Edition { Id = 12, ForeignEditionId = "hc:42", HardcoverEditionId = "42" },
                new Edition { Id = 13, ForeignEditionId = "legacy:42", HardcoverEditionId = "42" }
            };
            Assert.That(new RefreshService(config.Object).Match(local,
                new Edition { ForeignEditionId = "hc:edition:42", HardcoverEditionId = "42" }).Item1, Is.Null);
        }

        [TestCase(true, 3)]
        [TestCase(false, 1)]
        public void should_keep_source_allowed_editions_without_bypassing_profile_filters(bool compatibility, int expected)
        {
            var config = new Mock<IConfigService>();
            config.SetupGet(value => value.BookshelfMatchCompatibility).Returns(compatibility);
            var local = new Book
            {
                Title = "Test Work",
                MediaType = BookMediaType.Ebook,
                AuthorId = 1,
                Author = new Author { Id = 1, EbookMetadataProfile = new MetadataProfile { AllowedLanguages = "eng" } }
            };
            var remote = new Book
            {
                Title = "Test Work",
                Editions = Enumerable.Range(1, 4).Select(id => new Edition
                {
                    ForeignEditionId = "hc:edition:" + id,
                    HardcoverEditionId = id.ToString(),
                    Title = "Edition " + id,
                    ReadingFormatId = 1,
                    Language = id == 4 ? "fra" : "eng"
                }).ToList()
            };

            var editions = new RefreshService(config.Object).Editions(local, remote);

            Assert.That(editions, Has.Count.EqualTo(expected));
            Assert.That(editions.Select(edition => edition.Language), Is.All.EqualTo("eng"));
        }
    }
}
