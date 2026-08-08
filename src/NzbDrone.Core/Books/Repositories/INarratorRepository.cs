using System.Collections.Generic;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Books
{
    public interface INarratorRepository : IBasicRepository<Narrator>
    {
        bool NarratorPathExists(string path);
        Narrator FindByName(string cleanName);
        List<Narrator> FindByCleanNames(IEnumerable<string> cleanNames);
        Narrator FindById(string foreignNarratorId);
        Narrator FindByNarratorTitleSlug(string narratorTitleSlug);
        Dictionary<int, string> AllNarratorPaths();
        Dictionary<int, List<int>> AllNarratorTags();
        Narrator GetNarratorByMetadataId(int narratorMetadataId);
        List<Narrator> GetNarratorsByMetadataId(IEnumerable<int> narratorMetadataIds);
    }
}
