using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Chaptarr.Http.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NUnit.Framework;
using NzbDrone.Api.V1.Editions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaCover;
using CoreMediaCover = NzbDrone.Core.MediaCover.MediaCover;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class EditionControllerFacadeFixture
    {
        private class EditionServiceProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IEditionService.GetEditionsByBook) &&
                    args.Length == 1 &&
                    args[0] is IEnumerable<int> bookIds)
                {
                    return bookIds.Contains(10)
                        ? new List<Edition>
                        {
                            new Edition
                            {
                                Id = 1354135,
                                BookId = 10,
                                Title = "La tregua",
                                ForeignEditionId = "gr:199400410-ebook",
                                GoodreadsEditionId = 199400410,
                                Monitored = false,
                                Images = new List<CoreMediaCover>
                                {
                                    new CoreMediaCover(MediaCoverTypes.Cover, "https://images.example/la-tregua.jpg")
                                }
                            },
                            new Edition
                            {
                                Id = 1481726,
                                BookId = 10,
                                Title = "The Reawakening",
                                ForeignEditionId = "hc:edition:30643037-ebook",
                                HardcoverEditionId = "30643037",
                                Monitored = true,
                                Images = new List<CoreMediaCover>
                                {
                                    new CoreMediaCover(MediaCoverTypes.Cover, "https://images.example/cover.jpg")
                                }
                            }
                        }
                        : new List<Edition>();
                }

                throw new NotImplementedException(targetMethod?.Name);
            }
        }

        private class MediaCoverProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IMediaCoverProxy.ProxyRemoteUrls))
                {
                    foreach (var cover in (IEnumerable<CoreMediaCover>)args[0])
                    {
                        cover.Url = "/MediaCoverProxy/test/" + System.IO.Path.GetFileName(new Uri(cover.Url).AbsolutePath);
                    }

                    return null;
                }

                throw new NotImplementedException(targetMethod?.Name);
            }
        }

        private class BookServiceProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IBookService.GetBooks))
                {
                    return new List<Book>
                    {
                        new Book
                        {
                            Id = 10,
                            AuthorId = 1,
                            MediaType = BookMediaType.Ebook
                        }
                    };
                }

                if (targetMethod?.Name == nameof(IBookService.GetBooksByAuthor))
                {
                    return new List<Book>();
                }

                throw new NotImplementedException(targetMethod?.Name);
            }
        }

        [Test]
        public void should_emit_facade_edition_id_for_edition_book_lookup()
        {
            var controller = new EditionController(
                DispatchProxy.Create<IEditionService, EditionServiceProxy>(),
                DispatchProxy.Create<IBookService, BookServiceProxy>(),
                DispatchProxy.Create<IMediaCoverProxy, MediaCoverProxy>())
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext()
                }
            };
            controller.ControllerContext.HttpContext.Items[ReadarrFacadeContext.ItemKey] =
                new ReadarrFacadeContext("hc", "ebook", "/readarr/hc/ebook");

            var resources = controller.GetEditions(new List<int> { 10 });

            Assert.That(resources.Single().ForeignEditionId, Is.EqualTo("30643037"));
            Assert.That(resources.Single().Id, Is.EqualTo(1481726));
            Assert.That(resources.Single().Images.Single().Url, Is.EqualTo("/MediaCoverProxy/test/cover.jpg"));
        }

        [Test]
        public void narrator_search_should_load_each_edition_own_cover_without_persisting_or_borrowing_book_art()
        {
            var controller = new EditionController(
                DispatchProxy.Create<IEditionService, EditionServiceProxy>(),
                DispatchProxy.Create<IBookService, BookServiceProxy>(),
                DispatchProxy.Create<IMediaCoverProxy, MediaCoverProxy>())
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext()
                }
            };

            var resources = controller.GetEditions(new List<int> { 10 });

            Assert.That(resources.Select(resource => resource.Id), Is.EqualTo(new[] { 1354135, 1481726 }));
            Assert.That(resources.SelectMany(resource => resource.Images).Select(image => image.Url),
                Is.EqualTo(new[]
                {
                    "/MediaCoverProxy/test/la-tregua.jpg",
                    "/MediaCoverProxy/test/cover.jpg"
                }));
            Assert.That(resources.All(resource => resource.Images.All(image => image.Url.StartsWith("/MediaCoverProxy/"))), Is.True,
                "Browsing narrator variants must remain lazy; only a selected/monitored edition is persisted as the book cover.");
        }
    }
}
