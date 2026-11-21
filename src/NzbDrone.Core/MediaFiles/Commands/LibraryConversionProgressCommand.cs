using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.MediaFiles.Commands
{
    /// <summary>
    /// Shows the progress of library conversions in System > Tasks while any are queued, running
    /// or waiting to import, and cancels them when the task is cancelled. It only reads the
    /// conversion jobs: encoding runs on the conversion worker and importing runs as
    /// <see cref="ImportLibraryConversionCommand"/>, so this task never holds disk access and one
    /// instance covers every library conversion.
    /// </summary>
    public class LibraryConversionProgressCommand : Command
    {
        public override bool SendUpdatesToClient => true;
        public override bool UpdateScheduledTask => false;
        public override bool SupportsCancellation => true;
    }
}
