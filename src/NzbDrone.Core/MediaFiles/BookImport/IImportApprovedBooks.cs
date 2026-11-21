using System.Collections.Generic;
using System.Threading;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles.BookImport
{
    public interface IImportApprovedBooks
    {
        List<ImportResult> Import(List<ImportDecision<LocalBook>> decisions, bool replaceExisting, DownloadClientItem downloadClientItem = null, ImportMode importMode = ImportMode.Auto, CancellationToken cancellationToken = default);

        // Queues the conversion of one edition's library files as a library conversion job.
        LibraryConversionQueueResult QueueLibraryConversion(List<ImportDecision<LocalBook>> decisions, Author author, Book book);

        // Imports a finished library conversion: the converted file replaces the files it was made from.
        List<ImportResult> ImportLibraryConversion(ConversionJobRequest request, List<ImportDecision<LocalBook>> sourceDecisions);
    }

    public class LibraryConversionQueueResult
    {
        public ConversionJob Job { get; init; }
        public string Error { get; init; }
        public bool Queued => Job != null && Error == null;

        public static LibraryConversionQueueResult Refused(string error) => new() { Error = error };
    }
}
