using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class ProviderIdPoisonSanitizationFixture
    {
        private const string Poison = "System.Collections.Generic.List`1[System.String]";

        [Test]
        public void book_metadata_copy_should_discard_poisoned_remote_alias_set()
        {
            var local = new Book
            {
                BaseBookId = "hc:123",
                HardcoverBookId = "hc:123",
                RemoteProviderIds = new HashSet<string> { "gr:456" }
            };

            local.UseMetadataFrom(new Book
            {
                Title = "Remote",
                BaseBookId = Poison,
                HardcoverBookId = Poison,
                RemoteProviderIds = new HashSet<string> { Poison }
            });

            Assert.Multiple(() =>
            {
                Assert.That(local.BaseBookId, Is.EqualTo("hc:123"));
                Assert.That(local.HardcoverBookId, Is.EqualTo("hc:123"));
                Assert.That(local.RemoteProviderIds, Is.Null);
            });
        }

        [Test]
        public void book_metadata_copy_should_null_poisoned_local_when_remote_has_no_clean_replacement()
        {
            var local = new Book
            {
                BaseBookId = Poison,
                HardcoverBookId = Poison,
                RemoteProviderIds = new HashSet<string> { Poison }
            };

            local.UseMetadataFrom(new Book { Title = "Remote" });

            Assert.Multiple(() =>
            {
                Assert.That(local.BaseBookId, Is.Null);
                Assert.That(local.HardcoverBookId, Is.Null);
                Assert.That(local.RemoteProviderIds, Is.Null);
            });
        }

        [Test]
        public void edition_metadata_copy_should_keep_clean_local_when_remote_provider_id_is_poisoned()
        {
            var local = new Edition
            {
                ForeignEditionId = "hc:edition:123-audiobook",
                HardcoverEditionId = "123",
                OpenLibraryEditionId = "ol:abc",
                GoogleBooksEditionId = "gb:def",
                Asin = "B012345678",
                AudibleASIN = "B087654321",
                Asins = new List<string> { "B012345678" }
            };

            local.UseMetadataFrom(new Edition
            {
                ForeignEditionId = $"hc:edition:{Poison}-audiobook",
                HardcoverEditionId = Poison,
                OpenLibraryEditionId = Poison,
                GoogleBooksEditionId = Poison,
                Asin = Poison,
                AudibleASIN = Poison,
                Asins = new List<string> { Poison }
            });

            Assert.Multiple(() =>
            {
                Assert.That(local.ForeignEditionId, Is.EqualTo("hc:edition:123-audiobook"));
                Assert.That(local.HardcoverEditionId, Is.EqualTo("123"));
                Assert.That(local.OpenLibraryEditionId, Is.EqualTo("ol:abc"));
                Assert.That(local.GoogleBooksEditionId, Is.EqualTo("gb:def"));
                Assert.That(local.Asin, Is.EqualTo("B012345678"));
                Assert.That(local.AudibleASIN, Is.EqualTo("B087654321"));
                Assert.That(local.Asins, Does.Contain("B012345678"));
            });
        }

        [Test]
        public void edition_metadata_copy_should_null_poisoned_local_when_remote_has_no_clean_replacement()
        {
            var local = new Edition
            {
                ForeignEditionId = Poison,
                HardcoverEditionId = Poison,
                OpenLibraryEditionId = Poison,
                GoogleBooksEditionId = Poison,
                Asin = Poison,
                AudibleASIN = Poison,
                Asins = new List<string> { Poison }
            };

            local.UseMetadataFrom(new Edition());

            Assert.Multiple(() =>
            {
                Assert.That(local.ForeignEditionId, Is.Null);
                Assert.That(local.HardcoverEditionId, Is.Null);
                Assert.That(local.OpenLibraryEditionId, Is.Null);
                Assert.That(local.GoogleBooksEditionId, Is.Null);
                Assert.That(local.Asin, Is.Null);
                Assert.That(local.AudibleASIN, Is.Null);
                Assert.That(local.Asins, Is.Empty);
            });
        }

        [Test]
        public void provider_alias_service_should_reject_stringified_collection_aliases()
        {
            var service = new ProviderAliasService(null);

            var aliases = service.NormalizeProviderIds(new[]
            {
                "gr:123",
                "hc:edition:456",
                $"hc:{Poison}",
                $"hc:edition:{Poison}"
            });

            Assert.Multiple(() =>
            {
                Assert.That(aliases, Does.Contain(("gr", "123")));
                Assert.That(aliases, Does.Contain(("hc", "edition:456")));
                Assert.That(aliases.Any(a => a.NormalizedProviderId.Contains("System.Collections")), Is.False);
            });
        }
    }
}
