using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Messaging.Commands;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class BookAddedHandlerFixture
    {
        [Test]
        public void should_refresh_metadata_for_stale_author_and_keep_folder_scan()
        {
            var recorder = CreateQueue(out var queue);
            var subject = CreateSubject(queue);

            subject.Handle(CreateEvent(new Author
            {
                Id = 12,
                LastInfoSync = DateTime.UtcNow.AddDays(-31)
            }));

            var command = (RefreshAuthorCommand)recorder.Commands.Single();
            Assert.Multiple(() =>
            {
                Assert.That(command.AuthorId, Is.EqualTo(12));
                Assert.That(command.RefreshMetadata, Is.True);
                Assert.That(command.RescanFolders, Is.True);
                Assert.That(command.RescanWhenMetadataUnchanged, Is.True);
                Assert.That(command.ForceRefresh, Is.False);
            });
        }

        [Test]
        public void should_skip_recent_author_metadata_refresh_but_keep_folder_scan()
        {
            var recorder = CreateQueue(out var queue);
            var subject = CreateSubject(queue);

            subject.Handle(CreateEvent(new Author
            {
                Id = 12,
                LastInfoSync = DateTime.UtcNow.AddHours(-1)
            }));

            var command = (RefreshAuthorCommand)recorder.Commands.Single();
            Assert.Multiple(() =>
            {
                Assert.That(command.RefreshMetadata, Is.False);
                Assert.That(command.RescanFolders, Is.True);
                Assert.That(command.RescanWhenMetadataUnchanged, Is.True);
                Assert.That(command.ForceRefresh, Is.False);
            });
        }

        [Test]
        public void should_fail_open_to_metadata_refresh_when_author_relationship_is_missing()
        {
            var recorder = CreateQueue(out var queue);
            var subject = CreateSubject(queue);

            subject.Handle(CreateEvent(author: null));

            Assert.That(((RefreshAuthorCommand)recorder.Commands.Single()).RefreshMetadata, Is.True);
        }

        [Test]
        public void should_not_queue_a_refresh_when_event_suppresses_it()
        {
            var recorder = CreateQueue(out var queue);
            var subject = CreateSubject(queue);

            subject.Handle(new BookAddedEvent(new NzbDrone.Core.Books.Book
            {
                Author = new Author { Id = 12, LastInfoSync = DateTime.UtcNow },
                AuthorId = 12
            }, doRefresh: false));

            Assert.That(recorder.Commands, Is.Empty);
        }

        private static BookAddedHandler CreateSubject(IManageCommandQueue queue)
        {
            var bookService = DispatchProxy.Create<IBookService, UnexpectedBookServiceProxy>();
            return new BookAddedHandler(queue, new ShouldRefreshAuthor(bookService, LogManager.GetCurrentClassLogger()));
        }

        private static BookAddedEvent CreateEvent(Author author)
        {
            return new BookAddedEvent(new NzbDrone.Core.Books.Book
            {
                Author = author,
                AuthorId = 12
            });
        }

        private static RecordingCommandQueueProxy CreateQueue(out IManageCommandQueue queue)
        {
            queue = DispatchProxy.Create<IManageCommandQueue, RecordingCommandQueueProxy>();
            return (RecordingCommandQueueProxy)(object)queue;
        }

        public class UnexpectedBookServiceProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"Freshness fixture does not expect IBookService.{targetMethod?.Name}");
            }
        }

        public class RecordingCommandQueueProxy : DispatchProxy
        {
            public List<Command> Commands { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IManageCommandQueue.Push))
                {
                    var command = (Command)args[0];
                    Commands.Add(command);
                    return new CommandModel { Name = command.Name, Body = command };
                }

                throw new NotImplementedException($"Command queue fixture does not implement IManageCommandQueue.{targetMethod?.Name}");
            }
        }
    }
}
