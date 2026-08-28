using System.Linq;
using System.Xml.Linq;
using NzbDrone.Core.Download.Extensions;

namespace NzbDrone.Core.Download.Clients.RTorrent
{
    public class RTorrentFile
    {
        public RTorrentFile()
        {
        }

        public RTorrentFile(XElement element)
        {
            var data = element.Descendants("value").ToList();

            Path = data.ElementAtOrDefault(0)?.GetStringValue();
            FrozenPath = data.ElementAtOrDefault(1)?.GetStringValue();
            Priority = data.ElementAtOrDefault(2)?.GetLongValue() ?? 0;
        }

        public string Path { get; set; }
        public string FrozenPath { get; set; }
        public long Priority { get; set; }
    }
}
