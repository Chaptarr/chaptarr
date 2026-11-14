using System.Text.Json.Serialization;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.MediaFiles.Commands
{
    public abstract class RetagCommand : Command
    {
        public bool UpdateCovers { get; set; }
        public bool EmbedMetadata { get; set; }
        [JsonIgnore]
        public string ResultMessage { get; set; }
        public override string CompletionMessage => ResultMessage;
        public override bool SendUpdatesToClient => true;
        public override bool RequiresDiskAccess => true;
    }
}
