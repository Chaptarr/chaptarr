using NzbDrone.Core.Extras.Files;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;

namespace NzbDrone.Core.Extras.Metadata.Files
{
    public static class MetadataFileHash
    {
        public static string FileHash(MetadataFile metadata) => metadata?.Hash?.Split(':')[0];

        public static string SourceHash(MetadataFile metadata)
        {
            var separator = metadata?.Hash?.IndexOf(':') ?? -1;
            return separator >= 0 ? metadata.Hash.Substring(separator + 1) : null;
        }

        public static bool IsCurrent(MetadataFile metadata, string path, IDiskProvider diskProvider, bool binary)
        {
            var expected = FileHash(metadata);
            if (expected.IsNullOrWhiteSpace())
            {
                return false;
            }

            if (!binary)
            {
                return diskProvider.ReadAllText(path).SHA256Hash() == expected;
            }

            using (var stream = diskProvider.OpenReadStream(path))
            {
                return stream.SHA256Hash() == expected;
            }
        }
    }

    public class MetadataFile : ExtraFile
    {
        public string Hash { get; set; }
        public string Consumer { get; set; }
        public MetadataType Type { get; set; }
    }
}
