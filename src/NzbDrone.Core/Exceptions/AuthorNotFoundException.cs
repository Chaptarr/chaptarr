using NzbDrone.Common.Exceptions;

namespace NzbDrone.Core.Exceptions
{
    public class AuthorNotFoundException : NzbDroneException
    {
        public string ProviderId { get; set; }

        public AuthorNotFoundException(string providerId)
            : base($"Author with id {providerId} was not found, it may have been removed from the metadata server.")
        {
            ProviderId = providerId;
        }

        public AuthorNotFoundException(string providerId, string message, params object[] args)
            : base(message, args)
        {
            ProviderId = providerId;
        }

        public AuthorNotFoundException(string providerId, string message)
            : base(message)
        {
            ProviderId = providerId;
        }
    }
}
