using System;
using NUnit.Framework;
using NzbDrone.Core.Books;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class MemberwiseEqualityIgnoreVolatileFieldsFixture
    {
        [Test]
        public void book_equality_should_ignore_last_updated_and_legacy_monitored_but_consider_provider_urls()
        {
            var a = new Book
            {
                Title = "Same",
                MediaType = BookMediaType.Audiobook,
                ProviderUrls = new ProviderUrlMap { ["goodreads"] = "https://example.com/a" },
                LastUpdated = new DateTime(2020, 01, 01, 0, 0, 0, DateTimeKind.Utc),
                Monitored = false
            };

            var b = new Book
            {
                Title = "Same",
                MediaType = BookMediaType.Audiobook,
                ProviderUrls = new ProviderUrlMap { ["goodreads"] = "https://example.com/a" },
                LastUpdated = new DateTime(2026, 01, 01, 0, 0, 0, DateTimeKind.Utc),
                Monitored = true
            };

            Assert.Multiple(() =>
            {
                Assert.That(a.Equals(b), Is.True);

                b.ProviderUrls["goodreads"] = "https://example.com/b";
                Assert.That(a.Equals(b), Is.False);
            });
        }

        [Test]
        public void edition_equality_should_ignore_last_updated_but_consider_provider_urls()
        {
            var a = new Edition
            {
                ForeignEditionId = "ed:1",
                Title = "Same",
                ProviderUrls = new ProviderUrlMap { ["hardcover"] = "https://example.com/a" },
                LastUpdated = new DateTime(2020, 01, 01, 0, 0, 0, DateTimeKind.Utc)
            };

            var b = new Edition
            {
                ForeignEditionId = "ed:1",
                Title = "Same",
                ProviderUrls = new ProviderUrlMap { ["hardcover"] = "https://example.com/a" },
                LastUpdated = new DateTime(2026, 01, 01, 0, 0, 0, DateTimeKind.Utc)
            };

            Assert.Multiple(() =>
            {
                Assert.That(a.Equals(b), Is.True);

                b.ProviderUrls["hardcover"] = "https://example.com/b";
                Assert.That(a.Equals(b), Is.False);
            });
        }

        [Test]
        public void series_equality_should_ignore_last_updated_but_consider_provider_urls()
        {
            var a = new Series
            {
                Title = "Same",
                ProviderUrls = new ProviderUrlMap { ["goodreads"] = "https://example.com/a" },
                LastUpdated = new DateTime(2020, 01, 01, 0, 0, 0, DateTimeKind.Utc)
            };

            var b = new Series
            {
                Title = "Same",
                ProviderUrls = new ProviderUrlMap { ["goodreads"] = "https://example.com/a" },
                LastUpdated = new DateTime(2026, 01, 01, 0, 0, 0, DateTimeKind.Utc)
            };

            Assert.Multiple(() =>
            {
                Assert.That(a.Equals(b), Is.True);

                b.ProviderUrls["goodreads"] = "https://example.com/b";
                Assert.That(a.Equals(b), Is.False);
            });
        }
    }
}
