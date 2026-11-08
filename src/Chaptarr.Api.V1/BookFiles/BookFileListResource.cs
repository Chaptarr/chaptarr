using System.Collections.Generic;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Api.V1.BookFiles
{
    public class BookFileListResource
    {
        public List<int> BookFileIds { get; set; }
        private bool? _audioProductionOverride;
        public bool? AudioProductionOverride
        {
            get => _audioProductionOverride;
            set { _audioProductionOverride = value; AudioProductionOverrideSpecified = true; }
        }

        [global::System.Text.Json.Serialization.JsonIgnore]
        public bool AudioProductionOverrideSpecified { get; private set; }

        public QualityModel Quality { get; set; }
    }
}
