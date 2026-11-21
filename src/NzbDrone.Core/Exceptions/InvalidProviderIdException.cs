using System.Net;

namespace NzbDrone.Core.Exceptions
{
    public class InvalidProviderIdException : NzbDroneClientException
    {
        public string ProviderId { get; }

        public InvalidProviderIdException(string providerId, string message)
            : base(HttpStatusCode.BadRequest, message)
        {
            ProviderId = providerId;
        }

        public InvalidProviderIdException(string providerId, string message, params object[] args)
            : base(HttpStatusCode.BadRequest, message, args)
        {
            ProviderId = providerId;
        }
    }
}

