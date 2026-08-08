using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Http;
using Chaptarr.Http.REST;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Datastore.Events;
using NzbDrone.Core.Jobs;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.SignalR;

namespace Chaptarr.Api.V1.System.Tasks
{
    [V1ApiController("system/task")]
    public class TaskController : RestControllerWithSignalR<TaskResource, ScheduledTask>, IHandle<CommandExecutedEvent>
    {
        private readonly ITaskManager _taskManager;

        public TaskController(ITaskManager taskManager, IBroadcastSignalRMessage broadcastSignalRMessage)
            : base(broadcastSignalRMessage)
        {
            _taskManager = taskManager;
        }

        [HttpGet]
        public List<TaskResource> GetAll()
        {
            return _taskManager.GetAll()
                               .Select(ConvertToResource)
                               .OrderBy(t => t.Name)
                               .ToList();
        }

        protected override TaskResource GetResourceById(int id)
        {
            var task = _taskManager.GetAll()
                               .SingleOrDefault(t => t.Id == id);

            if (task == null)
            {
                return null;
            }

            return ConvertToResource(task);
        }

        private static TaskResource ConvertToResource(ScheduledTask scheduledTask)
        {
            var taskName = scheduledTask.TypeName.Split('.').Last().Replace("Command", "");
            var lastStartTime = scheduledTask.LastStartTime == DateTime.MinValue ? scheduledTask.LastExecution : scheduledTask.LastStartTime;
            var nextExecution = scheduledTask.LastExecution;
            try
            {
                nextExecution = scheduledTask.LastExecution.AddMinutes(scheduledTask.Interval);
            }
            catch (ArgumentOutOfRangeException)
            {
                // Guard against corrupted/edge-case timestamps (e.g., infinity conversions) that
                // would otherwise break the entire scheduled tasks endpoint.
            }

            return new TaskResource
            {
                Id = scheduledTask.Id,
                Name = taskName.SplitCamelCase(),
                TaskName = taskName,
                Interval = scheduledTask.Interval,
                LastExecution = scheduledTask.LastExecution,
                LastStartTime = lastStartTime,
                NextExecution = nextExecution
            };
        }

        [NonAction]
        public void Handle(CommandExecutedEvent message)
        {
            BroadcastResourceChange(ModelAction.Sync);
        }
    }
}
