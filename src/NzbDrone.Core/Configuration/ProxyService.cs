using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Datastore.Events;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Configuration
{
    public interface IProxyService
    {
        List<ProxyDefinition> All();
        ProxyDefinition Find(int id);
        ProxyDefinition Get(int id);
        ProxyDefinition Add(ProxyDefinition proxy);
        ProxyDefinition Update(ProxyDefinition proxy);
        void Delete(int id);
    }

    public class ProxyService : IProxyService, IHandle<CommandExecutedEvent>
    {
        private readonly IProxyRepository _proxyRepository;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;

        public ProxyService(IProxyRepository proxyRepository,
                           IEventAggregator eventAggregator,
                           Logger logger)
        {
            _proxyRepository = proxyRepository;
            _eventAggregator = eventAggregator;
            _logger = logger;
        }

        public List<ProxyDefinition> All()
        {
            return _proxyRepository.All().ToList();
        }

        public ProxyDefinition Find(int id)
        {
            return _proxyRepository.Find(id);
        }

        public ProxyDefinition Get(int id)
        {
            return _proxyRepository.Get(id);
        }

        public ProxyDefinition Add(ProxyDefinition proxy)
        {
            var existingProxy = _proxyRepository.GetByName(proxy.Name);
            if (existingProxy != null)
            {
                throw new System.InvalidOperationException($"Proxy with name '{proxy.Name}' already exists.");
            }

            _logger.Info("Adding proxy: {0}", proxy.Name);
            var addedProxy = _proxyRepository.Insert(proxy);
            _eventAggregator.PublishEvent(new ProxyAddedEvent(addedProxy));
            return addedProxy;
        }

        public ProxyDefinition Update(ProxyDefinition proxy)
        {
            _logger.Info("Updating proxy: {0}", proxy.Name);
            var updatedProxy = _proxyRepository.Update(proxy);
            _eventAggregator.PublishEvent(new ProxyUpdatedEvent(updatedProxy));
            return updatedProxy;
        }

        public void Delete(int id)
        {
            var proxy = _proxyRepository.Get(id);
            _logger.Info("Deleting proxy: {0}", proxy.Name);
            _proxyRepository.Delete(id);
            _eventAggregator.PublishEvent(new ProxyDeletedEvent(proxy));
        }

        public void Handle(CommandExecutedEvent message)
        {
            // Reserved for future use
        }
    }

    public class ProxyAddedEvent : ModelEvent<ProxyDefinition>
    {
        public ProxyAddedEvent(ProxyDefinition model)
            : base(model, ModelAction.Created)
        {
        }
    }

    public class ProxyUpdatedEvent : ModelEvent<ProxyDefinition>
    {
        public ProxyUpdatedEvent(ProxyDefinition model)
            : base(model, ModelAction.Updated)
        {
        }
    }

    public class ProxyDeletedEvent : ModelEvent<ProxyDefinition>
    {
        public ProxyDeletedEvent(ProxyDefinition model)
            : base(model, ModelAction.Deleted)
        {
        }
    }
}
