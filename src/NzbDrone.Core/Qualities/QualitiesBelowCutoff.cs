using System.Collections.Generic;
using NzbDrone.Core.Profiles.Qualities;

namespace NzbDrone.Core.Qualities
{
    public class QualitiesBelowCutoff
    {
        public int ProfileId { get; set; }
        public ProfileType ProfileType { get; set; }
        public IEnumerable<int> QualityIds { get; set; }

        public QualitiesBelowCutoff(int profileId, ProfileType profileType, IEnumerable<int> qualityIds)
        {
            ProfileId = profileId;
            ProfileType = profileType;
            QualityIds = qualityIds;
        }
    }
}
