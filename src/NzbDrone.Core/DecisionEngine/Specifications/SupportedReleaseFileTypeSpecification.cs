using System.Linq;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.DecisionEngine.Specifications
{
    public class SupportedReleaseFileTypeSpecification : IDecisionEngineSpecification
    {
        private readonly Logger _logger;

        public SupportedReleaseFileTypeSpecification(Logger logger)
        {
            _logger = logger;
        }

        public SpecificationPriority Priority => SpecificationPriority.Parsing;
        public RejectionType Type => RejectionType.Permanent;

        public Decision IsSatisfiedBy(RemoteBook subject, SearchCriteriaBase searchCriteria)
        {
            var torrentInfo = subject?.Release as TorrentInfo;
            var requestedMediaType = GetRequestedMediaType(subject, searchCriteria);
            if (torrentInfo?.FileType != null &&
                requestedMediaType.HasValue &&
                ReleaseFileTypeCompatibility.TryGetMediaTypeMismatch(torrentInfo.FileType, requestedMediaType.Value, out var mismatchedFileType))
            {
                _logger.Debug("Rejecting release '{0}' because indexer file type '{1}' is not compatible with requested media type {2}",
                              subject.Release?.Title ?? "Unknown",
                              torrentInfo.FileType,
                              requestedMediaType.Value);

                return Decision.RejectHardFilter("File type {0} is not compatible with {1} request", "Format", mismatchedFileType, FormatMediaType(requestedMediaType.Value));
            }

            if (torrentInfo?.FileType != null &&
                ReleaseFileTypeCompatibility.TryGetKnownUnsupportedFileType(torrentInfo.FileType, out var unsupportedFileType))
            {
                _logger.Debug("Rejecting release '{0}' because indexer file type '{1}' is not supported for import",
                              subject.Release?.Title ?? "Unknown",
                              torrentInfo.FileType);

                return Decision.RejectHardFilter("Unsupported file type: {0}", "Format", unsupportedFileType);
            }

            // Some indexers omit structured FileType metadata. If the release title itself
            // advertises an unsupported payload extension (for example ".mkv"), this is not
            // an "unknown quality" case; Chaptarr cannot import that payload.
            if (ReleaseFileTypeCompatibility.TryGetKnownUnsupportedReleaseTitleFileType(subject?.Release?.Title, out unsupportedFileType))
            {
                _logger.Debug("Rejecting release '{0}' because release title includes unsupported file type '{1}'",
                              subject?.Release?.Title ?? "Unknown",
                              unsupportedFileType);

                return Decision.RejectHardFilter("Unsupported file type: {0}", "Format", unsupportedFileType);
            }

            return Decision.Accept();
        }

        private static BookMediaType? GetRequestedMediaType(RemoteBook subject, SearchCriteriaBase searchCriteria)
        {
            var criteriaMediaType = GetSingleMediaType(searchCriteria?.Books);
            if (criteriaMediaType.HasValue)
            {
                return criteriaMediaType;
            }

            return GetSingleMediaType(subject?.Books);
        }

        private static BookMediaType? GetSingleMediaType(System.Collections.Generic.IEnumerable<Book> books)
        {
            var mediaTypes = (books ?? Enumerable.Empty<Book>())
                .Where(book => book != null)
                .Select(book => book.MediaType)
                .Distinct()
                .ToList();

            return mediaTypes.Count == 1 ? mediaTypes[0] : null;
        }

        private static string FormatMediaType(BookMediaType mediaType)
        {
            return mediaType == BookMediaType.Ebook ? "ebook" : "audiobook";
        }
    }
}
