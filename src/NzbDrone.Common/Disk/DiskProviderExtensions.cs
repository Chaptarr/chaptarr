namespace NzbDrone.Common.Disk
{
    public static class DiskProviderExtensions
    {
        public static bool FilesEqual(this IDiskProvider diskProvider, string sourceFile, string targetFile)
        {
            if (!diskProvider.FileExists(sourceFile) || !diskProvider.FileExists(targetFile))
            {
                return false;
            }

            if (diskProvider.GetFileSize(sourceFile) != diskProvider.GetFileSize(targetFile))
            {
                return false;
            }

            var sourceBuffer = new byte[64 * 1024];
            var targetBuffer = new byte[64 * 1024];
            using (var sourceStream = diskProvider.OpenReadStream(sourceFile))
            using (var targetStream = diskProvider.OpenReadStream(targetFile))
            {
                while (true)
                {
                    var sourceLength = sourceStream.Read(sourceBuffer, 0, sourceBuffer.Length);
                    var targetLength = targetStream.Read(targetBuffer, 0, targetBuffer.Length);

                    if (sourceLength != targetLength)
                    {
                        return false;
                    }

                    if (sourceLength == 0)
                    {
                        return true;
                    }

                    for (var i = 0; i < sourceLength; i++)
                    {
                        if (sourceBuffer[i] != targetBuffer[i])
                        {
                            return false;
                        }
                    }
                }
            }
        }

    }
}
