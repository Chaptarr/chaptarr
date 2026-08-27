using NzbDrone.Common.Exceptions;

namespace NzbDrone.Core.Exceptions
{
    public class PendingBookRequestException : NzbDroneException
    {
        public const string UserMessage = "The author and requested book are being prepared by the metadata server. Chaptarr saved your request and will add it automatically when it becomes available.";

        public int PendingId { get; }

        public PendingBookRequestException(int pendingId)
            : base(UserMessage)
        {
            PendingId = pendingId;
        }
    }
}
