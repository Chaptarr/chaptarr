using System.Collections.Generic;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Notifications.Grimmory
{
    public class PushGrimmoryMetadataCommand : Command
    {
        public List<int> BookIds { get; set; } = new List<int>();

        public List<string> Fields { get; set; } = new List<string>();

        // Grimmory's refresh is async, so a push queued right after an import has to wait
        // for the book to appear.
        public bool WaitForBook { get; set; }

        public override bool SendUpdatesToClient => true;
    }
}
