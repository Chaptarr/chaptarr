using System.Collections.Generic;

namespace NzbDrone.Core.Books
{
    public interface INarratorService
    {
        Narrator GetNarrator(int narratorId);
        Narrator GetNarratorByMetadataId(int narratorMetadataId);
        List<Narrator> GetNarrators(IEnumerable<int> narratorIds);
        Narrator AddNarrator(Narrator newNarrator);
        List<Narrator> AddNarrators(List<Narrator> newNarrators);
        Narrator FindById(string foreignNarratorId);
        Narrator FindByName(string name);
        Narrator FindByNameInexact(string name);
        Narrator FindByNarratorTitleSlug(string narratorTitleSlug);
        List<Narrator> GetCandidates(string name);
        List<Narrator> GetReportCandidates(string reportName);
        void DeleteNarrator(int narratorId);
        List<Narrator> GetAllNarrators();
        Dictionary<int, List<int>> GetAllNarratorTags();
        List<Narrator> AllForTag(int tagId);
        Narrator UpdateNarrator(Narrator narrator);
        List<Narrator> UpdateNarrators(List<Narrator> narrators);
    }
}
