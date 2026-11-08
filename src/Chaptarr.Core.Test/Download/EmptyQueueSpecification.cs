using System;
using System.Collections.Generic;
using Chaptarr.Core.Test.Common;
using NLog;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Queue;

namespace Chaptarr.Core.Test.Download
{
    internal sealed class EmptyQueueSpecification : QueueSpecification
    {
        public EmptyQueueSpecification()
            : base(InterfaceProxy.Proxy<IQueueService>((method, args) => method.Name switch
            {
                nameof(IQueueService.GetQueue) => new List<NzbDrone.Core.Queue.Queue>(),
                _ => throw new NotImplementedException(method.Name)
            }), null, null, null, LogManager.GetCurrentClassLogger())
        {
        }
    }
}
