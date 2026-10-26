using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Chaptarr.Api.V1.Indexers;
using System;
using System.Reflection;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.SignalR;

namespace Chaptarr.Core.Test.Common
{
    internal sealed class GroupedAuthorSearchDriver
    {
        public IManageCommandQueue Queue { get; }
        public Exception EnqueueFailure { get; set; }
        public GroupedAuthorSearchService Service { get; }
        public ConcurrentDictionary<string, InteractiveAuthorSearchCommand> Commands { get; } = new();
        public ConcurrentDictionary<string, Task> Executions { get; } = new();

        public GroupedAuthorSearchDriver(ISearchForReleases search, IPrioritizeDownloadDecision prioritizer,
            IBroadcastSignalRMessage broadcaster, ICacheManager cache, bool autoStart = false)
        {
            var logger = LogManager.GetCurrentClassLogger();
            Queue = DispatchProxy.Create<IManageCommandQueue, QueueProxy>();
            Service = new GroupedAuthorSearchService(search, prioritizer, Queue,
                new InteractiveReleaseMapper(cache, logger), broadcaster, cache, logger);
            ((QueueProxy)(object)Queue).Driver = this;
            ((QueueProxy)(object)Queue).AutoStart = autoStart;
        }

        private class QueueProxy : DispatchProxy
        {
            public GroupedAuthorSearchDriver Driver { get; set; }
            public bool AutoStart { get; set; }

            protected override object Invoke(MethodInfo method, object[] args)
            {
                if (method.Name != nameof(IManageCommandQueue.Push))
                {
                    throw new NotImplementedException(method.Name);
                }

                if (Driver.EnqueueFailure != null)
                {
                    throw Driver.EnqueueFailure;
                }

                var command = (InteractiveAuthorSearchCommand)args[0];
                NUnit.Framework.Assert.That(args[1], NUnit.Framework.Is.EqualTo(CommandPriority.High));
                NUnit.Framework.Assert.That(args[2], NUnit.Framework.Is.EqualTo(CommandTrigger.Manual));
                Driver.Commands[command.SearchId] = command;
                if (AutoStart)
                {
                    Driver.Executions[command.SearchId] = Task.Run(() => Driver.Service.Run(command, CancellationToken.None));
                }

                return new CommandModel { Id = Driver.Commands.Count, Body = command };
            }
        }

        public Task Run(string searchId)
        {
            return Service.Run(Commands[searchId], CancellationToken.None);
        }
    }
}
