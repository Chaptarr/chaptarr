using System.Collections.Generic;

namespace NzbDrone.Core.Notifications.Plex.PlexTv
{
    public class PlexTvResourceConnection
    {
        public string Protocol { get; set; }
        public string Address { get; set; }
        public int Port { get; set; }
        public string Uri { get; set; }
        public bool Local { get; set; }
        public bool Relay { get; set; }
    }

    public class PlexTvResourceResponse
    {
        public string Name { get; set; }
        public string Product { get; set; }
        public string Provides { get; set; }
        public string ClientIdentifier { get; set; }
        public List<PlexTvResourceConnection> Connections { get; set; }
    }
}

