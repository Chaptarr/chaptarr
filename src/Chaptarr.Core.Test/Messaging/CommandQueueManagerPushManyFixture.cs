using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Composition;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Messaging.Commands;

namespace Chaptarr.Core.Test.Messaging
{
    [TestFixture]
    public class CommandQueueManagerPushManyFixture
    {
        private class CommandRepositoryProxy : DispatchProxy
        {
            public List<CommandModel> Inserted { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(ICommandRepository.InsertMany) && args.Length == 1)
                {
                    foreach (var model in (IList<CommandModel>)args[0])
                    {
                        model.Id = Inserted.Count + 1;
                        Inserted.Add(model);
                    }

                    return null;
                }

                throw new NotImplementedException($"Test proxy does not implement ICommandRepository.{targetMethod.Name}");
            }
        }

        private CommandRepositoryProxy _repo;
        private CommandQueueManager _subject;

        [SetUp]
        public void SetUp()
        {
            var repo = DispatchProxy.Create<ICommandRepository, CommandRepositoryProxy>();
            _repo = (CommandRepositoryProxy)(object)repo;
            _subject = new CommandQueueManager(repo, null, new KnownTypes(), LogManager.GetCurrentClassLogger());
        }

        [Test]
        public void should_queue_an_identical_command_only_once_within_one_batch()
        {
            var result = _subject.PushMany(new List<AuthorSearchCommand>
            {
                new AuthorSearchCommand { AuthorId = 7 },
                new AuthorSearchCommand { AuthorId = 7 }
            });

            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(_repo.Inserted, Has.Count.EqualTo(1));
            Assert.That(_subject.All(), Has.Count.EqualTo(1));
        }

        [Test]
        public void should_queue_each_distinct_command_in_a_batch()
        {
            var result = _subject.PushMany(new List<AuthorSearchCommand>
            {
                new AuthorSearchCommand { AuthorId = 7 },
                new AuthorSearchCommand { AuthorId = 8 },
                new AuthorSearchCommand { AuthorId = 7 }
            });

            Assert.That(result.Select(c => ((AuthorSearchCommand)c.Body).AuthorId), Is.EquivalentTo(new[] { 7, 8 }));
            Assert.That(_subject.All(), Has.Count.EqualTo(2));
        }

        [Test]
        public void should_skip_a_command_that_is_already_queued()
        {
            _subject.PushMany(new List<AuthorSearchCommand> { new AuthorSearchCommand { AuthorId = 7 } });

            var result = _subject.PushMany(new List<AuthorSearchCommand> { new AuthorSearchCommand { AuthorId = 7 } });

            Assert.That(result, Is.Empty);
            Assert.That(_subject.All(), Has.Count.EqualTo(1));
        }
    }
}
