using System.Collections.Generic;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Books
{
    public interface INarratorMetadataRepository : IBasicRepository<NarratorMetadata>
    {
        List<NarratorMetadata> FindById(List<string> foreignIds);
        List<NarratorMetadata> FindByProviderIds(IEnumerable<string> goodreadsNarratorIds, IEnumerable<string> hardcoverNarratorIds);
        List<NarratorMetadata> FindByGoodreadsNarratorIds(IEnumerable<string> goodreadsNarratorIds);
        List<NarratorMetadata> FindByHardcoverNarratorIds(IEnumerable<string> hardcoverNarratorIds);
        bool UpsertMany(List<NarratorMetadata> data);
    }
}
