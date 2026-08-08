using NzbDrone.Core.Datastore;
using NzbDrone.Core.Books;

namespace NzbDrone.Core.ImportLists.Exclusions
{
    public class ImportListExclusion : ModelBase
    {
        public string ForeignId { get; set; }
        public string Name { get; set; }
        public BookMediaType? MediaType { get; set; }
    }
}
