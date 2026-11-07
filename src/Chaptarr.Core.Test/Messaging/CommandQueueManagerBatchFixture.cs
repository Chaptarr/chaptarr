using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Composition;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Messaging.Commands;

namespace Chaptarr.Core.Test.Messaging
{
    [TestFixture]
    public class CommandQueueManagerBatchFixture
    {
        private class RepositoryProxy : DispatchProxy
        {
            public List<CommandModel> Inserted { get; } = new();
            public bool Fail { get; set; }
            public int? FailOnInsertNumber { get; set; }

            protected override object Invoke(MethodInfo method, object[] args)
            {
                if (method.Name == nameof(ICommandRepository.Insert))
                {
                    if (Fail || FailOnInsertNumber == Inserted.Count + 1)
                    {
                        throw new InvalidOperationException("Insert failed");
                    }

                    var model = (CommandModel)args[0];
                    model.Id = Inserted.Count + 1;
                    Inserted.Add(model);
                    return model;
                }

                if (method.Name == nameof(ICommandRepository.InsertMany))
                {
                    foreach (var model in (IEnumerable<CommandModel>)args[0])
                    {
                        if (Fail || FailOnInsertNumber == Inserted.Count + 1)
                        {
                            throw new InvalidOperationException("Insert failed");
                        }

                        model.Id = Inserted.Count + 1;
                        Inserted.Add(model);
                    }

                    return null;
                }

                throw new NotImplementedException(method.Name);
            }
        }

        [Test]
        public void should_deduplicate_both_within_batch_and_against_queued_commands()
        {
            var repository = DispatchProxy.Create<ICommandRepository, RepositoryProxy>();
            var state = (RepositoryProxy)(object)repository;
            var manager = new CommandQueueManager(repository, null, new KnownTypes(), LogManager.GetCurrentClassLogger());

            manager.PushMany(new List<RefreshAuthorCommand>
            {
                new(1, true, false, forceRefresh: true),
                new(1, true, false, forceRefresh: true),
                new(2, true, false, forceRefresh: true)
            });
            manager.PushMany(new List<RefreshAuthorCommand>
            {
                new(2, true, false, forceRefresh: true),
                new(3, true, false, forceRefresh: true)
            });

            Assert.That(state.Inserted.Select(c => ((RefreshAuthorCommand)c.Body).AuthorId), Is.EqualTo(new[] { 1, 2, 3 }));
            Assert.That(manager.All(), Has.Count.EqualTo(3));
        }

        [Test]
        public void should_not_queue_commands_when_the_first_database_insert_fails()
        {
            var repository = DispatchProxy.Create<ICommandRepository, RepositoryProxy>();
            var state = (RepositoryProxy)(object)repository;
            state.Fail = true;
            var manager = new CommandQueueManager(repository, null, new KnownTypes(), LogManager.GetCurrentClassLogger());

            Assert.Throws<InvalidOperationException>(() => manager.PushMany(new List<RefreshAuthorCommand> { new(1, true, false) }));
            Assert.That(manager.All(), Is.Empty);
        }

        [Test]
        public void should_keep_successfully_inserted_commands_visible_when_a_later_insert_fails()
        {
            var repository = DispatchProxy.Create<ICommandRepository, RepositoryProxy>();
            var state = (RepositoryProxy)(object)repository;
            state.FailOnInsertNumber = 2;
            var manager = new CommandQueueManager(repository, null, new KnownTypes(), LogManager.GetCurrentClassLogger());

            Assert.Throws<InvalidOperationException>(() => manager.PushMany(new List<RefreshAuthorCommand>
            {
                new(1, true, false),
                new(2, true, false)
            }));

            Assert.That(state.Inserted, Has.Count.EqualTo(1));
            Assert.That(manager.All(), Has.Count.EqualTo(1));
            Assert.That(((RefreshAuthorCommand)manager.All()[0].Body).AuthorId, Is.EqualTo(1));
        }
    }
}
