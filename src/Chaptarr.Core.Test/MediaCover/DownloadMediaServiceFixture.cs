using System.Collections.Generic;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaCover.Commands;

namespace Chaptarr.Core.Test.MediaCover
{
    [TestFixture]
    public class DownloadMediaServiceFixture
    {
        private class BookServiceProxy : DispatchProxy
        {
            public List<int> RequestedBookIds { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IBookService.GetBook))
                {
                    var bookId = (int)args[0];
                    RequestedBookIds.Add(bookId);
                    return new Book { Id = bookId, Title = $"Book {bookId}" };
                }

                throw new System.NotImplementedException($"Test proxy does not implement {targetMethod?.Name}");
            }
        }

        private class CoverMapperProxy : DispatchProxy
        {
            public List<int> EnsuredBookIds { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IMapCoversToLocal.EnsureBookCovers))
                {
                    EnsuredBookIds.Add(((Book)args[0]).Id);
                    return null;
                }

                throw new System.NotImplementedException($"Test proxy does not implement {targetMethod?.Name}");
            }
        }

        [Test]
        public void should_download_covers_for_all_resolved_book_ids()
        {
            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var bookServiceProxy = (BookServiceProxy)(object)bookService;
            var coverMapper = DispatchProxy.Create<IMapCoversToLocal, CoverMapperProxy>();
            var coverMapperProxy = (CoverMapperProxy)(object)coverMapper;
            var service = new DownloadMediaService(
                null,
                bookService,
                null,
                coverMapper,
                null,
                null,
                LogManager.GetCurrentClassLogger());

            service.Execute(new DownloadBookMediaCommand(30)
            {
                BookIds = new List<int> { 30, 61 }
            });

            Assert.Multiple(() =>
            {
                Assert.That(bookServiceProxy.RequestedBookIds, Is.EqualTo(new[] { 30, 61 }));
                Assert.That(coverMapperProxy.EnsuredBookIds, Is.EqualTo(new[] { 30, 61 }));
            });
        }
    }
}
