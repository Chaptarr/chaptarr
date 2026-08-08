using NzbDrone.Common.Exceptions;

namespace NzbDrone.Core.Exceptions
{
    public class BookNotFoundException : NzbDroneException
    {
        public string ProviderId { get; set; }

        public BookNotFoundException(string providerId)
            : base($"Book with id {providerId} was not found, it may have been removed from metadata server.")
        {
            ProviderId = providerId;
        }

        public BookNotFoundException(string providerId, string message, params object[] args)
            : base(message, args)
        {
            ProviderId = providerId;
        }

        public BookNotFoundException(string providerId, string message)
            : base(message)
        {
            ProviderId = providerId;
        }
    }
}
