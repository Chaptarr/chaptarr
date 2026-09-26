using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common;
using NzbDrone.Common.Composition;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Messaging.Commands;

namespace Chaptarr.Core.Test.Messaging
{
    [TestFixture]
    public class CommandQueueManagerPushManyFixture
    {
        private class CommandRepositoryProxy : DispatchProxy
        {
            public List<CommandModel> Inserted { get; } = new List<CommandModel>();
            public int InsertManyCalls { get; private set; }
            public int? FailOnInsertNumber { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(ICommandRepository.Insert) && args.Length == 1)
                {
                    if (FailOnInsertNumber == Inserted.Count + 1)
                    {
                        throw new InvalidOperationException("insert failed");
                    }

                    var model = (CommandModel)args[0];
                    model.Id = Inserted.Count + 1;
                    Inserted.Add(model);
                    return model;
                }

                if (targetMethod?.Name == nameof(ICommandRepository.InsertMany))
                {
                    InsertManyCalls++;

                    // Reproduces the Postgres batch path, which cannot bind a Body whose runtime type is a
                    // Command subclass.
                    throw new NotSupportedException("The member p0_Body of type RefreshAuthorCommand cannot be used as a parameter value");
                }

                throw new NotImplementedException($"Test proxy does not implement ICommandRepository.{targetMethod?.Name}");
            }
        }

        private static (CommandQueueManager Manager, CommandRepositoryProxy Repo) Create()
        {
            var repo = DispatchProxy.Create<ICommandRepository, CommandRepositoryProxy>();
            var proxy = (CommandRepositoryProxy)(object)repo;
            var manager = new CommandQueueManager(repo, null, new KnownTypes(), LogManager.GetLogger("test"));
            return (manager, proxy);
        }

        private static List<RefreshAuthorCommand> Commands(int count)
        {
            return Enumerable.Range(1, count).Select(i => new RefreshAuthorCommand(i, true, false, forceRefresh: true)).ToList();
        }

        [Test]
        public void should_queue_all_commands_without_using_insert_many()
        {
            var (manager, repo) = Create();

            var models = manager.PushMany(Commands(3));

            Assert.AreEqual(3, models.Count);
            Assert.AreEqual(0, repo.InsertManyCalls);
            Assert.AreEqual(3, repo.Inserted.Count);
            Assert.AreEqual(3, manager.All().Count);
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, models.Select(m => m.Id));
        }

        [Test]
        public void should_do_nothing_for_an_empty_batch()
        {
            var (manager, repo) = Create();

            var models = manager.PushMany(new List<RefreshAuthorCommand>());

            Assert.IsEmpty(models);
            Assert.IsEmpty(repo.Inserted);
            Assert.IsEmpty(manager.All());
        }

        [Test]
        public void should_not_orphan_already_inserted_commands_when_a_later_insert_fails()
        {
            var (manager, repo) = Create();
            repo.FailOnInsertNumber = 3;

            Assert.Throws<InvalidOperationException>(() => manager.PushMany(Commands(5)));

            // Two rows were written before the failure. Every row that exists must also be queued; otherwise
            // it sits as Queued in the database, invisible to the executor, until the next restart requeues it
            // (and a retry of the same batch would insert it a second time).
            Assert.AreEqual(2, repo.Inserted.Count);
            Assert.AreEqual(2, manager.All().Count);
        }
    }
}
