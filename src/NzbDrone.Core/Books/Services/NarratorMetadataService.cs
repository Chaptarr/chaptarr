using System.Collections.Generic;

namespace NzbDrone.Core.Books
{
    public class NarratorMetadataService : INarratorMetadataService
    {
        private readonly INarratorMetadataRepository _narratorMetadataRepository;

        public NarratorMetadataService(INarratorMetadataRepository narratorMetadataRepository)
        {
            _narratorMetadataRepository = narratorMetadataRepository;
        }

        public NarratorMetadata Upsert(NarratorMetadata narrator)
        {
            return _narratorMetadataRepository.Upsert(narrator);
        }

        public bool UpsertMany(List<NarratorMetadata> narrators)
        {
            return _narratorMetadataRepository.UpsertMany(narrators);
        }
    }
}
