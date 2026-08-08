using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1.Queue;
using NUnit.Framework;
using NzbDrone.Core.Books;
using CoreQueue = NzbDrone.Core.Queue.Queue;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class QueueMediaTypeFilterFixture
    {
        [Test]
        public void should_keep_unknown_rows_when_filtering_by_media_type()
        {
            var queue = new List<CoreQueue>
            {
                new CoreQueue { Id = 1, Author = null, Book = null },
                new CoreQueue { Id = 2, Author = new Author(), Book = null },
                new CoreQueue { Id = 3, Author = new Author(), Book = new Book { MediaType = BookMediaType.Ebook } },
                new CoreQueue { Id = 4, Author = new Author(), Book = new Book { MediaType = BookMediaType.Audiobook } }
            };

            var filtered = QueueMediaTypeFilter.FilterByMediaType(queue, "ebook").ToList();

            Assert.That(filtered.Select(q => q.Id), Is.EqualTo(new[] { 1, 2, 3 }));
        }
    }
}
