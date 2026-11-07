using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ChaptarrNG.Installer.Pipeline;

public static class BookshelfDatabaseFiles
{
    public static IReadOnlyList<string> LegacyDatabaseNames { get; } = Array.AsReadOnly(new[] { "readarr.db", "audioarr.db" });

    public static string ResolveExistingDatabaseName(string dataDirectory)
    {
        var populated = GetPopulatedDatabaseNames(dataDirectory);
        if (populated.Count == 0)
        {
            throw new FileNotFoundException(
                "No populated BookshelfNG SQLite database (readarr.db or audioarr.db) was found.",
                Path.Combine(dataDirectory, "readarr.db"));
        }

        // readarr.db is the stable primary identity source for the automatic
        // dual-database merge. Both databases are backed up and watched by the
        // installer; this name only selects the source-facing app-data path.
        return populated[0];
    }

    public static string? ResolveExistingDatabaseNameOrNull(string dataDirectory)
    {
        var populated = GetPopulatedDatabaseNames(dataDirectory);
        return populated.FirstOrDefault();
    }

    public static IReadOnlyList<string> GetPopulatedDatabaseNames(string dataDirectory)
    {
        return Array.AsReadOnly(FindPopulatedDatabaseNames(dataDirectory));
    }

    public static void ValidateLegacyDatabasePaths(string dataDirectory)
    {
        _ = GetPopulatedDatabaseNames(dataDirectory);
    }

    private static string[] FindPopulatedDatabaseNames(string dataDirectory)
    {
        return LegacyDatabaseNames
            .Where(name =>
            {
                var path = Path.Combine(dataDirectory, name);
                var database = new FileInfo(path);
                if (database.LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null)
                {
                    throw new InvalidDataException($"The BookshelfNG database '{name}' is a symbolic link. Resolve it to a regular file before migrating.");
                }

                if (database.Exists && (database.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException($"The BookshelfNG database '{name}' is a reparse point. Resolve it to a regular file before migrating.");
                }

                if (Directory.Exists(path))
                {
                    throw new InvalidDataException($"The BookshelfNG database path '{name}' is a directory, not a regular SQLite file.");
                }

                var hasDurableSidecar = false;
                foreach (var suffix in new[] { "-journal", "-wal", "-shm" })
                {
                    var sidecarPath = path + suffix;
                    var sidecar = new FileInfo(sidecarPath);
                    var sidecarDirectory = new DirectoryInfo(sidecarPath);
                    if (Directory.Exists(sidecarPath))
                    {
                        throw new InvalidDataException($"The BookshelfNG SQLite sidecar '{name + suffix}' is a directory, not a regular file.");
                    }

                    if (sidecar.LinkTarget is not null ||
                        sidecarDirectory.LinkTarget is not null ||
                        ((File.Exists(sidecarPath) || Directory.Exists(sidecarPath)) &&
                            (File.GetAttributes(sidecarPath) & FileAttributes.ReparsePoint) != 0))
                    {
                        throw new InvalidDataException($"The BookshelfNG SQLite sidecar '{name + suffix}' is a reparse point. Resolve it to a regular file before migrating.");
                    }

                    if (suffix != "-shm" && File.Exists(sidecarPath) && sidecar.Length > 0)
                    {
                        hasDurableSidecar = true;
                    }
                }

                if (!database.Exists)
                {
                    if (hasDurableSidecar)
                    {
                        throw new InvalidDataException($"The BookshelfNG database '{name}' is missing but its WAL or rollback journal contains data. Preserve the database and sidecars and resolve recovery before migrating.");
                    }

                    return false;
                }

                return database.Length > 0 || hasDurableSidecar;
            })
            .ToArray();
    }
}
