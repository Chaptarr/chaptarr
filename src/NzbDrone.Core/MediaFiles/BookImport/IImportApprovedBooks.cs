using System.Collections.Generic;
using System.Threading;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles.BookImport
{
    public interface IImportApprovedBooks
    {
        List<ImportResult> Import(List<ImportDecision<LocalBook>> decisions, bool replaceExisting, DownloadClientItem downloadClientItem = null, ImportMode importMode = ImportMode.Auto, CancellationToken cancellationToken = default);

        // chaptarr #184: exposes the same "does a tracked file already occupy this book's managed
        // destination" check that Import() only runs at actual-import time, so callers that need to
        // warn about it earlier (the Manual Import preview) don't have to duplicate the logic. Returns
        // null when there's no conflict. Non-mutating - safe to call for a preview.
        string CheckExistingDestinationConflict(NzbDrone.Core.Parser.Model.LocalBook localBook, NzbDrone.Core.Books.Book book, NzbDrone.Core.Books.Author author);
    }
}
