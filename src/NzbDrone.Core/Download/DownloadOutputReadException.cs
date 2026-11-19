using System;
using System.IO;

namespace NzbDrone.Core.Download
{
    public class DownloadOutputReadException : IOException
    {
        public DownloadOutputReadException(string path, Exception innerException)
            : base($"Unable to read {path}: {innerException.Message}", innerException)
        {
        }
    }
}
