using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace ChaptarrNG.Installer.Pipeline;

public sealed record ExtractProgress(int Extracted, int Total);

public static class PayloadExtractor
{
    private const string PayloadResourceName = "ChaptarrNG.Installer.Payload.Chaptarr.payload.zip";

    public static bool HasEmbeddedPayload()
    {
        var resources = Assembly.GetExecutingAssembly().GetManifestResourceNames();
        return resources.Any(n => string.Equals(n, PayloadResourceName, StringComparison.Ordinal)) &&
            resources.Any(n => string.Equals(n, "rollback.ps1", StringComparison.Ordinal));
    }

    public static async Task<string> ExtractToStagingDirectoryAsync(
        string dataDirectory,
        IProgress<LogLine> log,
        IProgress<ExtractProgress> progress,
        CancellationToken ct = default)
    {
        if (!HasEmbeddedPayload())
        {
            throw new InvalidOperationException(
                $"Embedded payload '{PayloadResourceName}' was not found. Rebuild the installer with Payload\\Chaptarr.payload.zip.");
        }

        var stagingDirectory = Path.Combine(dataDirectory, ".chaptarrng-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);

        try
        {
            await ExtractEmbeddedZipAsync(stagingDirectory, log, progress, ct).ConfigureAwait(false);
            var consoleExe = Path.Combine(stagingDirectory, "Chaptarr.Console.exe");
            if (!File.Exists(consoleExe))
            {
                throw new InvalidDataException($"The embedded Chaptarr payload is missing {consoleExe}.");
            }

            log.Report(new LogLine(LogStream.Stdout, $"[payload] Chaptarr migration payload staged at {stagingDirectory}."));
            return stagingDirectory;
        }
        catch
        {
            TryDeleteDirectory(stagingDirectory);
            throw;
        }
    }

    public static string ReplaceBinDirectory(
        string dataDirectory,
        string stagingDirectory,
        IProgress<LogLine> log)
    {
        var binDirectory = Path.Combine(dataDirectory, "bin");
        var previousDirectory = Path.Combine(dataDirectory, "bin.bookshelfng-" + Guid.NewGuid().ToString("N"));

        log.Report(new LogLine(LogStream.Stdout, "[payload] Switching the application bin directory to ChaptarrNG."));
        Directory.Move(binDirectory, previousDirectory);
        try
        {
            Directory.Move(stagingDirectory, binDirectory);
        }
        catch
        {
            if (!Directory.Exists(binDirectory) && Directory.Exists(previousDirectory))
            {
                Directory.Move(previousDirectory, binDirectory);
            }

            throw;
        }

        log.Report(new LogLine(LogStream.Stdout, $"[payload] Existing BookshelfNG binaries retained at {previousDirectory} until health verification."));
        return previousDirectory;
    }

    public static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Cleanup failure must not undo a healthy service handoff or hide the original error.
        }
    }

    private static async Task ExtractEmbeddedZipAsync(
        string destinationDirectory,
        IProgress<LogLine> log,
        IProgress<ExtractProgress> progress,
        CancellationToken ct)
    {
        var assembly = Assembly.GetExecutingAssembly();
        await using var stream = assembly.GetManifestResourceStream(PayloadResourceName);
        if (stream is null)
        {
            throw new InvalidOperationException($"Embedded payload resource '{PayloadResourceName}' was not found.");
        }

        log.Report(new LogLine(LogStream.Stdout, "[payload] Opening embedded ChaptarrNG archive..."));
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var totalEntries = archive.Entries.Count(e => !string.IsNullOrEmpty(e.Name));
        log.Report(new LogLine(LogStream.Stdout, $"[payload] Extracting {totalEntries} files..."));

        var extracted = 0;
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var destination = GetContainedEntryPath(destinationDirectory, entry.FullName);
            var parent = Path.GetDirectoryName(destination);
            if (parent is not null)
            {
                Directory.CreateDirectory(parent);
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            entry.ExtractToFile(destination, overwrite: false);
            extracted++;
            if (extracted % 25 == 0 || extracted == totalEntries)
            {
                progress.Report(new ExtractProgress(extracted, totalEntries));
            }
        }

        progress.Report(new ExtractProgress(totalEntries, totalEntries));
        log.Report(new LogLine(LogStream.Stdout, $"[payload] Extracted {extracted}/{totalEntries} files."));
    }

    private static string GetContainedEntryPath(string destinationDirectory, string entryPath)
    {
        var root = Path.GetFullPath(destinationDirectory);
        var rootPrefix = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;
        var destination = Path.GetFullPath(Path.Combine(rootPrefix, entryPath));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!destination.StartsWith(rootPrefix, comparison))
        {
            throw new InvalidDataException($"Archive entry '{entryPath}' is outside the staging directory.");
        }

        return destination;
    }
}
