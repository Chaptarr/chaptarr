using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Books;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class AuthorIdentityFixture
    {
        [Test]
        public void should_collect_scalar_and_remote_provider_ids()
        {
            var author = new Author
            {
                HardcoverAuthorId = "hc:80626",
                GoodreadsAuthorId = "1077326",
                RemoteProviderIds = new HashSet<string>
                {
                    "gr:1244",
                    "hc:80626",
                    "bogus:123"
                }
            };

            var ids = AuthorIdentity.GetProviderIdentityTokenList(author);

            Assert.That(ids, Is.EquivalentTo(new[]
            {
                "hc:80626",
                "gr:1077326",
                "gr:1244"
            }));
        }

        [Test]
        public void should_match_authors_by_any_provider_alias_overlap()
        {
            var local = new Author
            {
                HardcoverAuthorId = "hc:80626",
                RemoteProviderIds = new HashSet<string> { "gr:1077326" }
            };

            var remote = new Author
            {
                GoodreadsAuthorId = "gr:1077326"
            };

            Assert.That(AuthorIdentity.MatchesByProviderIdIntersection(local, remote), Is.True);
        }

        [Test]
        public void should_not_match_authors_without_provider_alias_overlap()
        {
            var local = new Author
            {
                HardcoverAuthorId = "hc:80626"
            };

            var remote = new Author
            {
                GoodreadsAuthorId = "gr:999999"
            };

            Assert.That(AuthorIdentity.MatchesByProviderIdIntersection(local, remote), Is.False);
        }

        [Test]
        public void should_get_work_lookup_author_hint_for_same_supported_provider()
        {
            var author = new Author
            {
                GoodreadsAuthorId = "1077326",
                HardcoverAuthorId = "hc:80626",
                OpenLibraryAuthorId = "OL123A"
            };

            Assert.That(AuthorIdentity.GetWorkLookupAuthorHintForProviderId(author, "gr:208822121"), Is.EqualTo("gr:1077326"));
            Assert.That(AuthorIdentity.GetWorkLookupAuthorHintForProviderId(author, "hc:383236"), Is.EqualTo("hc:80626"));
            Assert.That(AuthorIdentity.GetWorkLookupAuthorHintForProviderId(author, "ol:OL456W"), Is.Null);
        }

        [TestCase("gr:208822121", "gr:1077326", "gr:1077326")]
        [TestCase("hc:383236", "hc:80626", "hc:80626")]
        [TestCase("gr:208822121", "hc:80626", null)]
        [TestCase("hc:383236", "gr:1077326", null)]
        [TestCase("ol:OL456W", "ol:OL123A", null)]
        [TestCase("208822121", "gr:1077326", null)]
        public void should_normalize_work_lookup_author_hint_only_for_same_supported_provider(string providerId, string authorProviderId, string expected)
        {
            Assert.That(AuthorIdentity.NormalizeWorkLookupAuthorHint(providerId, authorProviderId), Is.EqualTo(expected));
        }
    }
}
