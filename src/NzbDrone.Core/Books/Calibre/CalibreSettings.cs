using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Books.Calibre
{
    public class CalibreSettings : IEmbeddedDocument
    {
        public CalibreSettings()
        {
            Port = 8080;
        }

        public string Host { get; set; }
        public int Port { get; set; }
        public string UrlBase { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public string Library { get; set; }
        public string OutputFormat { get; set; }
        public int OutputProfile { get; set; }
        public bool UseSsl { get; set; }
    }
}
