using System.Linq;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.ImportLists.Hardcover.Library
{
    public interface IHardcoverLibraryImportListStateRepository : IBasicRepository<HardcoverLibraryImportListState>
    {
        HardcoverLibraryImportListState GetByImportListId(int importListId);
    }

    public class HardcoverLibraryImportListStateRepository : BasicRepository<HardcoverLibraryImportListState>, IHardcoverLibraryImportListStateRepository
    {
        public HardcoverLibraryImportListStateRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        public HardcoverLibraryImportListState GetByImportListId(int importListId)
        {
            return Query(x => x.ImportListId == importListId).FirstOrDefault();
        }
    }
}

