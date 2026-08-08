using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Books;
using NzbDrone.Core.History;
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

        public List<CustomFormat> ParseCustomFormat(RemoteBook remoteBook, long size)
        {
            var mediaType = ResolveRemoteBookMediaType(remoteBook);
            var isAudiobook = mediaType == BookMediaType.Audiobook;
            var input = new CustomFormatInput
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
                    remoteBook.ParsedBookInfo?.ReleaseTitle,
                    remoteBook.ParsedBookInfo?.BookTitle,
                    remoteBook.ParsedBookInfo?.Narrator,
                    remoteBook.ParsedBookInfo?.AudioProductionType) : new List<string>()
            };

            ApplyPreferredNarratorTarget(input, remoteBook.Books);

            return ParseCustomFormat(input);
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
                MediaType = QualityMediaTypeHelper.DetectMediaType(blocklist.Quality?.Quality ?? Quality.Unknown, blocklist.SourceTitle),
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

            var input = new CustomFormatInput
            {
                BookInfo = bookInfo,
                Author = author,
                Size = size,
                IndexerFlags = indexerFlags,
                MediaType = QualityMediaTypeHelper.DetectMediaType(history.Quality?.Quality ?? Quality.Unknown, history.SourceTitle) ?? book?.MediaType,
                IsGraphicAudio = bool.TryParse(history.Data.GetValueOrDefault("IsGraphicAudio"), out var isGraphicAudio) && isGraphicAudio,
                Narrator = history.Data.GetValueOrDefault("Narrator"),
                AudioProductionFields = BuildAudioProductionFields(
                    history.SourceTitle,
                    history.Data.GetValueOrDefault("Narrator"),
                    history.Data.GetValueOrDefault("IsGraphicAudio"))
            };

            PreferredNarratorMatcher.ApplyTarget(input, book);

            return ParseCustomFormat(input);
        }

        public List<CustomFormat> ParseCustomFormat(LocalBook localBook)
        {
            var mediaType = QualityMediaTypeHelper.GetKnownMediaType(localBook.Quality?.Quality ?? Quality.Unknown) ?? QualityMediaTypeHelper.GetMediaTypeFromPath(localBook.Path);
            var isAudiobook = mediaType == BookMediaType.Audiobook;
            var bookInfo = new ParsedBookInfo
            {
                AuthorName = localBook.Author.Name,
                ReleaseTitle = localBook.SceneName,
                Quality = localBook.Quality,
                ReleaseGroup = localBook.ReleaseGroup
            };

            var input = new CustomFormatInput
            {
                BookInfo = bookInfo,
                Author = localBook.Author,
                Size = localBook.Size,
                IndexerFlags = localBook.IndexerFlags,
                MediaType = mediaType,
                IsGraphicAudio = isAudiobook && AudioProductionDetector.IsDramatizedOrFullCast(localBook.RawTags?.AllTags),
                Narrator = localBook.Narrator,
                AudioProductionFields = isAudiobook ? BuildAudioProductionFields(
                    localBook.Path,
                    localBook.SceneName,
                    localBook.Narrator,
                    localBook.Edition?.Title,
                    localBook.Book?.Title,
                    AudioProductionDetector.Flatten(localBook.RawTags?.AllTags)) : new List<string>(),
            };

            PreferredNarratorMatcher.ApplyTarget(input, localBook.Book, localBook.Edition);

            return ParseCustomFormat(input);
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
            var releaseTitle = string.Empty;

            if (bookFile.SceneName.IsNotNullOrWhiteSpace())
            {
                _logger.Trace("Using scene name for release title: {0}", bookFile.SceneName);
                releaseTitle = bookFile.SceneName;
            }
            else if (bookFile.OriginalFilePath.IsNotNullOrWhiteSpace())
            {
                _logger.Trace("Using original file path for release title: {0}", bookFile.OriginalFilePath);
                releaseTitle = bookFile.OriginalFilePath;
            }
            else if (bookFile.Path.IsNotNullOrWhiteSpace())
            {
                _logger.Trace("Using path for release title: {0}", Path.GetFileName(bookFile.Path));
                releaseTitle = Path.GetFileName(bookFile.Path);
            }

            var bookInfo = new ParsedBookInfo
            {
                AuthorName = author.Name,
                ReleaseTitle = releaseTitle,
                Quality = bookFile.Quality,
                ReleaseGroup = bookFile.ReleaseGroup
            };

            var mediaType = QualityMediaTypeHelper.GetKnownMediaType(bookFile.Quality?.Quality ?? Quality.Unknown) ?? QualityMediaTypeHelper.GetMediaTypeFromPath(bookFile.Path);
            var isAudiobook = mediaType == BookMediaType.Audiobook;
            var input = new CustomFormatInput
            {
                BookInfo = bookInfo,
                Author = author,
                Size = bookFile.Size,
                IndexerFlags = bookFile.IndexerFlags,
                Filename = Path.GetFileName(bookFile.Path),
                MediaType = mediaType,
                IsGraphicAudio = isAudiobook && (bookFile.IsGraphicAudio || AudioProductionDetector.IsDramatizedOrFullCast(bookFile.AllTags)),
                AudioProductionType = isAudiobook ? bookFile.AudioProductionType : null,
                Narrator = bookFile.Narrator,
                AudioProductionFields = isAudiobook ? BuildAudioProductionFields(
                    bookFile.Path,
                    bookFile.SceneName,
                    bookFile.OriginalFilePath,
                    bookFile.Narrator,
                    bookFile.AudioProductionType,
                    AudioProductionDetector.Flatten(bookFile.AllTags)) : new List<string>()
            };

            PreferredNarratorMatcher.ApplyTarget(input, bookFile.Edition?.Book, bookFile.Edition);

            return ParseCustomFormat(input, allCustomFormats);
        }

        private static BookMediaType? ResolveRemoteBookMediaType(RemoteBook remoteBook)
        {
            var detected = QualityMediaTypeHelper.DetectMediaType(
                remoteBook.ParsedBookInfo?.Quality?.Quality ?? Quality.Unknown,
                remoteBook.Release);

            if (detected.HasValue)
            {
                return detected;
            }

            var bookMediaTypes = (remoteBook.Books ?? new List<Book>())
                .Where(book => book != null)
                .Select(book => book.MediaType)
                .Distinct()
                .ToList();

            return bookMediaTypes.Count == 1 ? bookMediaTypes[0] : null;
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
