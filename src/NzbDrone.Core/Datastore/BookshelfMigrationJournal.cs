using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace NzbDrone.Core.Datastore
{
    /// <summary>
    /// Durable handoff state shared by package hooks and the database converters.
    /// Values are tokens or hashes; secrets and connection strings are never stored.
    /// </summary>
    public sealed class BookshelfMigrationJournal
    {
        private static readonly object Sync = new object();
        private readonly string _directory;

        public BookshelfMigrationJournal(string dataDirectory)
        {
            if (string.IsNullOrWhiteSpace(dataDirectory))
            {
                throw new ArgumentException("A BookshelfNG handoff data directory is required.", nameof(dataDirectory));
            }

            _directory = Path.Combine(Path.GetFullPath(dataDirectory), ".chaptarrng-bookshelfng-journal");
        }

        public void RecordState(string state)
        {
            if (state is not ("detected" or "preflighted" or "snapshotted" or "converted" or "promoted" or "healthy" or "rolled-back" or "intervention-required"))
            {
                throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown BookshelfNG migration state.");
            }

            lock (Sync)
            {
                EnsureDirectories();
                WriteAtomic(Path.Combine(_directory, "states", state), DateTime.UtcNow.ToString("O"));
                WriteAtomic(Path.Combine(_directory, "state"), state);
            }
        }

        public void RecordOutcome(string name, string value)
        {
            ValidateToken(name, nameof(name));
            ValidateToken(value, nameof(value));

            lock (Sync)
            {
                EnsureDirectories();
                WriteAtomic(Path.Combine(_directory, "outcomes", name), value);
            }
        }

        public void RecordFingerprint(string name, string path)
        {
            ValidateToken(name, nameof(name));
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("A path is required for a BookshelfNG migration fingerprint.", nameof(path));
            }

            var fullPath = Path.GetFullPath(path);
            if (Directory.Exists(fullPath))
            {
                throw new IOException($"Migration fingerprint '{name}' requires a regular file.");
            }

            var fingerprint = ComputeFingerprint(fullPath, name);

            lock (Sync)
            {
                EnsureDirectories();
                WriteAtomic(Path.Combine(_directory, "fingerprints", name), fingerprint);
            }
        }

        public static string ComputeFingerprint(string path)
        {
            return ComputeFingerprint(path, "migration-artifact");
        }

        private static string ComputeFingerprint(string path, string name)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("A path is required for a BookshelfNG migration fingerprint.", nameof(path));
            }

            var fullPath = Path.GetFullPath(path);
            string fingerprint;
            var fileInfo = new FileInfo(fullPath);
            var directoryInfo = new DirectoryInfo(fullPath);
            if (fileInfo.LinkTarget is not null || directoryInfo.LinkTarget is not null)
            {
                throw new IOException($"Migration fingerprint '{name}' cannot follow a symbolic link or reparse point.");
            }

            FileAttributes attributes = default;
            var exists = true;
            try
            {
                attributes = File.GetAttributes(fullPath);
            }
            catch (FileNotFoundException)
            {
                exists = false;
            }
            catch (DirectoryNotFoundException)
            {
                exists = false;
            }

            if (!exists)
            {
                fingerprint = "missing";
            }
            else if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"Migration fingerprint '{name}' cannot follow a symbolic link or reparse point.");
            }
            else if ((attributes & FileAttributes.Directory) != 0)
            {
                fingerprint = FingerprintDirectory(fullPath, name);
            }
            else
            {
                fingerprint = FingerprintFile(fullPath, name);
            }

            return fingerprint;
        }

        private static string FingerprintFile(string path, string name)
        {
            var beforeAttributes = File.GetAttributes(path);
            if ((beforeAttributes & (FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.Device)) != 0)
            {
                throw new IOException($"Migration fingerprint '{name}' requires a regular file.");
            }

            var before = new FileInfo(path);
            var originalLength = before.Length;
            var originalLastWrite = before.LastWriteTimeUtc;
            byte[] digest;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                digest = SHA256.HashData(stream);
            }

            var after = new FileInfo(path);
            var afterAttributes = File.GetAttributes(path);
            if ((afterAttributes & FileAttributes.ReparsePoint) != 0 ||
                !after.Exists ||
                after.Length != originalLength ||
                after.LastWriteTimeUtc != originalLastWrite)
            {
                throw new IOException($"Migration fingerprint '{name}' changed while it was being recorded.");
            }

            return "sha256:" + Convert.ToHexString(digest).ToLowerInvariant();
        }

        private static string FingerprintDirectory(string rootPath, string name)
        {
            var entries = new List<(string RelativePath, string FullPath)>();
            var pendingDirectories = new Stack<string>();
            pendingDirectories.Push(rootPath);

            while (pendingDirectories.Count > 0)
            {
                var directory = pendingDirectories.Pop();
                foreach (var entryPath in Directory.EnumerateFileSystemEntries(directory))
                {
                    var attributes = File.GetAttributes(entryPath);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new IOException($"Migration fingerprint '{name}' cannot follow a symbolic link or reparse point.");
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pendingDirectories.Push(entryPath);
                    }
                    else if ((attributes & FileAttributes.Device) == 0)
                    {
                        var relativePath = Path.GetRelativePath(rootPath, entryPath)
                            .Replace(Path.DirectorySeparatorChar, '/')
                            .Replace(Path.AltDirectorySeparatorChar, '/');
                        entries.Add((relativePath, entryPath));
                    }
                    else
                    {
                        throw new IOException($"Migration fingerprint '{name}' contains a non-regular filesystem entry.");
                    }
                }
            }

            entries.Sort((left, right) => StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath));
            var manifest = new StringBuilder();
            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                if (index > 0)
                {
                    manifest.Append('\n');
                }

                manifest.Append(entry.RelativePath);
                manifest.Append('\0');
                manifest.Append(FingerprintFile(entry.FullPath, name).Substring("sha256:".Length));
            }

            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(manifest.ToString()));
            return "sha256:" + Convert.ToHexString(digest).ToLowerInvariant();
        }

        public void RecordConverterStage(string state, string artifact)
        {
            var artifactName = Path.GetFileName(artifact ?? string.Empty);
            if (string.IsNullOrWhiteSpace(artifactName))
            {
                artifactName = "none";
            }

            artifactName = new string(artifactName.Select(character =>
                char.IsLetterOrDigit(character) || character is '.' or '_' or '-' ? character : '_').ToArray());

            RecordOutcome("converter-phase", state);
            RecordOutcome("converter-artifact", artifactName);
            RecordOutcome("converter-" + state + "-artifact", artifactName);
            if (File.Exists(artifact) && state == "snapshotted")
            {
                // The consistent SQLite snapshot is the authoritative source
                // image. It is later moved into the retained source-backup path.
                RecordFingerprint("source-database", artifact);
            }
            else if (File.Exists(artifact) && state == "converted")
            {
                RecordFingerprint("target-database", artifact);
            }

            if (state is "converted" or "promoted")
            {
                if (state == "promoted")
                {
                    RecordOutcome("source-backup", "retained");
                }

                RecordState(state);
            }
            else
            {
                RecordMilestone("converter-" + state);
            }
        }

        private void RecordMilestone(string milestone)
        {
            ValidateToken(milestone, nameof(milestone));
            lock (Sync)
            {
                EnsureDirectories();
                WriteAtomic(Path.Combine(_directory, "states", milestone), DateTime.UtcNow.ToString("O"));
            }
        }

        private void EnsureDirectories()
        {
            EnsureJournalDirectory(_directory);
            EnsureJournalDirectory(Path.Combine(_directory, "states"));
            EnsureJournalDirectory(Path.Combine(_directory, "outcomes"));
            EnsureJournalDirectory(Path.Combine(_directory, "fingerprints"));
            if (!OperatingSystem.IsWindows())
            {
                var ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
                File.SetUnixFileMode(_directory, ownerOnly);
                File.SetUnixFileMode(Path.Combine(_directory, "states"), ownerOnly);
                File.SetUnixFileMode(Path.Combine(_directory, "outcomes"), ownerOnly);
                File.SetUnixFileMode(Path.Combine(_directory, "fingerprints"), ownerOnly);
            }
        }

        private static void EnsureJournalDirectory(string path)
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("BookshelfNG migration journal paths must be regular directories.");
            }
        }

        private static void WriteAtomic(string path, string value)
        {
            var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.WriteLine(value);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }

                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(temporaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }

                File.Move(temporaryPath, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }

        private static void ValidateToken(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Any(character =>
                    !char.IsLetterOrDigit(character) && character is not ('.' or '_' or '-' or ':')))
            {
                throw new ArgumentException("Journal values must be non-empty tokens.", parameterName);
            }
        }
    }
}
