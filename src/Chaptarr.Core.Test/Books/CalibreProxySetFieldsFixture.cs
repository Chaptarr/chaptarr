using System;
using System.Collections.Generic;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.MediaFiles;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class CalibreProxySetFieldsFixture
    {
        private sealed class ReachedHttpException : Exception
        {
        }

        // Any HTTP call means the payload was built; stop there.
        private class HttpClientProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new ReachedHttpException();
            }
        }

        [Test]
        public void should_build_payload_when_provider_metadata_is_missing()
        {
            var book = new Book
            {
                Id = 1,
                Title = "City of Lost Souls",
                Genres = null,
                LazySeriesLinks = null
            };
            var edition = new Edition
            {
                Id = 2,
                BookId = book.Id,
                Book = book,
                Title = book.Title,
                Images = null,
                Ratings = null
            };
            var file = new BookFile
            {
                CalibreId = 2766,
                Path = "/data/calibre/library/Cassandra Clare/City of Lost Souls (2766)/City of Lost Souls.epub",
                Edition = edition,
                Author = new Author { Name = "Cassandra Clare" }
            };

            var subject = new CalibreProxy(
                DispatchProxy.Create<IHttpClient, HttpClientProxy>(),
                null, null, null, null, null, new CacheManager(),
                LogManager.GetLogger(nameof(CalibreProxySetFieldsFixture)));

            Assert.Throws<ReachedHttpException>(() => subject.SetFields(file, new CalibreSettings { Host = "calibre", Port = 8181 }, false));
        }
    }
}
