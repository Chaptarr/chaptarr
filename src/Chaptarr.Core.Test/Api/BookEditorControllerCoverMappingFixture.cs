using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Chaptarr.Api.V1.Books;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaCover;
using Cover = NzbDrone.Core.MediaCover.MediaCover;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class BookEditorControllerCoverMappingFixture
    {
        private class ServiceProxy<T> : DispatchProxy where T : class
        {
            public Func<MethodInfo, object[], object> Handler { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return Handler(targetMethod, args);
            }
        }

        private static T Proxy<T>(Func<MethodInfo, object[], object> handler) where T : class
        {
            var proxy = DispatchProxy.Create<T, ServiceProxy<T>>();
            ((ServiceProxy<T>)(object)proxy).Handler = handler;
            return proxy;
        }

        [Test]
        public void bulk_unmonitor_maps_cover_resources_after_lifecycle_save()
        {
            const int bookId = 17;
            const string remoteUrl = "https://covers.example/book.jpg";
            var sourceCover = new Cover(MediaCoverTypes.Poster, remoteUrl) { RemoteUrl = remoteUrl };
            var book = new Book
            {
                Id = bookId,
                Title = "Fixture Book",
                MediaType = BookMediaType.Audiobook,
                AudiobookMonitored = true,
                EbookMonitored = false,
                Images = new List<Cover> { sourceCover }
            };
            var lifecycleSaved = false;
            var batchMapperCalled = false;
            List<KeyValuePair<int, List<Cover>>> mappedCovers = null;

            var bookService = Proxy<IBookService>((method, args) =>
            {
                if (method.Name == nameof(IBookService.GetBooks))
                {
                    return new List<Book> { book };
                }

                if (method.Name == nameof(IBookService.UpdateManyWithLifecycle))
                {
                    Assert.That(lifecycleSaved, Is.False);
                    Assert.That(book.AudiobookMonitored, Is.False);
                    lifecycleSaved = true;
                    return null;
                }

                throw new NotImplementedException(method.Name);
            });

            var coverMapper = Proxy<IMapCoversToLocal>((method, args) =>
            {
                if (method.Name != nameof(IMapCoversToLocal.ConvertBookCoversToLocalUrls))
                {
                    throw new NotImplementedException(method.Name);
                }

                Assert.That(lifecycleSaved, Is.True);
                mappedCovers = ((IEnumerable<KeyValuePair<int, List<Cover>>>)args[0]).ToList();
                Assert.That(mappedCovers, Has.Count.EqualTo(1));
                Assert.That(mappedCovers[0].Key, Is.EqualTo(bookId));
                Assert.That(mappedCovers[0].Value[0], Is.Not.SameAs(sourceCover));

                // Model the LibraryBooks result: retain provider metadata while suppressing list URLs.
                mappedCovers[0].Value[0].Url = null;
                mappedCovers[0].Value[0].TemporaryUrl = string.Empty;
                batchMapperCalled = true;
                return null;
            });

            var controller = new BookEditorController(bookService, null, coverMapper)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };

            var result = controller.SaveAll(new BookEditorResource
            {
                BookIds = new List<int> { bookId },
                Monitored = false
            });

            Assert.That(result, Is.InstanceOf<AcceptedResult>());
            Assert.That(lifecycleSaved, Is.True);
            Assert.That(batchMapperCalled, Is.True);
            var returnedBooks = ((AcceptedResult)result).Value as List<BookResource>;
            Assert.That(returnedBooks, Has.Count.EqualTo(1));
            Assert.That(returnedBooks[0].Images[0].Url, Is.Null);
            Assert.That(returnedBooks[0].Images[0].TemporaryUrl, Is.EqualTo(string.Empty));
            Assert.That(returnedBooks[0].Images[0].RemoteUrl, Is.EqualTo(remoteUrl));
            Assert.That(sourceCover.Url, Is.EqualTo(remoteUrl));
        }
    }
}
