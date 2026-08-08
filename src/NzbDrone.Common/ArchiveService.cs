using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ICSharpCode.SharpZipLib.Core;
using ICSharpCode.SharpZipLib.GZip;
using ICSharpCode.SharpZipLib.Tar;
using ICSharpCode.SharpZipLib.Zip;
using NLog;

namespace NzbDrone.Common
{
    internal static class ArchiveExtractionLimits
    {
        internal const int DefaultMaxEntries = 20_000;
        internal const long DefaultMaxSingleEntryBytes = 512L * 1024 * 1024; // 512 MiB
        internal const long DefaultMaxTotalBytes = 1024L * 1024 * 1024; // 1 GiB

        internal static int MaxEntries { get; set; } = DefaultMaxEntries;
        internal static long MaxSingleEntryBytes { get; set; } = DefaultMaxSingleEntryBytes;
        internal static long MaxTotalBytes { get; set; } = DefaultMaxTotalBytes;
    }

    public interface IArchiveService
    {
        void Extract(string compressedFile, string destination);
        void CreateZip(string path, IEnumerable<string> files);
    }

    public class ArchiveService : IArchiveService
    {
        private readonly Logger _logger;

        public ArchiveService(Logger logger)
        {
            _logger = logger;
        }

        public void Extract(string compressedFile, string destination)
        {
            _logger.Debug("Extracting archive [{0}] to [{1}]", compressedFile, destination);

            if (compressedFile.EndsWith(".zip", StringComparison.InvariantCultureIgnoreCase))
            {
                ExtractZip(compressedFile, destination);
            }
            else
            {
                ExtractTgz(compressedFile, destination);
            }

            _logger.Debug("Extraction complete.");
        }

        public void CreateZip(string path, IEnumerable<string> files)
        {
            _logger.Debug("Creating archive {0}", path);

            using var zipFile = ZipFile.Create(path);

            zipFile.BeginUpdate();

            foreach (var file in files)
            {
                zipFile.Add(file, Path.GetFileName(file));
            }

            zipFile.CommitUpdate();
        }

        private void ExtractZip(string compressedFile, string destination)
        {
            Directory.CreateDirectory(destination);
            var destinationRoot = GetSafeExtractionRoot(destination);

            using var fileStream = File.OpenRead(compressedFile);
            using var zipFile = new ZipFile(fileStream);

            _logger.Debug("Validating Archive {0}", compressedFile);

            if (!zipFile.TestArchive(true, TestStrategy.FindFirstError, OnZipError))
            {
                throw new IOException(string.Format("File {0} failed archive validation.", compressedFile));
            }

            var entryCount = 0;
            long totalExtractedBytes = 0;

            foreach (ZipEntry zipEntry in zipFile)
            {
                entryCount++;
                if (entryCount > ArchiveExtractionLimits.MaxEntries)
                {
                    throw new IOException($"Archive contains too many entries (limit {ArchiveExtractionLimits.MaxEntries})");
                }

                if (!zipEntry.IsFile)
                {
                    continue; // Ignore directories
                }

                var entryFileName = zipEntry.Name;

                if (zipEntry.Size > ArchiveExtractionLimits.MaxSingleEntryBytes)
                {
                    throw new IOException($"Archive entry '{entryFileName}' exceeds maximum allowed size (limit {ArchiveExtractionLimits.MaxSingleEntryBytes} bytes)");
                }

                if (zipEntry.Size >= 0 && totalExtractedBytes + zipEntry.Size > ArchiveExtractionLimits.MaxTotalBytes)
                {
                    throw new IOException($"Archive exceeds maximum allowed extracted size (limit {ArchiveExtractionLimits.MaxTotalBytes} bytes)");
                }

                var fullZipToPath = GetSafeExtractionPath(destinationRoot, entryFileName);
                var directoryName = Path.GetDirectoryName(fullZipToPath);
                if (!string.IsNullOrWhiteSpace(directoryName))
                {
                    Directory.CreateDirectory(directoryName);
                }

                using var zipStream = zipFile.GetInputStream(zipEntry);
                using var streamWriter = File.Create(fullZipToPath);
                CopyStreamWithLimits(zipStream, streamWriter, entryFileName, ArchiveExtractionLimits.MaxSingleEntryBytes, ref totalExtractedBytes, ArchiveExtractionLimits.MaxTotalBytes);
            }
        }

        private void ExtractTgz(string compressedFile, string destination)
        {
            Directory.CreateDirectory(destination);
            var destinationRoot = GetSafeExtractionRoot(destination);

            using var inStream = File.OpenRead(compressedFile);
            using var gzipStream = new GZipInputStream(inStream);
            using var tarStream = new TarInputStream(gzipStream, Encoding.UTF8);

            var entryCount = 0;
            long totalExtractedBytes = 0;

            TarEntry entry;
            while ((entry = tarStream.GetNextEntry()) != null)
            {
                if (string.IsNullOrWhiteSpace(entry.Name))
                {
                    continue;
                }

                entryCount++;
                if (entryCount > ArchiveExtractionLimits.MaxEntries)
                {
                    throw new IOException($"Archive contains too many entries (limit {ArchiveExtractionLimits.MaxEntries})");
                }

                var typeFlag = entry.TarHeader.TypeFlag;

                if (typeFlag == TarHeader.LF_LINK || typeFlag == TarHeader.LF_SYMLINK)
                {
                    throw new IOException($"Unsupported archive entry type (link): '{entry.Name}'");
                }

                if (entry.Size < 0)
                {
                    throw new IOException($"Invalid archive entry size for '{entry.Name}'");
                }

                if (entry.Size > ArchiveExtractionLimits.MaxSingleEntryBytes)
                {
                    throw new IOException($"Archive entry '{entry.Name}' exceeds maximum allowed size (limit {ArchiveExtractionLimits.MaxSingleEntryBytes} bytes)");
                }

                if (totalExtractedBytes + entry.Size > ArchiveExtractionLimits.MaxTotalBytes)
                {
                    throw new IOException($"Archive exceeds maximum allowed extracted size (limit {ArchiveExtractionLimits.MaxTotalBytes} bytes)");
                }

                if (entry.IsDirectory)
                {
                    var fullPath = GetSafeExtractionPath(destinationRoot, entry.Name);
                    Directory.CreateDirectory(fullPath);
                    continue;
                }

                // Ignore tar metadata/extended headers and non-file types
                if (typeFlag != TarHeader.LF_NORMAL && typeFlag != TarHeader.LF_OLDNORM)
                {
                    CopyStreamWithLimits(tarStream, Stream.Null, entry.Name, ArchiveExtractionLimits.MaxSingleEntryBytes, ref totalExtractedBytes, ArchiveExtractionLimits.MaxTotalBytes);
                    continue;
                }

                var fullFilePath = GetSafeExtractionPath(destinationRoot, entry.Name);

                var directoryName = Path.GetDirectoryName(fullFilePath);
                if (!string.IsNullOrWhiteSpace(directoryName))
                {
                    Directory.CreateDirectory(directoryName);
                }

                using var outputStream = File.Create(fullFilePath);
                CopyStreamWithLimits(tarStream, outputStream, entry.Name, ArchiveExtractionLimits.MaxSingleEntryBytes, ref totalExtractedBytes, ArchiveExtractionLimits.MaxTotalBytes);
            }
        }

        private static void CopyStreamWithLimits(Stream input, Stream output, string entryName, long maxEntryBytes, ref long totalExtractedBytes, long maxTotalBytes)
        {
            var buffer = new byte[81920];
            long entryBytes = 0;

            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                entryBytes += read;
                totalExtractedBytes += read;

                if (entryBytes > maxEntryBytes)
                {
                    throw new IOException($"Archive entry '{entryName}' exceeds maximum allowed size (limit {maxEntryBytes} bytes)");
                }

                if (totalExtractedBytes > maxTotalBytes)
                {
                    throw new IOException($"Archive exceeds maximum allowed extracted size (limit {maxTotalBytes} bytes)");
                }

                output.Write(buffer, 0, read);
            }
        }

        private static string GetSafeExtractionRoot(string destination)
        {
            var destinationFullPath = Path.GetFullPath(destination);
            if (!destinationFullPath.EndsWith(Path.DirectorySeparatorChar.ToString(), GetPathComparison()) &&
                !destinationFullPath.EndsWith(Path.AltDirectorySeparatorChar.ToString(), GetPathComparison()))
            {
                destinationFullPath += Path.DirectorySeparatorChar;
            }

            return destinationFullPath;
        }

        private static string GetSafeExtractionPath(string destinationRoot, string entryName)
        {
            var normalizedEntryName = entryName.Replace('\\', Path.DirectorySeparatorChar);
            var fullPath = Path.GetFullPath(Path.Combine(destinationRoot, normalizedEntryName));

            if (!fullPath.StartsWith(destinationRoot, GetPathComparison()))
            {
                throw new IOException($"Archive entry is trying to write outside destination folder: '{entryName}'");
            }

            return fullPath;
        }

        private static StringComparison GetPathComparison()
        {
            return Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        }

        private void OnZipError(TestStatus status, string message)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                _logger.Error("File {0} failed zip validation. {1}", status.File.Name, message);
            }
        }
    }
}
