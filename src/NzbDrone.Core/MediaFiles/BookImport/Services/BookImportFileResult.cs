using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.MediaFiles.BookImport.Services
{
    public sealed class BookImportFileResult
    {
        private BookImportFileResult(string path, ImportOutcome outcome, string reasonCode, int? bookFileId)
        {
            Path = path;
            Outcome = outcome;
            ReasonCode = reasonCode;
            BookFileId = bookFileId;
        }

        public string Path { get; }
        public ImportOutcome Outcome { get; }
        public string ReasonCode { get; }
        public int? BookFileId { get; }

        public bool IsApplied => Outcome == ImportOutcome.Imported;
        public bool IsHandled => IsApplied || Outcome == ImportOutcome.AlreadyLinked;

        public static BookImportFileResult Imported(string path, int? bookFileId)
        {
            return new BookImportFileResult(path, ImportOutcome.Imported, null, bookFileId);
        }

        public static BookImportFileResult AlreadyLinked(string path, int? bookFileId)
        {
            return new BookImportFileResult(path, ImportOutcome.AlreadyLinked, null, bookFileId);
        }

        public static BookImportFileResult Unmapped(string path, string reasonCode)
        {
            return new BookImportFileResult(path, ImportOutcome.Unmapped, reasonCode, null);
        }

        public static BookImportFileResult Failed(string path, string reasonCode)
        {
            return new BookImportFileResult(path, ImportOutcome.Failed, reasonCode, null);
        }
    }
}
