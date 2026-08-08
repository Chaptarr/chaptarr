using NzbDrone.Common.Exceptions;

namespace NzbDrone.Core.Exceptions
{
    public class InvalidProviderIdException : NzbDroneException
    {
        public string ProviderId { get; }

        public InvalidProviderIdException(string providerId, string message)
            : base(message)
        {
            ProviderId = providerId;
        }

        public InvalidProviderIdException(string providerId, string message, params object[] args)
            : base(message, args)
        {
            ProviderId = providerId;
        }
    }
}

