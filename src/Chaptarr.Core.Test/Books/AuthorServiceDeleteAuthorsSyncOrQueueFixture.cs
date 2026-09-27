using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.Books
{
    // AuthorService.DeleteAuthorsSyncOrQueue has already needed two rounds of behavioral fixes
    // (the 200-book threshold, the queued-vs-inline return value driving the controllers' 202 vs
    // 200 response) with no test coverage catching either round - covering the core decision here.
    [TestFixture]
    public class AuthorServiceDeleteAuthorsSyncOrQueueFixture
    {
        private class ThrowingProxy<T> : DispatchProxy where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"Test proxy does not implement {typeof(T).Name}.{targetMethod?.Name}");
            }
        }

        private class CountOnlyBookRepositoryProxy : DispatchProxy
        {
            public Dictionary<int, int> Counts { get; set; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (string.Equals(targetMethod?.Name, nameof(IBookRepository.CountBooksByAuthorIds), StringComparison.Ordinal))
                {
                    return Counts;
                }

                throw new NotImplementedException($"Test proxy does not implement IBookRepository.{targetMethod?.Name}");
            }
        }

        private class RecordingCommandQueueManagerProxy : DispatchProxy
        {
            public List<Command> PushedCommands { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (string.Equals(targetMethod?.Name, "Push", StringComparison.Ordinal) &&
                    args?.Length >= 1 && args[0] is Command command)
                {
                    PushedCommands.Add(command);
                    return null;
                }

                throw new NotImplementedException($"Test proxy does not implement IManageCommandQueue.{targetMethod?.Name}");
            }
        }

        private class RecordingAuthorRepositoryProxy : DispatchProxy
        {
            public Dictionary<int, Author> Authors { get; set; } = new();
            public List<int> DeleteManyCalls { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (string.Equals(targetMethod?.Name, "Get", StringComparison.Ordinal) &&
                    args?.Length == 1 && args[0] is IEnumerable<int> getIds)
                {
                    return getIds.Select(id => Authors.TryGetValue(id, out var author) ? author : null)
                        .Where(author => author != null)
                        .ToList();
                }

                if (string.Equals(targetMethod?.Name, "DeleteMany", StringComparison.Ordinal) &&
                    args?.Length == 1 && args[0] is IEnumerable<int> deleteIds)
                {
                    DeleteManyCalls.AddRange(deleteIds);
                    return null;
                }

                throw new NotImplementedException($"Test proxy does not implement IAuthorRepository.{targetMethod?.Name}");
            }
        }

        private sealed class NoOpEventAggregator : IEventAggregator
        {
            public void PublishEvent<TEvent>(TEvent @event)
                where TEvent : class, IEvent
            {
            }
        }

        [Test]
        public void should_queue_and_return_true_when_book_count_exceeds_the_threshold()
        {
            var bookRepository = DispatchProxy.Create<IBookRepository, CountOnlyBookRepositoryProxy>();
            ((CountOnlyBookRepositoryProxy)(object)bookRepository).Counts = new Dictionary<int, int> { { 1, 10113 } };

            var commandQueue = DispatchProxy.Create<IManageCommandQueue, RecordingCommandQueueManagerProxy>();
            var commandQueueRecorder = (RecordingCommandQueueManagerProxy)(object)commandQueue;

            var service = new AuthorService(
                DispatchProxy.Create<IAuthorRepository, ThrowingProxy<IAuthorRepository>>(),
                new NoOpEventAggregator(),
                null,
                null,
                commandQueue,
                new CacheManager(),
                bookRepository,
                null,
                LogManager.GetCurrentClassLogger());

            var queued = service.DeleteAuthorsSyncOrQueue(new List<int> { 1 }, deleteFiles: true);

            Assert.That(queued, Is.True);
            var command = commandQueueRecorder.PushedCommands.OfType<DeleteAuthorCommand>().Single();
            Assert.That(command.AuthorIds, Is.EquivalentTo(new[] { 1 }));
            Assert.That(command.DeleteFiles, Is.True);
        }

        [Test]
        public void should_delete_inline_and_return_false_when_book_count_is_at_or_under_the_threshold()
        {
            var author = new Author { Id = 1, Name = "Small Author" };

            var bookRepository = DispatchProxy.Create<IBookRepository, CountOnlyBookRepositoryProxy>();
            ((CountOnlyBookRepositoryProxy)(object)bookRepository).Counts = new Dictionary<int, int> { { 1, 5 } };

            var authorRepository = DispatchProxy.Create<IAuthorRepository, RecordingAuthorRepositoryProxy>();
            var authorRepoRecorder = (RecordingAuthorRepositoryProxy)(object)authorRepository;
            authorRepoRecorder.Authors = new Dictionary<int, Author> { { author.Id, author } };

            var service = new AuthorService(
                authorRepository,
                new NoOpEventAggregator(),
                null,
                null,
                DispatchProxy.Create<IManageCommandQueue, ThrowingProxy<IManageCommandQueue>>(),
                new CacheManager(),
                bookRepository,
                null,
                LogManager.GetCurrentClassLogger());

            var queued = service.DeleteAuthorsSyncOrQueue(new List<int> { author.Id }, deleteFiles: false);

            Assert.That(queued, Is.False);
            Assert.That(authorRepoRecorder.DeleteManyCalls, Does.Contain(author.Id));
        }
    }
}
