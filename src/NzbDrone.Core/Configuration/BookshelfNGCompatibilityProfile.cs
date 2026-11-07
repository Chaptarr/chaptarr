using System;

namespace NzbDrone.Core.Configuration
{
    /// <summary>
    /// Records the source semantics needed to keep a migrated BookshelfNG
    /// installation compatible across future ChaptarrNG updates.
    /// </summary>
    public sealed class BookshelfNGCompatibilityProfile
    {
        public const int CurrentVersion = 2;
        public const int CurrentIdentityMapVersion = 1;
        public const int LatestSupportedSourceSchemaVersion = 158;
        public const string CurrentFormatSemantics = "audiobook-ebook-split-v1";
        public const string QualityProfileIdentitySequenceKey = "bookshelfnglegacyqualityprofilesequence";

        public int Version { get; set; }
        public string SourceProduct { get; set; } = string.Empty;
        public int SourceSchemaVersion { get; set; }
        public string ProviderDialect { get; set; } = string.Empty;
        public string ApiBasePath { get; set; } = string.Empty;
        public string FormatSemantics { get; set; } = string.Empty;
        public int IdentityMapVersion { get; set; }
        public string MigrationOutcome { get; set; } = string.Empty;

        public bool IsCompatible
        {
            get
            {
                if (!IsProviderDialect(ProviderDialect))
                {
                    return false;
                }

                // Versions 0 and 1 were written by earlier migration candidates.
                // Keep those installations usable while new conversions write the
                // complete version-2 profile below.
                if (Version is 0 or 1)
                {
                    return true;
                }

                return Version == CurrentVersion &&
                       string.Equals(SourceProduct, "BookshelfNG", StringComparison.OrdinalIgnoreCase) &&
                       SourceSchemaVersion is > 0 and <= LatestSupportedSourceSchemaVersion &&
                       IsValidApiBasePath(ApiBasePath) &&
                       string.Equals(FormatSemantics, CurrentFormatSemantics, StringComparison.Ordinal) &&
                       IdentityMapVersion == CurrentIdentityMapVersion &&
                       string.Equals(MigrationOutcome, "converted", StringComparison.Ordinal);
            }
        }

        public static bool IsProviderDialect(string value)
        {
            return string.Equals(value, "gr", StringComparison.Ordinal) ||
                   string.Equals(value, "hc", StringComparison.Ordinal);
        }

        public static string NormalizeApiBasePath(string value)
        {
            var path = (value ?? string.Empty).Trim().Trim('/');
            return path.Length == 0 ? string.Empty : "/" + path;
        }

        private static bool IsValidApiBasePath(string value)
        {
            return string.IsNullOrEmpty(value) ||
                   (value.StartsWith("/", StringComparison.Ordinal) &&
                    value.IndexOfAny(new[] { '?', '#', '\\' }) < 0);
        }
    }
}
