using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.ImportLists;

namespace Chaptarr.Core.Test.ImportLists
{
    [TestFixture]
    public class ImportListSyncServiceProviderAliasFixture
    {
        [Test]
        public void book_provider_ids_should_include_remote_provider_aliases()
        {
            var book = new Book
            {
                HardcoverBookId = "hc:383236",
                RemoteProviderIds = new HashSet<string> { "gr:3046572" }
            };

            var ids = InvokeGetBookProviderIds(book);

            Assert.That(ids, Does.Contain("gr:3046572"));
        }

        [Test]
        public void book_matching_should_match_remote_provider_aliases()
        {
            var book = new Book
            {
                HardcoverBookId = "hc:383236",
                RemoteProviderIds = new HashSet<string> { "gr:3046572" }
            };

            var matches = InvokeBookMatchesProviderId(book, "gr", "gr:3046572", "3046572");

            Assert.That(matches, Is.True);
        }

        [Test]
        public void author_provider_ids_should_include_remote_provider_aliases()
        {
            var author = new Author
            {
                HardcoverAuthorId = "hc:80626",
                RemoteProviderIds = new HashSet<string> { "gr:1077326" }
            };

            var ids = InvokeGetAuthorProviderIds(author);

            Assert.That(ids, Does.Contain("gr:1077326"));
        }

        private static List<string> InvokeGetBookProviderIds(Book book)
        {
            var method = typeof(ImportListSyncService).GetMethod("GetBookProviderIds", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);

            return ((IEnumerable<string>)method.Invoke(null, new object[] { book })).ToList();
        }

        private static List<string> InvokeGetAuthorProviderIds(Author author)
        {
            var method = typeof(ImportListSyncService).GetMethod("GetAuthorProviderIds", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);

            return ((IEnumerable<string>)method.Invoke(null, new object[] { author })).ToList();
        }

        private static bool InvokeBookMatchesProviderId(Book book, string providerPrefix, string providerId, string rawId)
        {
            var method = typeof(ImportListSyncService).GetMethod("BookMatchesProviderId", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);

            return (bool)method.Invoke(null, new object[] { book, providerPrefix, providerId, rawId });
        }
    }
}
