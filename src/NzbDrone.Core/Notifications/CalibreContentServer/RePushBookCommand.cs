using System.Collections.Generic;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Notifications.CalibreContentServer
{
    public class RePushBookCommand : Command
    {
        public int BookId { get; set; }

        public List<int> BookIds { get; set; }

        public bool FromLibraryEdit { get; set; }

        public override bool SendUpdatesToClient => true;
    }
}
