using System;
using System.Collections.Generic;
using System.Linq;

namespace NzbDrone.Core.CustomFormats
{
    public static class BuiltInCustomFormats
    {
        public const string DramatizedAudioKey = "dramatized-full-cast-audio";
        public const string StandardAudioKey = "standard-non-dramatized-audio";
        public const string PreferredNarratorKey = "preferred-narrator";
        public const string PreferredNarratorMajorityKey = "preferred-narrator-majority";
        public const string CompletePreferredCastKey = "complete-preferred-cast";
        public const string DramatizedAudioName = "Dramatized / Full-Cast Audio";
        public const string StandardAudioName = "Standard / Non-Dramatized Audio";
        public const string PreferredNarratorName = "Selected Audiobook Narrators";
        public const string PreferredNarratorMajorityName = "Preferred Narrator Majority";
        public const string CompletePreferredCastName = "Complete Preferred Cast";
        public const string LegacyPreferredNarratorName = "Preferred Narrator";
        public const string InterimPreferredNarratorName = "Pinned Edition Narrator";
        public const string InterimNarratorMatchName = "Narrator Match";
        public const string InterimPreferredNarratorMajorityName = "Pinned Edition Narrator Majority";
        public const string InterimCompletePreferredCastName = "Complete Pinned Edition Cast";
        public const string TransitionalPreferredNarratorMajorityName = "Narrator Majority";
        public const string TransitionalCompletePreferredCastName = "Full Cast Match";
        public const int PreferredNarratorDefaultAudiobookScore = 50;
        public const int RetiredNarratorTierDefaultAudiobookScore = 25;

        public static int? GetDefaultAudiobookProfileScore(CustomFormat format)
        {
            if (string.Equals(format?.BuiltInKey, PreferredNarratorKey, StringComparison.OrdinalIgnoreCase))
            {
                return PreferredNarratorDefaultAudiobookScore;
            }

            return null;
        }

        public static IReadOnlyCollection<string> GetLegacyNames(string builtInKey)
        {
            if (string.Equals(builtInKey, PreferredNarratorKey, StringComparison.OrdinalIgnoreCase))
            {
                return new[] { LegacyPreferredNarratorName, InterimPreferredNarratorName, InterimNarratorMatchName };
            }

            return Array.Empty<string>();
        }

        public static bool IsRetiredBuiltIn(CustomFormat format)
        {
            return string.Equals(format?.BuiltInKey, StandardAudioKey, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(format?.BuiltInKey, PreferredNarratorMajorityKey, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(format?.BuiltInKey, CompletePreferredCastKey, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsUntouchedRetiredBuiltIn(CustomFormat format)
        {
            return IsUntouchedRetiredBuiltIn(format, format?.BuiltInKey);
        }

        public static bool TryGetRetiredBuiltInKeyForUnkeyed(CustomFormat format, out string builtInKey)
        {
            builtInKey = null;
            if (format?.BuiltInKey != null)
            {
                return false;
            }

            foreach (var candidateKey in new[] { StandardAudioKey, PreferredNarratorMajorityKey, CompletePreferredCastKey })
            {
                if (IsUntouchedRetiredBuiltIn(format, candidateKey))
                {
                    builtInKey = candidateKey;
                    return true;
                }
            }

            return false;
        }

        private static bool IsUntouchedRetiredBuiltIn(CustomFormat format, string builtInKey)
        {
            if (format == null ||
                string.IsNullOrWhiteSpace(builtInKey) ||
                format.IncludeCustomFormatWhenRenaming ||
                format.Specifications?.Count != 1)
            {
                return false;
            }

            var specification = format.Specifications[0];
            if (specification == null || specification.Required)
            {
                return false;
            }

            if (string.Equals(builtInKey, StandardAudioKey, StringComparison.OrdinalIgnoreCase))
            {
                return HasKnownName(format.Name, StandardAudioName) &&
                       HasKnownName(specification.Name, StandardAudioName) &&
                       specification is AudioProductionSpecification &&
                       specification.Negate;
            }

            if (string.Equals(builtInKey, PreferredNarratorMajorityKey, StringComparison.OrdinalIgnoreCase))
            {
                return HasKnownName(format.Name,
                                    PreferredNarratorMajorityName,
                                    InterimPreferredNarratorMajorityName,
                                    TransitionalPreferredNarratorMajorityName) &&
                       HasKnownName(specification.Name,
                                    PreferredNarratorMajorityName,
                                    InterimPreferredNarratorMajorityName,
                                    TransitionalPreferredNarratorMajorityName) &&
                       specification is PreferredNarratorMajoritySpecification &&
                       !specification.Negate;
            }

            return string.Equals(builtInKey, CompletePreferredCastKey, StringComparison.OrdinalIgnoreCase) &&
                   HasKnownName(format.Name,
                                CompletePreferredCastName,
                                InterimCompletePreferredCastName,
                                TransitionalCompletePreferredCastName) &&
                   HasKnownName(specification.Name,
                                CompletePreferredCastName,
                                InterimCompletePreferredCastName,
                                TransitionalCompletePreferredCastName) &&
                   specification is PreferredNarratorCompleteSpecification &&
                   !specification.Negate;
        }

        private static bool HasKnownName(string value, params string[] knownNames)
        {
            return ((IEnumerable<string>)knownNames).Contains(value, StringComparer.Ordinal);
        }

        public static IEnumerable<CustomFormat> All()
        {
            yield return new CustomFormat
            {
                Name = DramatizedAudioName,
                BuiltInKey = DramatizedAudioKey,
                AppliesTo = CustomFormatMediaType.Audiobook,
                IncludeCustomFormatWhenRenaming = false,
                Specifications = new List<ICustomFormatSpecification>
                {
                    new AudioProductionSpecification
                    {
                        Name = DramatizedAudioName
                    }
                }
            };

            yield return new CustomFormat
            {
                Name = PreferredNarratorName,
                BuiltInKey = PreferredNarratorKey,
                AppliesTo = CustomFormatMediaType.Audiobook,
                IncludeCustomFormatWhenRenaming = false,
                Specifications = new List<ICustomFormatSpecification>
                {
                    new PreferredNarratorSpecification
                    {
                        Name = PreferredNarratorName
                    }
                }
            };
        }
    }
}
