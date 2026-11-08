using System.Collections.Generic;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Books
{
    public interface INarratorRepository : IBasicRepository<Narrator>
    {
        List<Narrator> FindByCleanNames(IEnumerable<string> cleanNames);
        List<Narrator> GetNarratorsByMetadataId(IEnumerable<int> narratorMetadataIds);
    }
}
