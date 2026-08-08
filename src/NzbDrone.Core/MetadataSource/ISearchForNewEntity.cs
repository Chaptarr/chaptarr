using System.Collections.Generic;

namespace NzbDrone.Core.MetadataSource
{
    public interface ISearchForNewEntity
    {
        List<object> SearchForNewEntity(string title);
        List<object> SearchForNewEntity(string title, string provider);
    }
}
