using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Backup;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Configuration.Events;
using NzbDrone.Core.Download;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Core.Housekeeping;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Hardcover.Library;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.MediaCover.Commands;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Update.Commands;

namespace NzbDrone.Core.Jobs
{
    public interface ITaskManager
    {
        IList<ScheduledTask> GetPending();
        List<ScheduledTask> GetAll();
        DateTime GetNextExecution(Type type);
    }

    public class TaskManager : ITaskManager, IHandle<ApplicationStartedEvent>, IHandle<CommandExecutedEvent>, IHandleAsync<ConfigSavedEvent>
    {
        private readonly IScheduledTaskRepository _scheduledTaskRepository;
        private readonly IConfigService _configService;
        private readonly Logger _logger;
        private readonly ICached<ScheduledTask> _cache;

        public TaskManager(IScheduledTaskRepository scheduledTaskRepository, IConfigService configService, ICacheManager cacheManager, Logger logger)
        {
            _scheduledTaskRepository = scheduledTaskRepository;
            _configService = configService;
            _cache = cacheManager.GetCache<ScheduledTask>(GetType());
            _logger = logger;
        }

        public IList<ScheduledTask> GetPending()
        {
            var now = DateTime.UtcNow;

            return _cache.Values
                         .Where(task => task.Interval > 0 && IsTaskPending(task, now))
                         .ToList();
        }

        public List<ScheduledTask> GetAll()
        {
            return _cache.Values.ToList();
        }

        public DateTime GetNextExecution(Type type)
        {
            var scheduledTask = GetScheduledTask(type);

            if (scheduledTask == null)
            {
                _logger.Warn("Scheduled task '{0}' was requested before it was initialized; returning now as next execution", type.FullName);
                return DateTime.UtcNow;
            }

            try
            {
                return scheduledTask.LastExecution.AddMinutes(scheduledTask.Interval);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                _logger.Warn(ex, "Invalid scheduled task timestamps for '{0}'; returning now as next execution", scheduledTask.TypeName ?? type.FullName);
                return DateTime.UtcNow;
            }
        }

        private ScheduledTask GetScheduledTask(Type type)
        {
            var typeName = type.FullName;
            var scheduledTask = _cache.Find(typeName);

            if (scheduledTask != null)
            {
                return scheduledTask;
            }

            try
            {
                scheduledTask = _scheduledTaskRepository.GetDefinition(type);
            }
            catch (InvalidOperationException ex)
            {
                _logger.Warn(ex, "Scheduled task '{0}' was not found in the database while rebuilding the task cache", typeName);
                return null;
            }

            _cache.Set(typeName, scheduledTask);

            return scheduledTask;
        }

        public void Handle(ApplicationStartedEvent message)
        {
            var defaultTasks = new List<ScheduledTask>
                {
                    new ScheduledTask
                    {
                        Interval = 1,
                        TypeName = typeof(RefreshMonitoredDownloadsCommand).FullName,
                        Priority = CommandPriority.High
                    },

                    new ScheduledTask
                    {
                        Interval = 5,
                        TypeName = typeof(MessagingCleanupCommand).FullName
                    },

                    new ScheduledTask
                    {
                        Interval = 6 * 60,
                        TypeName = typeof(ApplicationUpdateCheckCommand).FullName
                    },

                    new ScheduledTask
                    {
                        Interval = 6 * 60,
                        TypeName = typeof(CheckHealthCommand).FullName
                    },

                    new ScheduledTask
                    {
                        Interval = 60,
                        TypeName = typeof(RefreshMyAnonaMouseAccountStatusCommand).FullName
                    },

                    new ScheduledTask
                    {
                        Interval = 24 * 60,
                        TypeName = typeof(RefreshAuthorCommand).FullName
                    },

                    new ScheduledTask
                    {
                        Interval = 24 * 60,
                        TypeName = typeof(RescanFoldersCommand).FullName
                    },

                    new ScheduledTask
                    {
                        Interval = 24 * 60,
                        TypeName = typeof(HousekeepingCommand).FullName
                    },

                    new ScheduledTask
                    {
                        Interval = 24 * 60,
                        TypeName = typeof(RepairAuthorMediaCoversCommand).FullName,
                        Priority = CommandPriority.Low
                    },

                    new ScheduledTask
                    {
                        Interval = GetBackupInterval(),
                        TypeName = typeof(BackupCommand).FullName
                    },

                    new ScheduledTask
                    {
                        Interval = 5,
                        TypeName = typeof(ImportListSyncCommand).FullName
                    },

                    new ScheduledTask
                    {
                        Interval = 60,
                        TypeName = typeof(HardcoverLibrarySyncCommand).FullName
                    },

                    new ScheduledTask
                    {
                        Interval = GetRssSyncInterval(),
                        TypeName = typeof(RssSyncCommand).FullName
                    },

                    new ScheduledTask
                    {
                        Interval = 1, // Run every minute
                        TypeName = typeof(ProcessPendingImportsCommand).FullName,
                        Priority = CommandPriority.Low
                    }
                };

            List<ScheduledTask> currentTasks;
            try
            {
                currentTasks = _scheduledTaskRepository.All().ToList();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to load scheduled tasks from database; using default task definitions");
                currentTasks = new List<ScheduledTask>();
            }

            _logger.Trace("Initializing jobs. Available: {0} Existing: {1}", defaultTasks.Count, currentTasks.Count);

            foreach (var job in currentTasks)
            {
                if (!defaultTasks.Any(c => c.TypeName == job.TypeName))
                {
                    _logger.Trace("Removing job from database '{0}'", job.TypeName);
                    _scheduledTaskRepository.Delete(job.Id);
                }
            }

            foreach (var defaultTask in defaultTasks)
            {
                var currentDefinition = currentTasks.SingleOrDefault(c => c.TypeName == defaultTask.TypeName) ?? defaultTask;

                currentDefinition.Interval = defaultTask.Interval;

                if (currentDefinition.Id == 0)
                {
                    currentDefinition.LastExecution = DateTime.UtcNow;
                    currentDefinition.LastStartTime = currentDefinition.LastExecution;
                }

                if (currentDefinition.LastExecution == default(DateTime))
                {
                    currentDefinition.LastExecution = DateTime.UtcNow;
                }

                if (currentDefinition.LastStartTime == default(DateTime) || currentDefinition.LastStartTime > currentDefinition.LastExecution)
                {
                    currentDefinition.LastStartTime = currentDefinition.LastExecution;
                }

                currentDefinition.Priority = defaultTask.Priority;

                _cache.Set(currentDefinition.TypeName, currentDefinition);
                _scheduledTaskRepository.Upsert(currentDefinition);
            }
        }

        private int GetBackupInterval()
        {
            var interval = _configService.BackupInterval;

            if (interval < 1)
            {
                interval = 1;
            }

            return interval * 60 * 24;
        }

        private int GetRssSyncInterval()
        {
            var interval = _configService.RssSyncInterval;

            if (interval > 0 && interval < 10)
            {
                return 10;
            }

            if (interval < 0)
            {
                return 0;
            }

            return interval;
        }

        public void Handle(CommandExecutedEvent message)
        {
            var typeName = message.Command.Body.GetType().FullName;
            var scheduledTask = _cache.Find(typeName);

            if (scheduledTask != null && message.Command.Body.UpdateScheduledTask)
            {
                _logger.Trace("Updating last run time for: {0}", scheduledTask.TypeName);

                var lastExecution = DateTime.UtcNow;
                var startTime = message.Command.StartedAt.Value;

                _scheduledTaskRepository.SetLastExecutionTime(scheduledTask.Id, lastExecution, startTime);

                scheduledTask.LastExecution = lastExecution;
                scheduledTask.LastStartTime = startTime;
            }
        }

        private bool IsTaskPending(ScheduledTask task, DateTime now)
        {
            try
            {
                return task.LastExecution.AddMinutes(task.Interval) < now;
            }
            catch (ArgumentOutOfRangeException ex)
            {
                _logger.Warn(ex, "Invalid scheduled task timestamps for '{0}'; forcing task to run", task.TypeName);
                return true;
            }
        }

        public void HandleAsync(ConfigSavedEvent message)
        {
            var rss = _scheduledTaskRepository.GetDefinition(typeof(RssSyncCommand));
            rss.Interval = GetRssSyncInterval();

            var backup = _scheduledTaskRepository.GetDefinition(typeof(BackupCommand));
            backup.Interval = GetBackupInterval();

            _scheduledTaskRepository.UpdateMany(new List<ScheduledTask> { rss, backup });

            _cache.Set(rss.TypeName, rss);
            _cache.Set(backup.TypeName, backup);
        }
    }
}
