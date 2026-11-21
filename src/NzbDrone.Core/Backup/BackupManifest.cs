using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Backup
{
    public class BackupManifest
    {
        public const string FileName = "manifest.json";
        [JsonRequired]
        public int FormatVersion { get; set; } = 3;
        [JsonRequired]
        public string ChaptarrVersion { get; set; } = BuildInfo.Version.ToString();
        [JsonRequired]
        public DatabaseType DatabaseType { get; set; }
        [JsonRequired]
        public Dictionary<string, string> Files { get; set; } = new(StringComparer.Ordinal);
        public List<string> OmittedComponents { get; set; } = new();
        public int? PostgresMajorVersion { get; set; }
        public Dictionary<string, PostgresDatabaseSettings> PostgresDatabases { get; set; }

        public sealed class PostgresDatabaseSettings
        {
            public string Encoding { get; set; }
            public string Collation { get; set; }
            public string CharacterType { get; set; }
            public string Provider { get; set; }
            public string Locale { get; set; }
            public string IcuRules { get; set; }
            public string CollationVersion { get; set; }
        }

        public static BackupManifest Create(string folder, DatabaseType databaseType)
        {
            return new BackupManifest
            {
                DatabaseType = databaseType,
                Files = Directory.GetFiles(folder).Where(path => Path.GetFileName(path) != FileName).ToDictionary(Path.GetFileName, Hash, StringComparer.Ordinal)
            };
        }

        public void Write(string folder) => File.WriteAllText(Path.Combine(folder, FileName), JsonSerializer.Serialize(this));

        public static BackupManifest Read(string folder)
        {
            BackupManifest manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(Path.Combine(folder, FileName)));
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("The backup manifest is invalid or missing required metadata.", ex);
            }
            if (manifest == null || manifest.FormatVersion is not (1 or 2 or 3) || manifest.Files == null || manifest.Files.Count == 0)
            {
                throw new InvalidDataException("Unsupported or empty backup manifest.");
            }

            return manifest;
        }

        internal void ValidateStructure(bool requireConfig = true, bool checkApplicationVersion = true)
        {
            if (FormatVersion is not (1 or 2 or 3) || Files == null || Files.Count == 0 || !Enum.IsDefined(DatabaseType))
            {
                throw new InvalidDataException("Unsupported or empty backup manifest.");
            }

            var databases = DatabaseType == DatabaseType.SQLite ? new[] { "chaptarr.db", "logs.db", "cache.db" } : new[] { "chaptarr.dump", "logs.dump", "cache.dump" };
            if ((requireConfig && (!Files.ContainsKey("config.xml") || (FormatVersion < 3 && !Files.ContainsKey("staging.db")))) || !Files.ContainsKey(databases[0]) ||
                (requireConfig && FormatVersion == 2 && databases.Any(name => !Files.ContainsKey(name))))
            {
                throw new InvalidDataException("The backup is missing a required component.");
            }

            if (DatabaseType == DatabaseType.PostgreSQL && (FormatVersion < 2 || PostgresMajorVersion is null or < 10))
            {
                throw new InvalidDataException("The PostgreSQL backup is missing compatibility metadata.");
            }
            if (DatabaseType == DatabaseType.PostgreSQL && (PostgresDatabases == null || databases.Where(Files.ContainsKey).Any(name =>
                !PostgresDatabases.TryGetValue(name, out var settings) || settings == null ||
                string.IsNullOrWhiteSpace(settings.Encoding) || string.IsNullOrWhiteSpace(settings.Collation) ||
                string.IsNullOrWhiteSpace(settings.CharacterType) || settings.Provider is not ("c" or "i" or "b"))))
            {
                throw new InvalidDataException("The PostgreSQL backup does not contain database encoding and locale settings. Create a new backup before restoring through Chaptarr.");
            }

            if (!Version.TryParse(ChaptarrVersion, out var version) || (checkApplicationVersion && version > BuildInfo.Version))
            {
                throw new InvalidDataException("This backup requires a newer Chaptarr version.");
            }

            if (OmittedComponents == null || OmittedComponents.Any(name => !databases.Skip(1).Contains(name) || Files.ContainsKey(name)))
            {
                throw new InvalidDataException("The backup contains invalid optional-component omissions.");
            }
            foreach (var entry in Files)
            {
                if (!databases.Concat(new[] { "config.xml", "staging.db", "INFO" }).Contains(entry.Key, StringComparer.Ordinal))
                {
                    throw new InvalidDataException("The backup contains an unsupported component.");
                }


            }
        }

        public void Validate(string folder, bool requireConfig = true)
        {
            ValidateStructure(requireConfig);
            foreach (var entry in Files)
            {
                var path = Path.Combine(folder, entry.Key);
                if (!File.Exists(path) || !string.Equals(Hash(path), entry.Value, StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"The backup component {entry.Key} is missing or has an invalid checksum.");
                }
            }
        }

        public void ValidateArchive(string path)
        {
            using var archive = ZipFile.OpenRead(path);
            if (!archive.Entries.Select(entry => entry.FullName).OrderBy(name => name, StringComparer.Ordinal)
                .SequenceEqual(Files.Keys.Append(FileName).OrderBy(name => name, StringComparer.Ordinal)))
            {
                throw new InvalidDataException("The backup archive has missing, duplicate or unexpected components.");
            }

            using (var stream = archive.GetEntry(FileName).Open())
            {
                var archivedManifest = JsonSerializer.Deserialize<BackupManifest>(stream);
                if (JsonSerializer.Serialize(archivedManifest) != JsonSerializer.Serialize(this))
                {
                    throw new InvalidDataException("The backup archive has an invalid manifest.");
                }
            }

            foreach (var entry in Files)
            {
                using var stream = archive.GetEntry(entry.Key).Open();
                if (Convert.ToHexString(SHA256.HashData(stream)) != entry.Value)
                {
                    throw new InvalidDataException($"The archived component {entry.Key} has an invalid checksum.");
                }
            }
        }

        private static string Hash(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
    }
}
