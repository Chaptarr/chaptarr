using System;
using System.Net;
using NzbDrone.Core.Exceptions;

namespace NzbDrone.Core.MetadataSource.Audible
{
    public class AudibleCatalogException : NzbDroneClientException
    {
        public AudibleCatalogException(HttpStatusCode statusCode, string message)
            : base(statusCode, message)
        {
        }

        public AudibleCatalogException(HttpStatusCode statusCode, string message, Exception innerException)
            : base(statusCode, message, innerException)
        {
        }

        public AudibleCatalogException(HttpStatusCode statusCode, string message, params object[] args)
            : base(statusCode, message, args)
        {
        }
    }
}
