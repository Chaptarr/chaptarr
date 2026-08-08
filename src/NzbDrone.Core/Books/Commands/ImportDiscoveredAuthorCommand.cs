using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Books.Commands
{
    public class ImportDiscoveredAuthorCommand : Command
    {
        // Provider-prefixed author ID (e.g., "hc:80626")
        public string ProviderId { get; set; }

        // Root folder context where author was discovered (path or identifier string)
        public string RootFolderPath { get; set; }

        // Optional: best-guess author folder path to preserve structure
        public string DiscoveredAuthorFolderPath { get; set; }

        // Optional: a representative file path sampled during discovery
        // NOTE: For mixed roots we APPLY BOTH SIDES; this path is not used to choose sides
        public string SampleFilePath { get; set; }

        // Optional: audit/tracking
        public string RequestedBy { get; set; }

        public override bool IsExclusive => true; // single-writer semantics

        public override string ToString()
        {
            return $"ImportDiscoveredAuthorCommand [providerId={ProviderId}, root='{RootFolderPath}', folder='{DiscoveredAuthorFolderPath}']";
        }
    }
}

