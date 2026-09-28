using System.Globalization;

namespace NzbDrone.Core.MediaFiles
{
    /// <summary>
    /// The correlation id used to track a conversion that no download client owns: a manual
    /// import, or a book already in the library. Shared so the import pipeline that runs the
    /// conversion and the library conversion service that reports on it agree on one id.
    /// </summary>
    public static class LocalConversionId
    {
        public static string For(int bookId, int editionId)
        {
            return string.Format(CultureInfo.InvariantCulture, "chaptarr-local-{0}-{1}", bookId, editionId);
        }
    }
}
