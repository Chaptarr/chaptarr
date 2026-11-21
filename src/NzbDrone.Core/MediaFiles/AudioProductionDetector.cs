using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.MediaFiles
{
    public static class AudioProductionDetector
    {
        public static bool ContainsGraphicAudioBrand(string value) =>
            AudioProductionConstants.Matches(value, kind: AudioProductionConstants.EvidenceKind.Brand);

        public static bool HasFullCastNarrators(IEnumerable<string> names) =>
            names?.Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(3).Count() == 3;

        public static string GetProductionLabel(BookFile file, Edition edition)
        {
            if (HasGraphicAudioBrand(file, edition)) return "GraphicAudio";
            if (HasFullCastNarrators(edition?.NarratorNames) || HasCastCredit(edition)) return "Full Cast";
            if (file?.AudioProductionOverride == false) return null;

            var label = file?.AudioProductionType?.Trim();
            if (string.IsNullOrWhiteSpace(label)) label = edition?.AudioProductionType?.Trim();
            // Legacy production flags/text described a style, not verified publisher identity.
            if (ContainsGraphicAudioBrand(label)) label = null;
            var productionType = GetProductionType(new[] { label });
            if (productionType != null) return productionType;
            if (!string.IsNullOrWhiteSpace(label)) return label;
            return IsDramatized(file, edition) ? "Dramatized" : null;
        }

        public static bool IsDramatizedEdition(Edition edition) => edition != null &&
            (edition.IsGraphicAudio ||
             AudioProductionConstants.Matches(edition.AudioProductionType, editionClassification: true) ||
             AudioProductionConstants.Matches(edition.Publisher, editionClassification: true) ||
             ContainsGraphicAudioBrand(edition.Title) || ContainsGraphicAudioBrand(edition.Subtitle) ||
             GetProductionType(new[] { edition.Title, edition.Subtitle }, titleQualifier: true, editionClassification: true) != null ||
             GetProductionType((edition.NarratorNames ?? Enumerable.Empty<string>()).Append(edition.Narrator), editionClassification: true) != null ||
             HasFullCastNarrators(edition.NarratorNames));

        private static bool HasCastCredit(Edition edition) => edition != null &&
            (AudioProductionConstants.Matches(edition.Narrator, kind: AudioProductionConstants.EvidenceKind.CastCredit) ||
             edition.NarratorNames?.Any(name => AudioProductionConstants.Matches(name,
                 kind: AudioProductionConstants.EvidenceKind.CastCredit)) == true);

        // Explicit brand text is evidence; legacy boolean/style fields and parent folders are not.
        public static bool HasGraphicAudioBrand(BookFile file, Edition edition = null) =>
            file?.AudioProductionOverride != false &&
            (ContainsGraphicAudioBrand((edition ?? file?.Edition)?.Publisher) ||
             Flatten(file?.AllTags).Any(ContainsGraphicAudioBrand) ||
             ContainsGraphicAudioBrand(file?.SceneName) ||
             (file?.AllTags?.Values.Any(values => values?.Any(value => !string.IsNullOrWhiteSpace(value)) == true) != true &&
              (NameEvidence(file?.Path, null).Any(ContainsGraphicAudioBrand) ||
               file?.MatchProvenance?.ImportSourcePaths?.SelectMany(path => NameEvidence(path, null))
                   .Any(ContainsGraphicAudioBrand) == true)));

        public static bool IsCastLabel(string name) => AudioProductionConstants.Matches(name, castLabel: true);

        public static bool IsProductionLabel(string value) => AudioProductionConstants.Matches(value, exact: true, titleQualifier: true);

        public static bool IsDramatizedOrFullCast(bool isGraphicAudio, string audioProductionType, params string[] fields) =>
            IsDramatizedOrFullCast(isGraphicAudio, audioProductionType, fields?.AsEnumerable());

        public static bool IsDramatizedOrFullCast(bool isGraphicAudio, string audioProductionType, IEnumerable<string> fields) =>
            isGraphicAudio || ContainsIndicator(new[] { audioProductionType }) || ContainsIndicator(fields);

        public static bool IsDramatizedOrFullCast(IDictionary<string, List<string>> tags) => ContainsIndicator(Flatten(tags));

        public static bool ContainsIndicator(IEnumerable<string> fields) =>
            fields?.Any(field => AudioProductionConstants.Matches(field)) == true;

        public static bool IsDramatizedFile(bool isGraphicAudio, string audioProductionType,
            IDictionary<string, List<string>> tags, string path, string importRoot = null) =>
            IsDramatizedOrFullCast(isGraphicAudio, audioProductionType, Flatten(tags)) ||
            NameEvidence(path, importRoot).Any(value => AudioProductionConstants.Matches(value, nameEvidence: true));

        public static bool IsDramatized(LocalBook file) => file != null &&
            (file.AudioProductionOverride ?? Detect(file));

        public static bool Detect(LocalBook file) => file != null && Effective(file.IsGraphicAudio, file.AudioProductionType, file.Edition,
            file.RawTags?.AllTags, file.Path, file.ImportRoot, file.SceneName, file.MatchProvenance?.ImportSourcePaths);

        public static bool IsDramatized(BookFile file, Edition edition = null) => file != null &&
            (file.AudioProductionOverride ?? Effective(file.IsGraphicAudio, file.AudioProductionType, edition ?? file.Edition,
                file.AllTags, file.Path, null, file.SceneName, file.MatchProvenance?.ImportSourcePaths));

        private static bool IsAudioFile(LocalBook file) => file != null &&
            (QualityMediaTypeHelper.GetKnownMediaType(file.Quality?.Quality ?? Quality.Unknown) ??
             QualityMediaTypeHelper.GetMediaTypeFromPath(file.Path)) == BookMediaType.Audiobook;

        public static bool DetectRecording(LocalBook file) => IsAudioFile(file) &&
            Effective(file.IsGraphicAudio, file.AudioProductionType, null, file.RawTags?.AllTags,
                file.Path, file.ImportRoot, file.SceneName, file.MatchProvenance?.ImportSourcePaths);

        public static string GetProductionType(IEnumerable<string> fields, bool titleQualifier = false, bool editionClassification = false)
        {
            var evidence = fields?.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray() ?? Array.Empty<string>();
            if (evidence.Any(value => AudioProductionConstants.Matches(value, kind: AudioProductionConstants.EvidenceKind.DramatizedPhrase,
                titleQualifier: titleQualifier, editionClassification: editionClassification))) return "Dramatized";
            return evidence.Any(value => AudioProductionConstants.Matches(value, kind: AudioProductionConstants.EvidenceKind.CastCredit,
                titleQualifier: titleQualifier, editionClassification: editionClassification)) ? "Full Cast" : null;
        }

        public static string GetRecordingProductionType(LocalBook file) => !IsAudioFile(file) ? null :
            GetProductionType(Flatten(file.RawTags?.AllTags).Concat(new[] { file.AudioProductionType, file.SceneName })
                .Concat(NameEvidence(file.Path, file.ImportRoot)
                    .Concat(file.MatchProvenance?.ImportSourcePaths?.SelectMany(path => NameEvidence(path, null)) ?? Enumerable.Empty<string>())
                    .Where(value => AudioProductionConstants.Matches(value, nameEvidence: true))));

        // Replacement compares the recordings themselves, not the metadata shared by their edition.
        public static bool IsDramatizedRecording(LocalBook file) => file != null &&
            (file.AudioProductionOverride ?? DetectRecording(file));

        public static bool IsDramatizedRecording(BookFile file) => file != null &&
            (file.AudioProductionOverride ?? Effective(file.IsGraphicAudio, file.AudioProductionType, null,
                file.AllTags, file.Path, null, file.SceneName, file.MatchProvenance?.ImportSourcePaths));

        private static bool Effective(bool stored, string productionType, Edition edition, IDictionary<string, List<string>> tags,
            string path, string importRoot, string sceneName, IEnumerable<string> sourcePaths) =>
            IsDramatizedFile(stored || edition?.IsGraphicAudio == true, productionType, tags, path, importRoot) ||
            IsDramatizedOrFullCast(false, edition?.AudioProductionType, sceneName) ||
            (sourcePaths?.SelectMany(source => NameEvidence(source, null))
                .Any(value => AudioProductionConstants.Matches(value, nameEvidence: true)) == true);

        // Only a caller-supplied boundary authorizes folder evidence. Never infer a category/root from a path.
        private static IEnumerable<string> NameEvidence(string path, string importRoot)
        {
            if (string.IsNullOrWhiteSpace(path)) yield break;
            path = path.Replace('\\', '/');
            yield return Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrWhiteSpace(importRoot)) yield break;
            importRoot = importRoot.Replace('\\', '/').TrimEnd('/');
            var folder = Path.GetDirectoryName(path);
            while (folder != null && importRoot.IsParentPath(folder))
            {
                yield return Path.GetFileName(folder);
                folder = Path.GetDirectoryName(folder);
            }
        }

        public static IEnumerable<string> Flatten(IDictionary<string, List<string>> tags) => tags == null
            ? Enumerable.Empty<string>()
            : tags.Where(kvp => kvp.Value != null).SelectMany(kvp => kvp.Value.Append(kvp.Key))
                .Where(value => !string.IsNullOrWhiteSpace(value));
    }
}
