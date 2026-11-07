using System.Collections.Generic;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Books.Calibre
{
    public class PushCalibreMetadataCommand : Command
    {
        public List<int> BookIds { get; set; } = new List<int>();

        public List<string> Fields { get; set; } = new List<string>();

        public override bool SendUpdatesToClient => true;
    }
}
