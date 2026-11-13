using System;
using NzbDrone.Common.Exceptions;

namespace NzbDrone.Core.Books.Calibre
{
    // The server did not return a usable Calibre API response; this is library-wide.
    public class CalibreInvalidResponseException : CalibreException
    {
        public CalibreInvalidResponseException(string message, Exception innerException = null)
            : base(message, innerException)
        {
        }
    }

    public class CalibreException : NzbDroneException
    {
        public bool WriteAccepted { get; set; }

        public CalibreException(string message)
            : base(message)
        {
        }

        public CalibreException(string message, Exception innerException, params object[] args)
            : base(message, innerException, args)
        {
        }
    }
}
