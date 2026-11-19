using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Books;
using NzbDrone.Core.History;
using NzbDrone.Core.Download;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.CustomFormats
{
    public interface ICustomFormatCalculationService
    {
        List<CustomFormat> ParseCustomFormat(RemoteBook remoteBook, long size);
        List<CustomFormat> ParseCustomFormat(BookFile bookFile, Author artist);
        List<CustomFormat> ParseCustomFormat(BookFile bookFile);
        List<CustomFormat> ParseCustomFormat(Blocklist blocklist, Author artist);
        List<CustomFormat> ParseCustomFormat(EntityHistory history, Author artist);
        List<CustomFormat> ParseCustomFormat(LocalBook localBook);
    }

    public class CustomFormatCalculationService : ICustomFormatCalculationService
    {
        private readonly ICustomFormatService _formatService;
        private readonly Logger _logger;

        // Do not add dependencies here that transitively depend on IAuthorService.
        // FileNameBuilder resolves this singleton; AuthorService -> AuthorPathBuilder -> FileNameBuilder
        // -> CustomFormatCalculationService -> AuthorService will crash startup with a DryIoc recursion.
        public CustomFormatCalculationService(ICustomFormatService formatService, Logger logger)
        {
            _formatService = formatService;
            _logger = logger;
        }

        public static string GetKnownNarratorDisplay(RemoteBook remoteBook)
        {
            if (remoteBook?.Books?.Count != 1 || remoteBook.GetMediaType() != BookMediaType.Audiobook ||
                remoteBook.Release?.NarratorEvidenceFields?.Count > 0 != true)
            {
                return null;
            }

            var known = Parser.ReleaseIdentityEvidence.GetKnownNarrators(remoteBook.Books[0]).Where(name =>
                remoteBook.Release.NarratorEvidenceFields.Any(field =>
                    ReleaseNarratorEvidenceExtractor.ContainsKnownNarratorName(new[] { name }, field)))
                .Distinct(Parser.Parser.NarratorNameComparer).ToList();
            return known.Count > 0 ? string.Join(", ", known) : null;
        }

        public List<CustomFormat> ParseCustomFormat(RemoteBook remoteBook, long size)
        {
            var input = CreateReleaseInput(remoteBook, size);
            ApplyPreferredNarratorTarget(input, remoteBook.Books);
            return ParseCustomFormat(input);
        }

        internal static CustomFormatInput CreateReleaseInput(RemoteBook remoteBook, long size = 0)
        {
            var mediaType = remoteBook.GetMediaType();
            var isAudiobook = mediaType == BookMediaType.Audiobook;
            return new CustomFormatInput
            {
                BookInfo = remoteBook.ParsedBookInfo,
                Author = remoteBook.Author,
                Size = size,
                IndexerFlags = remoteBook.Release?.IndexerFlags ?? 0,
                MediaType = mediaType,
                IsGraphicAudio = isAudiobook && (remoteBook.Release?.IsGraphicAudio == true || remoteBook.ParsedBookInfo?.IsGraphicAudio == true),
                AudioProductionType = isAudiobook ? remoteBook.ParsedBookInfo?.AudioProductionType : null,
                Narrator = remoteBook.Release?.Narrator ?? remoteBook.ParsedBookInfo?.Narrator,
                AudioProductionFields = isAudiobook ? BuildAudioProductionFields(
                    remoteBook.Release?.Title,
                    remoteBook.Release?.Book,
                    remoteBook.Release?.Narrator,
                    remoteBook.Release?.NarratorEvidenceFields,
                    remoteBook.ParsedBookInfo?.ReleaseTitle,
                    remoteBook.ParsedBookInfo?.BookTitle,
                    remoteBook.ParsedBookInfo?.Narrator,
                    remoteBook.ParsedBookInfo?.AudioProductionType) : new List<string>()
            };
        }

        public List<CustomFormat> ParseCustomFormat(BookFile bookFile, Author author)
        {
            return ParseCustomFormat(bookFile, author, _formatService.All());
        }

        public List<CustomFormat> ParseCustomFormat(BookFile bookFile)
        {
            return ParseCustomFormat(bookFile, bookFile.Author, _formatService.All());
        }

        public List<CustomFormat> ParseCustomFormat(Blocklist blocklist, Author author)
        {
            var parsed = Parser.Parser.ParseBookTitle(blocklist.SourceTitle);

            var bookInfo = new ParsedBookInfo
            {
                AuthorName = author.Name,
                ReleaseTitle = parsed?.ReleaseTitle ?? blocklist.SourceTitle,
                Quality = blocklist.Quality,
                ReleaseGroup = parsed?.ReleaseGroup
            };

            var input = new CustomFormatInput
            {
                BookInfo = bookInfo,
                Author = author,
                Size = blocklist.Size ?? 0,
                IndexerFlags = blocklist.IndexerFlags,
                MediaType = QualityMediaTypeHelper.GetKnownMediaType(blocklist.Quality?.Quality),
                AudioProductionFields = BuildAudioProductionFields(blocklist.SourceTitle)
            };

            return ParseCustomFormat(input);
        }

        public List<CustomFormat> ParseCustomFormat(EntityHistory history, Author author)
        {
            var book = history.Book;
            var parsed = Parser.Parser.ParseBookTitle(history.SourceTitle);

            long.TryParse(history.Data.GetValueOrDefault("size"), out var size);
            Enum.TryParse(history.Data.GetValueOrDefault("indexerFlags"), true, out IndexerFlags indexerFlags);

            var bookInfo = new ParsedBookInfo
            {
                AuthorName = author.Name,
                ReleaseTitle = parsed?.ReleaseTitle ?? history.SourceTitle,
                Quality = history.Quality,
                ReleaseGroup = parsed?.ReleaseGroup,
            };

            var rawNarratorFields = history.GetDataValue(nameof(ReleaseInfo.NarratorEvidenceFields));
            var narratorFields = rawNarratorFields.IsNotNullOrWhiteSpace() && Json.TryDeserialize<List<string>>(rawNarratorFields, out var fields) ? fields : null;
            var input = new CustomFormatInput
            {
                BookInfo = bookInfo,
                Author = author,
                Size = size,
                IndexerFlags = indexerFlags,
                MediaType = book?.MediaType ?? QualityMediaTypeHelper.GetKnownMediaType(history.Quality?.Quality),
                IsGraphicAudio = bool.TryParse(history.Data.GetValueOrDefault("IsGraphicAudio"), out var isGraphicAudio) && isGraphicAudio,
                Narrator = history.Data.GetValueOrDefault("Narrator"),
                AudioProductionFields = BuildAudioProductionFields(
                    history.SourceTitle,
                    history.Data.GetValueOrDefault("Narrator"),
                    narratorFields,
                    history.Data.GetValueOrDefault("IsGraphicAudio"))
            };

            PreferredNarratorMatcher.ApplyTarget(input, book);

            return ParseCustomFormat(input);
        }

        public List<CustomFormat> ParseCustomFormat(LocalBook localBook)
        {
            return ParseCustomFormat(CreateLocalInput(localBook));
        }

        private static CustomFormatInput CreateLocalInput(LocalBook localBook, string originalFilePath = null)
        {
            var mediaType = QualityMediaTypeHelper.GetKnownMediaType(localBook.Quality?.Quality ?? Quality.Unknown) ?? QualityMediaTypeHelper.GetMediaTypeFromPath(localBook.Path);
            var isAudiobook = mediaType == BookMediaType.Audiobook;
            var isDramatized = isAudiobook && AudioProductionDetector.IsDramatized(localBook);
            var bookInfo = new ParsedBookInfo
            {
                AuthorName = localBook.Author.Name,
                ReleaseTitle = localBook.SceneName.IsNotNullOrWhiteSpace() ? localBook.SceneName
                    : originalFilePath.IsNotNullOrWhiteSpace() ? originalFilePath : Path.GetFileName(localBook.Path),
                Quality = localBook.Quality,
                ReleaseGroup = localBook.ReleaseGroup
            };

            var input = new CustomFormatInput
            {
                BookInfo = bookInfo,
                Author = localBook.Author,
                Size = localBook.Size,
                Filename = Path.GetFileName(localBook.Path),
                IndexerFlags = localBook.IndexerFlags,
                MediaType = mediaType,
                EffectiveAudioProduction = isDramatized,
                AudioProductionType = isAudiobook ? localBook.AudioProductionType : null,
                Narrator = localBook.Narrator,
                AudioProductionFields = isAudiobook ? BuildAudioProductionFields(
                    localBook.SceneName,
                    localBook.Narrator,
                    localBook.AudioProductionType,
                    localBook.Edition?.Title,
                    localBook.Book?.Title,
                    AudioProductionDetector.Flatten(localBook.RawTags?.AllTags)) : new List<string>(),
            };

            PreferredNarratorMatcher.ApplyTarget(input, localBook.Book, localBook.Edition);

            return input;
        }

        private List<CustomFormat> ParseCustomFormat(CustomFormatInput input)
        {
            return ParseCustomFormat(input, _formatService.All());
        }

        private static List<CustomFormat> ParseCustomFormat(CustomFormatInput input, List<CustomFormat> allCustomFormats)
        {
            var matches = new List<CustomFormat>();

            foreach (var customFormat in allCustomFormats)
            {
                if (!customFormat.AppliesToMediaType(input.MediaType))
                {
                    continue;
                }

                var specificationMatches = customFormat.Specifications
                    .GroupBy(t => t.GetType())
                    .Select(g => new SpecificationMatchesGroup
                    {
                        Matches = g.ToDictionary(t => t, t => t.IsSatisfiedBy(input))
                    })
                    .ToList();

                if (specificationMatches.All(x => x.DidMatch))
                {
                    matches.Add(customFormat);
                }
            }

            return matches.OrderBy(x => x.Name).ToList();
        }

        private List<CustomFormat> ParseCustomFormat(BookFile bookFile, Author author, List<CustomFormat> allCustomFormats)
        {
            var local = new LocalBook
            {
                Path = bookFile.Path, Size = bookFile.Size, Author = author,
                Book = bookFile.Edition?.Book, Edition = bookFile.Edition, Quality = bookFile.Quality,
                SceneName = bookFile.SceneName, ReleaseGroup = bookFile.ReleaseGroup, IndexerFlags = bookFile.IndexerFlags,
                IsGraphicAudio = bookFile.IsGraphicAudio, AudioProductionOverride = bookFile.AudioProductionOverride,
                AudioProductionType = bookFile.AudioProductionType, Narrator = bookFile.Narrator,
                RawTags = new RawFileTags { AllTags = bookFile.AllTags }, MatchProvenance = bookFile.MatchProvenance
            };
            return ParseCustomFormat(CreateLocalInput(local, bookFile.OriginalFilePath), allCustomFormats);
        }

        private static void ApplyPreferredNarratorTarget(CustomFormatInput input, IEnumerable<Book> books)
        {
            if (books == null)
            {
                return;
            }

            var targets = books
                .Where(book => book != null)
                .Select(book => PreferredNarratorMatcher.BuildTarget(book))
                .Where(target => target != null)
                .ToList();

            if (targets.Count != 1)
            {
                return;
            }

            PreferredNarratorMatcher.ApplyTarget(input, targets[0]);
        }

        private static List<string> BuildAudioProductionFields(params object[] values)
        {
            var fields = new List<string>();

            foreach (var value in values)
            {
                switch (value)
                {
                    case null:
                        continue;
                    case string text when !string.IsNullOrWhiteSpace(text):
                        fields.Add(text);
                        break;
                    case IEnumerable<string> strings:
                        fields.AddRange(strings.Where(s => !string.IsNullOrWhiteSpace(s)));
                        break;
                }
            }

            return fields;
        }
    }
}
