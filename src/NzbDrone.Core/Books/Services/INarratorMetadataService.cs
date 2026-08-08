using System.Collections.Generic;

namespace NzbDrone.Core.Books
{
    public interface INarratorMetadataService
    {
        NarratorMetadata Upsert(NarratorMetadata narrator);
        bool UpsertMany(List<NarratorMetadata> narrators);
    }
}
