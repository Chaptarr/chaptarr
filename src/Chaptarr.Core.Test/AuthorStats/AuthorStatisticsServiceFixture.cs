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
            public List<BookStatistics> AuthorStatistics(IEnumerable<int> authorIds) => Stats();
            public List<BookStatistics> AuthorStatistics(string mediaType) => Stats();
            public List<BookStatistics> AuthorStatistics(int authorId, string mediaType) => Stats();
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
        public void identity_changes_refresh_narrator_and_system_counts()
        {
            var narratorReads = 0;
            var systemReads = 0;
            var narrators = new NzbDrone.Core.NarratorStats.NarratorStatisticsService(
                Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<NzbDrone.Core.NarratorStats.INarratorStatisticsRepository>((_, _) =>
                { narratorReads++; return new List<NzbDrone.Core.NarratorStats.BookStatistics>(); }), new CacheManager());
            var system = new NzbDrone.Core.SystemStats.SystemStatisticsService(
                Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<NzbDrone.Core.SystemStats.ISystemStatisticsRepository>((_, _) =>
                { systemReads++; return new NzbDrone.Core.SystemStats.SystemStatistics(); }), new CacheManager(), NLog.LogManager.GetCurrentClassLogger());
            narrators.NarratorStatistics();
            system.GetSystemStatistics("audiobook");
            var change = new NzbDrone.Core.Books.Events.LibraryIdentityChangedEvent(new[] { 1 }, new[] { 10 });
            narrators.Handle(change);
            system.Handle(change);
            narrators.NarratorStatistics();
            system.GetSystemStatistics("audiobook");
            Assert.That(narratorReads, Is.EqualTo(2));
            Assert.That(systemReads, Is.EqualTo(2));
        }

        [Test]
        public void identity_changes_invalidate_statistics_without_a_delete_event()
        {
            var repository = new RecordingRepository();
            var service = new AuthorStatisticsService(repository, new CacheManager());
            service.AuthorStatistics();
            service.Handle(new NzbDrone.Core.Books.Events.LibraryIdentityChangedEvent(new[] { 1 }, new[] { 10 }, new[] { 11 }));
            service.AuthorStatistics();
            Assert.That(repository.AllAuthorCalls, Is.EqualTo(2));
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
