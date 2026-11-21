using NzbDrone.Common.Exceptions;

namespace NzbDrone.Core.Notifications.AudioBookShelf
{
    public class AudioBookShelfException : NzbDroneException
    {
        public AudioBookShelfException(string message)
            : base(message)
        {
        }

        public AudioBookShelfException(string message, params object[] args)
            : base(message, args)
        {
        }
    }
}
