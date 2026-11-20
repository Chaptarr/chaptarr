using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.MediaFiles.Commands
{
    /// <summary>
    /// Imports the finished library conversion of one edition: the converted file replaces the
    /// library files it was made from. Queued by the conversion worker when a library job is
    /// ready, and again by its scheduler after a restart. One command per edition; pushing the
    /// same edition while one is queued or running is a no-op.
    /// </summary>
    public class ImportLibraryConversionCommand : Command
    {
        public int EditionId { get; set; }

        public override bool RequiresDiskAccess => true;

        public ImportLibraryConversionCommand()
        {
        }

        public ImportLibraryConversionCommand(int editionId)
        {
            EditionId = editionId;
        }
    }
}
