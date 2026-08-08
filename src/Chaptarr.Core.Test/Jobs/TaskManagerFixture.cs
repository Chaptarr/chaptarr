using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Backup;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Configuration.Events;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Jobs;

namespace Chaptarr.Core.Test.Jobs
{
    [TestFixture]
    public class TaskManagerFixture
    {
        private class ScheduledTaskRepositoryProxy : DispatchProxy
        {
            public ScheduledTask Definition { get; set; }
            public Dictionary<Type, ScheduledTask> Definitions { get; } = new();
            public List<ScheduledTask> UpdatedTasks { get; } = new();
            public List<ScheduledTask> ExistingTasks { get; } = new();
            public List<ScheduledTask> UpsertedTasks { get; } = new();
            public int GetDefinitionCalls { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(IScheduledTaskRepository.GetDefinition))
                {
                    GetDefinitionCalls++;

                    if (Definitions.TryGetValue((Type)args[0], out var definition))
                    {
                        return definition;
                    }

                    if (Definition == null)
                    {
                        throw new InvalidOperationException("No scheduled task found");
                    }

                    return Definition;
                }

                if (targetMethod.Name == nameof(IScheduledTaskRepository.UpdateMany))
                {
                    UpdatedTasks.AddRange((IList<ScheduledTask>)args[0]);
                    return null;
                }

                if (targetMethod.Name == nameof(IScheduledTaskRepository.All))
                {
                    return ExistingTasks.ToList();
                }

                if (targetMethod.Name == nameof(IScheduledTaskRepository.Upsert))
                {
                    var task = (ScheduledTask)args[0];
                    UpsertedTasks.Add(task);
                    return task;
                }

                if (targetMethod.Name == nameof(IScheduledTaskRepository.Delete))
                {
                    return null;
                }

                throw new NotImplementedException($"Test proxy does not implement IScheduledTaskRepository.{targetMethod?.Name}");
            }
        }

        private class ConfigServiceProxy : DispatchProxy
        {
            public int RssSyncIntervalValue { get; set; } = 15;
            public int BackupIntervalValue { get; set; } = 7;

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    "get_RssSyncInterval" => RssSyncIntervalValue,
                    "get_BackupInterval" => BackupIntervalValue,
                    _ => throw new NotImplementedException($"Test proxy does not implement IConfigService.{targetMethod?.Name}")
                };
            }
        }

        [Test]
        public void should_reload_next_execution_from_repository_when_cache_is_empty()
        {
            var repository = DispatchProxy.Create<IScheduledTaskRepository, ScheduledTaskRepositoryProxy>();
            var repositoryProxy = (ScheduledTaskRepositoryProxy)(object)repository;
            var lastExecution = new DateTime(2026, 05, 10, 01, 08, 53, DateTimeKind.Utc);

            repositoryProxy.Definition = new ScheduledTask
            {
                Id = 11,
                TypeName = typeof(RssSyncCommand).FullName,
                Interval = 15,
                LastExecution = lastExecution
            };

            var subject = new TaskManager(repository, null, new CacheManager(), LogManager.GetCurrentClassLogger());

            var nextExecution = subject.GetNextExecution(typeof(RssSyncCommand));
            var cachedNextExecution = subject.GetNextExecution(typeof(RssSyncCommand));

            Assert.That(nextExecution, Is.EqualTo(lastExecution.AddMinutes(15)));
            Assert.That(cachedNextExecution, Is.EqualTo(nextExecution));
            Assert.That(repositoryProxy.GetDefinitionCalls, Is.EqualTo(1));
        }

        [Test]
        public void should_return_now_when_cache_and_repository_are_missing_task()
        {
            var repository = DispatchProxy.Create<IScheduledTaskRepository, ScheduledTaskRepositoryProxy>();
            var subject = new TaskManager(repository, null, new CacheManager(), LogManager.GetCurrentClassLogger());

            var before = DateTime.UtcNow;
            var nextExecution = subject.GetNextExecution(typeof(RssSyncCommand));
            var after = DateTime.UtcNow;

            Assert.That(nextExecution, Is.InRange(before, after.AddSeconds(1)));
        }

        [Test]
        public void should_schedule_mam_account_status_refresh_hourly()
        {
            var repository = DispatchProxy.Create<IScheduledTaskRepository, ScheduledTaskRepositoryProxy>();
            var repositoryProxy = (ScheduledTaskRepositoryProxy)(object)repository;
            var configService = DispatchProxy.Create<IConfigService, ConfigServiceProxy>();
            var subject = new TaskManager(repository, configService, new CacheManager(), LogManager.GetCurrentClassLogger());

            subject.Handle(new ApplicationStartedEvent());

            var mamTask = repositoryProxy.UpsertedTasks.Single(task =>
                task.TypeName == typeof(RefreshMyAnonaMouseAccountStatusCommand).FullName);
            Assert.That(mamTask.Interval, Is.EqualTo(60));
        }

        [Test]
        public void should_update_task_cache_when_config_is_saved_before_startup_cache_is_populated()
        {
            var repository = DispatchProxy.Create<IScheduledTaskRepository, ScheduledTaskRepositoryProxy>();
            var repositoryProxy = (ScheduledTaskRepositoryProxy)(object)repository;
            var configService = DispatchProxy.Create<IConfigService, ConfigServiceProxy>();
            var configProxy = (ConfigServiceProxy)(object)configService;
            var lastExecution = new DateTime(2026, 05, 10, 01, 08, 53, DateTimeKind.Utc);

            configProxy.RssSyncIntervalValue = 17;
            configProxy.BackupIntervalValue = 2;

            repositoryProxy.Definitions[typeof(RssSyncCommand)] = new ScheduledTask
            {
                Id = 11,
                TypeName = typeof(RssSyncCommand).FullName,
                Interval = 15,
                LastExecution = lastExecution
            };

            repositoryProxy.Definitions[typeof(BackupCommand)] = new ScheduledTask
            {
                Id = 12,
                TypeName = typeof(BackupCommand).FullName,
                Interval = 10080,
                LastExecution = lastExecution
            };

            var subject = new TaskManager(repository, configService, new CacheManager(), LogManager.GetCurrentClassLogger());

            subject.HandleAsync(new ConfigSavedEvent());

            var rssNextExecution = subject.GetNextExecution(typeof(RssSyncCommand));
            var backupNextExecution = subject.GetNextExecution(typeof(BackupCommand));

            Assert.That(rssNextExecution, Is.EqualTo(lastExecution.AddMinutes(17)));
            Assert.That(backupNextExecution, Is.EqualTo(lastExecution.AddMinutes(2 * 60 * 24)));
            Assert.That(repositoryProxy.UpdatedTasks.Select(t => t.TypeName), Is.EquivalentTo(new[] { typeof(RssSyncCommand).FullName, typeof(BackupCommand).FullName }));
            Assert.That(repositoryProxy.GetDefinitionCalls, Is.EqualTo(2));
        }
    }
}
