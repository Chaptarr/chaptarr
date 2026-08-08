using System;
using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.AuthorStats;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.Events;

namespace Chaptarr.Core.Test.AuthorStats
{
    [TestFixture]
    public class AuthorStatisticsServiceFixture
    {
        private sealed class RecordingRepository : IAuthorStatisticsRepository
        {
            public int AllAuthorCalls { get; private set; }

            public List<BookStatistics> AuthorStatistics()
            {
                AllAuthorCalls++;
                return Stats();
            }

            public List<BookStatistics> AuthorStatistics(int authorId) => Stats();
            public List<BookStatistics> AuthorStatistics(string mediaType) => Stats();
            public List<BookStatistics> AuthorStatistics(int authorId, string mediaType) => Stats();
            public BookAggregate GetAggregateStatistics(List<int> authorIds, string mediaType) => new();

            private static List<BookStatistics> Stats()
            {
                return new()
                {
                    new BookStatistics
                    {
                        AuthorId = 1,
                        BookId = 10,
                        BookCount = 1,
                        TotalBookCount = 1
                    }
                };
            }
        }

        [Test]
        public void file_events_should_not_evict_the_global_cache_during_an_active_import_session()
        {
            const int commandId = 912301;
            var repository = new RecordingRepository();
            var service = new AuthorStatisticsService(repository, new CacheManager());

            try
            {
                service.AuthorStatistics();
                ImportSessionProgressTracker.Activate(commandId);

                service.Handle(new BookFileAddedEvent(new BookFile
                {
                    Author = new NzbDrone.Core.Books.Author { Id = 1 }
                }));

                service.AuthorStatistics();

                Assert.That(repository.AllAuthorCalls, Is.EqualTo(1));
            }
            finally
            {
                ImportSessionProgressTracker.Clear(commandId);
            }
        }

        [Test]
        public void file_events_should_evict_the_global_cache_outside_an_import_session()
        {
            var repository = new RecordingRepository();
            var service = new AuthorStatisticsService(repository, new CacheManager());

            service.AuthorStatistics();
            service.Handle(new BookFileAddedEvent(new BookFile
            {
                Author = new NzbDrone.Core.Books.Author { Id = 1 }
            }));
            service.AuthorStatistics();

            Assert.That(repository.AllAuthorCalls, Is.EqualTo(2));
        }
    }
}
