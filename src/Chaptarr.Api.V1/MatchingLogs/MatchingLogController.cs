using System;
using System.IO;
using System.Linq;
using Chaptarr.Api.V1.MediaTypes;
using Chaptarr.Http;
using Chaptarr.Http.REST;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Commands;

namespace Chaptarr.Api.V1.MatchingLogs
{
    [V1ApiController]
    public class MatchingLogController : Controller
    {
        private const int DefaultMinutesBack = 30;
        private const int DefaultMaxEntries = 1000;
        private const int PreviewSampleCount = 3;

        private readonly IMatchingLogsCollector _matchingLogsCollector;

        public MatchingLogController(IMatchingLogsCollector matchingLogsCollector)
        {
            _matchingLogsCollector = matchingLogsCollector;
        }

        [HttpPost("preview")]
        public MatchingLogPreviewResource Preview([FromBody] SendMatchingLogsCommand request)
        {
            request ??= new SendMatchingLogsCommand();

            if (request.MaxEntries <= 0)
            {
                request.MaxEntries = DefaultMaxEntries;
            }

            if (!request.MinutesBack.HasValue && request.DaysBack <= 0)
            {
                request.MinutesBack = DefaultMinutesBack;
            }

            ValidateUnmappedFilesSelection(request);

            var logs = _matchingLogsCollector.Collect(request);
            var samples = logs
                .Take(PreviewSampleCount)
                .Select(ToPreviewEntry)
                .ToList();

            return new MatchingLogPreviewResource
            {
                TotalEntries = logs.Count,
                SampleCount = samples.Count,
                MaxEntries = request.MaxEntries,
                MinutesBack = request.MinutesBack ?? 0,
                FailedMatchesOnly = request.FailedMatchesOnly,
                MediaType = request.MediaType,
                Scope = request.UnmappedFiles?.Scope ?? (request.SpecificFilePaths?.Any() == true ? "specific-paths" : "recent"),
                Samples = samples
            };
        }

        private static MatchingLogPreviewEntryResource ToPreviewEntry(MatchingLogEntry log)
        {
            var result = log.MatchResult;
            var rejection = result?.Rejections?.FirstOrDefault();

            return new MatchingLogPreviewEntryResource
            {
                Timestamp = log.Timestamp,
                Path = log.FileName,
                FileName = Path.GetFileName(log.FileName),
                MediaType = log.MediaType,
                Success = result?.Success ?? false,
                Reason = result?.Reason,
                Decision = result?.Decision,
                AuthorMatched = result?.AuthorMatched ?? result?.MatchedAuthor,
                BookMatched = result?.BookMatched ?? result?.MatchedBook,
                EditionMatched = result?.EditionMatched ?? result?.MatchedEdition,
                MatchedVia = result?.MatchedVia,
                MatchedEditionTitle = result?.MatchedEditionTitle,
                TopRejectionReason = rejection?.Reason,
                TopRejectionDetail = rejection?.Detail,
                TopRejectionTitle = rejection?.TitleSnippet,
                UploadEntryJson = JsonConvert.SerializeObject(SendMatchingLogsCommandHandler.BuildUploadEntry(log), Formatting.Indented),
                Tags = SendMatchingLogsCommandHandler.FilterTagsForUpload(log.ExtractedTags)
            };
        }

        private static void ValidateUnmappedFilesSelection(SendMatchingLogsCommand request)
        {
            var selection = request.UnmappedFiles;
            if (selection == null)
            {
                return;
            }

            var scope = selection.Scope?.Trim();
            if (string.IsNullOrWhiteSpace(scope))
            {
                throw new BadRequestException("unmappedFiles.scope must be provided");
            }

            selection.Scope = scope;

            if (!string.Equals(scope, "all", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(scope, "selected", StringComparison.OrdinalIgnoreCase))
            {
                throw new BadRequestException("unmappedFiles.scope must be either 'all' or 'selected'");
            }

            request.MediaType = MediaTypeParameterParser.NormalizeOptional(request.MediaType);

            if (string.Equals(scope, "selected", StringComparison.OrdinalIgnoreCase) &&
                !(selection.BookFileIds?.Any(id => id > 0) ?? false))
            {
                throw new BadRequestException("unmappedFiles.bookFileIds must be provided when scope is 'selected'");
            }
        }
    }
}
