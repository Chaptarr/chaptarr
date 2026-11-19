using NLog;
using NzbDrone.Core.Indexers.Newznab;

namespace NzbDrone.Core.Indexers.Torznab
{
    public class TorznabRequestGenerator : NewznabRequestGenerator
    {
        public TorznabRequestGenerator(INewznabCapabilitiesProvider capabilitiesProvider, Logger logger, string indexerName)
        : base(capabilitiesProvider, logger, indexerName)
        {
        }
    }
}
