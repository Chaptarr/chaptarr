using System.Linq;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Configuration
{
    public interface IProxyRepository : IBasicRepository<ProxyDefinition>
    {
        ProxyDefinition GetByName(string name);
    }

    public class ProxyRepository : BasicRepository<ProxyDefinition>, IProxyRepository
    {
        public ProxyRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        public ProxyDefinition GetByName(string name)
        {
            return Query(p => p.Name == name).SingleOrDefault();
        }
    }
}
