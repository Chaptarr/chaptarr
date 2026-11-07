using System.Security.Cryptography;
using System.Text;
using System.Collections.Generic;
using System.Linq;

namespace ChaptarrNG.Installer.Pipeline;

public static class SqliteDatabaseFingerprint
{
    public sealed record Snapshot(string Hash, IReadOnlyDictionary<string, string> Files);

    public static string Compute(string dataDirectory)
    {
        return Capture(dataDirectory).Hash;
    }

    public static Snapshot Capture(string dataDirectory)
    {
        using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);

        // SQLite may rewrite its disposable -shm index while opening a WAL database read-only.
        // Fingerprint both possible source databases, including absent names, so a
        // second populated source appearing during the upgrade invalidates the snapshot.
        foreach (var databaseName in BookshelfDatabaseFiles.LegacyDatabaseNames)
        {
            foreach (var name in new[] { databaseName, databaseName + "-journal", databaseName + "-wal" })
            {
                fingerprint.AppendData(Encoding.UTF8.GetBytes(name));
                fingerprint.AppendData(new byte[] { 0 });
                var path = Path.Combine(dataDirectory, name);
                if (!File.Exists(path))
                {
                    fingerprint.AppendData(new byte[] { 0 });
                    files[name] = "<missing>";
                    continue;
                }

                using var stream = File.OpenRead(path);
                fingerprint.AppendData(new byte[] { 1 });
                var fileHash = SHA256.HashData(stream);
                fingerprint.AppendData(fileHash);
                files[name] = Convert.ToHexString(fileHash);
            }
        }

        return new Snapshot(Convert.ToHexString(fingerprint.GetHashAndReset()), files);
    }

    public static IReadOnlyList<string> GetChangedFiles(Snapshot before, Snapshot after)
    {
        return before.Files
            .Where(file => !after.Files.TryGetValue(file.Key, out var current) ||
                !string.Equals(file.Value, current, StringComparison.Ordinal))
            .Select(file => file.Key)
            .ToArray();
    }
}
