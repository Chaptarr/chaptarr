using System;
using System.Text.Json.Serialization;
using Chaptarr.Http.REST;
using NzbDrone.Core.Datastore.Events;

namespace Chaptarr.Http
{
    public class ResourceChangeMessage<TResource>
        where TResource : RestResource
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public TResource Resource { get; private set; }
        public ModelAction Action { get; private set; }

        public ResourceChangeMessage(ModelAction action)
        {
            if (action != ModelAction.Deleted && action != ModelAction.Sync)
            {
                throw new InvalidOperationException("Resource message without a resource needs to have Delete or Sync as action");
            }

            Action = action;
        }

        public ResourceChangeMessage(TResource resource, ModelAction action)
        {
            if (resource == null && action != ModelAction.Deleted && action != ModelAction.Sync)
            {
                throw new InvalidOperationException($"Resource change message for '{action}' requires a non-null resource");
            }

            Resource = resource;
            Action = action;
        }
    }
}
