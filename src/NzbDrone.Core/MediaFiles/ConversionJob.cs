using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.MediaFiles
{
    internal static class ConversionWorkPath
    {
        internal static bool IsWorkPath(string path)
        {
            return !string.IsNullOrWhiteSpace(path) && path.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries)
                .Any(part => part.Equals(".chaptarr-conversions", StringComparison.OrdinalIgnoreCase) || part.Equals("chaptarr-conversions", StringComparison.OrdinalIgnoreCase));
        }

        internal static bool HasSuccessfulArtifact(string outputPath) => GetSuccessfulArtifact(outputPath) != null;

        internal static ConversionArtifactManifest GetSuccessfulArtifact(string outputPath)
        {
            if (!IsWorkPath(outputPath) || !File.Exists(outputPath)) return null;
            try
            {
                var manifest = JsonSerializer.Deserialize<ConversionArtifactManifest>(File.ReadAllText(Path.Combine(Path.GetDirectoryName(outputPath), "conversion-artifact.json")));
                return IsSuccessfulArtifact(manifest, outputPath) ? manifest : null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is ArgumentException)
            {
                return null;
            }
        }

        internal static bool IsSuccessfulArtifact(ConversionArtifactManifest manifest, string outputPath)
        {
            return manifest?.EncoderSucceeded == true && manifest.OutputSize > 0 &&
                   outputPath.IsNotNullOrWhiteSpace() && manifest.OutputPath.IsNotNullOrWhiteSpace() &&
                   manifest.OutputPath.PathEquals(outputPath) && File.Exists(outputPath) &&
                   new FileInfo(outputPath).Length == manifest.OutputSize;
        }

        internal static bool SourcesMatch(IReadOnlyList<ConversionArtifactSource> left, IReadOnlyList<ConversionArtifactSource> right)
        {
            return left != null && right != null && left.Count == right.Count &&
                   left.Zip(right, (a, b) => a?.Path.IsNotNullOrWhiteSpace() == true && b?.Path.IsNotNullOrWhiteSpace() == true &&
                       a.Path.PathEquals(b.Path) && a.Size == b.Size && a.ModifiedUtcTicks == b.ModifiedUtcTicks).All(matches => matches);
        }

        internal static void Cleanup(string path, Logger logger, bool throwOnError = false)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                var (folder, container, files) = InspectCleanup(path);
                if (!folder.Exists) return;
                Directory.Delete(folder.FullName, recursive: true);
                logger.Info("Removed conversion workspace {0} ({1} files)", folder.FullName, files);
                for (var parent = folder.Parent; parent != null; parent = parent.Parent)
                {
                    if (!Directory.Exists(parent.FullName) || Directory.EnumerateFileSystemEntries(parent.FullName).Any()) break;
                    Directory.Delete(parent.FullName);
                    if (parent.FullName == container.FullName) break;
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Unable to remove conversion workspace {0}", path);
                if (throwOnError) throw;
            }
        }

        internal static void ValidateCleanup(string path) => InspectCleanup(path);

        private static (DirectoryInfo Folder, DirectoryInfo Container, int Files) InspectCleanup(string path)
        {
            var folder = new DirectoryInfo(Path.GetFullPath(path));
            var container = folder.Parent;
            if (!IsContainer(container?.Name)) container = container?.Parent;
            if (!IsContainer(container?.Name)) throw new IOException($"Refusing to remove non-conversion workspace: {path}");
            for (var current = folder; current != null; current = current.Parent)
            {
                if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException($"Refusing to remove linked conversion workspace: {current.FullName}");
                if (current.FullName == container.FullName) break;
            }

            if (!folder.Exists) return (folder, container, 0);
            var files = 0;
            var pending = new Stack<string>();
            pending.Push(folder.FullName);
            while (pending.Count > 0)
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException($"Refusing to remove workspace containing linked entries: {entry}");
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                    else files++;
                }
            }

            return (folder, container, files);
        }

        private static bool IsContainer(string name) => string.Equals(name, ".chaptarr-conversions", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "chaptarr-conversions", StringComparison.OrdinalIgnoreCase);

        internal static string SafeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "converted";
            var invalid = Path.GetInvalidFileNameChars();
            var safe = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
            return string.IsNullOrWhiteSpace(safe) ? "converted" : safe;
        }

        internal static string RetainedFolder(string downloadId, string outputPath)
        {
            var folder = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(outputPath)));
            var root = folder.Parent;
            var container = root?.Parent;
            if (container == null ||
                !(container.Name.Equals(".chaptarr-conversions", StringComparison.OrdinalIgnoreCase) ||
                  container.Name.Equals("chaptarr-conversions", StringComparison.OrdinalIgnoreCase)) ||
                !root.Name.Equals(SafeName(downloadId), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetExtension(outputPath).Equals(".m4b", StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"Refusing to remove conversion output outside download {downloadId}'s workspace: {outputPath}");
            }

            for (var directory = folder; directory != null; directory = directory.Parent)
            {
                if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException($"Refusing to remove linked conversion workspace: {directory.FullName}");
                }

                if (directory.FullName == container.FullName) break;
            }

            return folder.FullName;
        }
    }

    public enum ConversionJobStatus
    {
        Queued = 0,
        Converting = 1,
        ReadyToImport = 2,
        Completed = 3,
        Failed = 4,
        Cancelling = 5,
        Cancelled = 6
    }

    public class ConversionJob : ModelBase
    {
        public string DownloadId { get; set; }
        public ConversionJobStatus Status { get; set; }
        public string RequestJson { get; set; }
        public string WorkRoot { get; set; }
        public string WorkFolder { get; set; }
        public string OutputPath { get; set; }
        public int TargetQualityId { get; set; }
        public string TargetQualityName { get; set; }
        public decimal? Progress { get; set; }
        public string Message { get; set; }
        public string Error { get; set; }
        public int AttemptCount { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? HeartbeatAt { get; set; }
        public DateTime? CompletedAt { get; set; }
    }

    public class ConversionJobRequest
    {
        public string DownloadId { get; set; }
        public bool DiscardRequested { get; set; }
        public int BookId { get; set; }
        public string BookTitle { get; set; }
        public string WorkRoot { get; set; }
        public string WorkFolder { get; set; }
        public string OutputPath { get; set; }
        public List<ConversionArtifactSource> Sources { get; set; } = new();
        public int TargetQualityId { get; set; }
        public string TargetQualityName { get; set; }
        public int AudioBitrate { get; set; }
        public int AudioChannels { get; set; }
        public long ExpectedSourceDurationTicks { get; set; }
        public string TagSignature { get; set; }
        public ConversionTagOptions TagOptions { get; set; }
    }

    public class ConversionArtifactSource
    {
        public string Path { get; set; }
        public long Size { get; set; }
        public long ModifiedUtcTicks { get; set; }
    }

    public class ConversionArtifactManifest
    {
        public string DownloadId { get; set; }
        // Success is written only after the encoder reports success and validates its output.
        public bool EncoderSucceeded { get; set; }
        public long OutputSize { get; set; }
        public DateTime CreatedUtc { get; set; }
        public string OutputPath { get; set; }
        public int TargetQualityId { get; set; }
        public string TargetQualityName { get; set; }
        public int AudioBitrate { get; set; }
        public int AudioChannels { get; set; }
        public string TagMode { get; set; }
        public string TagSignature { get; set; }
        public List<ConversionArtifactSource> Sources { get; set; } = new();
    }
}
