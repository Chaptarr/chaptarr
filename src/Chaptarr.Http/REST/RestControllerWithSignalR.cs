using System;
using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.SignalR;

namespace Chaptarr.Http.REST
{
    public abstract class RestControllerWithSignalR<TResource, TModel> : RestController<TResource>, IHandle<ModelEvent<TModel>>
        where TResource : RestResource, new()
        where TModel : ModelBase, new()
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private static readonly ConcurrentDictionary<string, DateTime> BroadcastLookupLogTimestamps = new ConcurrentDictionary<string, DateTime>();
        private static readonly TimeSpan BroadcastLookupLogThrottle = TimeSpan.FromMinutes(1);
        protected string Resource { get; }
        private readonly IBroadcastSignalRMessage _signalRBroadcaster;
        private readonly bool _shouldBroadcastResourceChanges;

        protected RestControllerWithSignalR(IBroadcastSignalRMessage signalRBroadcaster)
        {
            _signalRBroadcaster = signalRBroadcaster;

            var apiAttribute = GetType().GetCustomAttribute<VersionedApiControllerAttribute>(inherit: true);
            _shouldBroadcastResourceChanges = apiAttribute != null;
            if (apiAttribute != null && apiAttribute.Resource != VersionedApiControllerAttribute.CONTROLLER_RESOURCE)
            {
                Resource = apiAttribute.Resource;
            }
            else
            {
                Resource = new TResource().ResourceName.Trim('/');
            }
        }

        protected virtual TResource GetResourceByIdForBroadcast(int id)
        {
            return GetResourceById(id);
        }

        [NonAction]
        public void Handle(ModelEvent<TModel> message)
        {
            if (!_signalRBroadcaster.IsConnected)
            {
                return;
            }

            if (message.Action == ModelAction.Sync)
            {
                BroadcastResourceChange(message.Action);
                return;
            }

            if (message.Action == ModelAction.Deleted)
            {
                BroadcastResourceChange(message.Action);
                BroadcastResourceChange(message.Action, message.ModelId);
                return;
            }

            BroadcastResourceChange(message.Action, message.ModelId);
        }

        private bool ShouldBroadcastResourceChanges()
        {
            return _shouldBroadcastResourceChanges;
        }

        private void LogBroadcastLookupFallback(ModelAction action, int id, Exception exception, string reason)
        {
            try
            {
                var now = DateTime.UtcNow;
                var key = $"{Resource}|{action}|{reason}|{exception?.GetType().Name ?? "none"}";
                var last = BroadcastLookupLogTimestamps.GetOrAdd(key, DateTime.MinValue);

                if ((now - last) < BroadcastLookupLogThrottle)
                {
                    return;
                }

                BroadcastLookupLogTimestamps[key] = now;

                if (exception == null)
                {
                    Logger.Debug("SignalR broadcast fallback: resource '{0}' {1} id={2} ({3}); broadcasting Sync instead", Resource, action, id, reason);
                }
                else
                {
                    Logger.Debug(exception, "SignalR broadcast fallback: resource '{0}' {1} id={2} ({3}); broadcasting Sync instead", Resource, action, id, reason);
                }
            }
            catch
            {
                // best-effort only
            }
        }

        protected void BroadcastResourceChange(ModelAction action, int id)
        {
            if (!_signalRBroadcaster.IsConnected)
            {
                return;
            }

            if (!ShouldBroadcastResourceChanges())
            {
                return;
            }

            if (action == ModelAction.Deleted)
            {
                BroadcastResourceChange(action, new TResource { Id = id });
            }
            else if (action == ModelAction.Sync)
            {
                BroadcastResourceChange(action);
            }
            else
            {
                TResource resource;
                try
                {
                    resource = GetResourceByIdForBroadcast(id);
                }
                catch (ModelNotFoundException ex)
                {
                    LogBroadcastLookupFallback(action, id, ex, "not found");
                    BroadcastResourceChange(ModelAction.Sync);
                    return;
                }
                catch (Exception ex)
                {
                    LogBroadcastLookupFallback(action, id, ex, "exception");
                    BroadcastResourceChange(ModelAction.Sync);
                    return;
                }

                if (resource == null)
                {
                    LogBroadcastLookupFallback(action, id, null, "returned null");
                    BroadcastResourceChange(ModelAction.Sync);
                    return;
                }

                BroadcastResourceChange(action, resource);
            }
        }

        protected void BroadcastResourceChange(ModelAction action, TResource resource)
        {
            if (!_signalRBroadcaster.IsConnected)
            {
                return;
            }

            if (ShouldBroadcastResourceChanges())
            {
                var signalRMessage = new SignalRMessage
                {
                    Name = Resource,
                    Body = new ResourceChangeMessage<TResource>(resource, action),
                    Action = action
                };

                _signalRBroadcaster.BroadcastMessage(signalRMessage);
            }
        }

        protected void BroadcastResourceChange(ModelAction action)
        {
            if (!_signalRBroadcaster.IsConnected)
            {
                return;
            }

            if (ShouldBroadcastResourceChanges())
            {
                var signalRMessage = new SignalRMessage
                {
                    Name = Resource,
                    Body = new ResourceChangeMessage<TResource>(action),
                    Action = action
                };

                _signalRBroadcaster.BroadcastMessage(signalRMessage);
            }
        }
    }
}
