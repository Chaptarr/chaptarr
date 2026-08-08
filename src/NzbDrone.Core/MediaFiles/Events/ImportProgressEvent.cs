using NzbDrone.Common.Messaging;

namespace NzbDrone.Core.MediaFiles.Events
{
    public abstract class ImportProgressEvent : IEvent
    {
        public string FolderPath { get; set; }

        protected ImportProgressEvent(string folderPath)
        {
            FolderPath = folderPath;
        }
    }
}
