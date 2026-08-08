using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Processes;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Books;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Notifications.CustomScript
{
    public class CustomScript : NotificationBase<CustomScriptSettings>
    {
        private readonly IDiskProvider _diskProvider;
        private readonly IProcessProvider _processProvider;
        private readonly IEditionService _editionService;
        private readonly Logger _logger;

        public CustomScript(IDiskProvider diskProvider, IProcessProvider processProvider, IEditionService editionService, Logger logger)
        {
            _diskProvider = diskProvider;
            _processProvider = processProvider;
            _editionService = editionService;
            _logger = logger;
        }

        public override string Name => "Custom Script";

        public override string Link => "https://discord.gg/nqFGsGUug2";

        public override ProviderMessage Message => new ProviderMessage("Testing will execute the script with the EventType set to Test, ensure your script handles this correctly", ProviderMessageType.Warning);

        public override void OnGrab(GrabMessage message)
        {
            var author = message.Author;
            var remoteBook = message.RemoteBook;
            var releaseGroup = remoteBook.ParsedBookInfo.ReleaseGroup;
            var environmentVariables = new StringDictionary();

            EnsureEditionsLoaded(remoteBook.Books);

            environmentVariables.Add("Chaptarr_EventType", "Grab");
            environmentVariables.Add("Chaptarr_Author_Id", author.Id.ToString());
            environmentVariables.Add("Chaptarr_Author_Name", author.Name);
            environmentVariables.Add("Chaptarr_Author_GRId", author.GoodreadsAuthorId?.ToString() ?? string.Empty);
            environmentVariables.Add("Chaptarr_Release_BookCount", remoteBook.Books.Count.ToString());
            environmentVariables.Add("Chaptarr_Release_BookReleaseDates", string.Join(",", remoteBook.Books.Select(e => e.ReleaseDate)));
            environmentVariables.Add("Chaptarr_Release_BookTitles", string.Join("|", remoteBook.Books.Select(e => e.Title)));
            environmentVariables.Add("Chaptarr_Release_BookIds", string.Join("|", remoteBook.Books.Select(e => e.Id.ToString())));
            environmentVariables.Add("Chaptarr_Release_GRIds", remoteBook.Books.ConcatToString(GetMonitoredEditionForeignId, "|"));
            environmentVariables.Add("Chaptarr_Release_Title", remoteBook.Release.Title);
            environmentVariables.Add("Chaptarr_Release_Indexer", remoteBook.Release.Indexer ?? string.Empty);
            environmentVariables.Add("Chaptarr_Release_Size", remoteBook.Release.Size.ToString());
            environmentVariables.Add("Chaptarr_Release_Quality", remoteBook.ParsedBookInfo.Quality.Quality.Name);
            environmentVariables.Add("Chaptarr_Release_QualityVersion", remoteBook.ParsedBookInfo.Quality.Revision.Version.ToString());
            environmentVariables.Add("Chaptarr_Release_ReleaseGroup", releaseGroup ?? string.Empty);
            environmentVariables.Add("Chaptarr_Release_IndexerFlags", remoteBook.Release.IndexerFlags.ToString());
            environmentVariables.Add("Chaptarr_Download_Client", message.DownloadClientName ?? string.Empty);
            environmentVariables.Add("Chaptarr_Download_Client_Type", message.DownloadClientType ?? string.Empty);
            environmentVariables.Add("Chaptarr_Download_Id", message.DownloadId ?? string.Empty);

            ExecuteScript(environmentVariables);
        }

        public override void OnReleaseImport(BookDownloadMessage message)
        {
            var author = message.Author;
            var book = message.Book;
            var environmentVariables = new StringDictionary();
            var editionId = message.BookFiles?.FirstOrDefault()?.Edition?.ForeignEditionId;

            if (editionId.IsNullOrWhiteSpace())
            {
                editionId = GetMonitoredEditionForeignId(book);
            }

            environmentVariables.Add("Chaptarr_EventType", "Download");
            environmentVariables.Add("Chaptarr_Author_Id", author.Id.ToString());
            environmentVariables.Add("Chaptarr_Author_Name", author.Name);
            environmentVariables.Add("Chaptarr_Author_Path", author.Path);
            environmentVariables.Add("Chaptarr_Author_GRId", author.GoodreadsAuthorId?.ToString() ?? string.Empty);
            environmentVariables.Add("Chaptarr_Book_Id", book.Id.ToString());
            environmentVariables.Add("Chaptarr_Book_Title", book.Title);
            environmentVariables.Add("Chaptarr_Book_GRId", editionId);
            environmentVariables.Add("Chaptarr_Book_ReleaseDate", book.ReleaseDate.ToString());
            environmentVariables.Add("Chaptarr_Download_Client", message.DownloadClientInfo?.Name ?? string.Empty);
            environmentVariables.Add("Chaptarr_Download_Client_Type", message.DownloadClientInfo?.Type ?? string.Empty);
            environmentVariables.Add("Chaptarr_Download_Id", message.DownloadId ?? string.Empty);

            if (message.BookFiles.Any())
            {
                environmentVariables.Add("Chaptarr_AddedBookPaths", string.Join("|", message.BookFiles.Select(e => e.Path)));
            }

            if (message.OldFiles.Any())
            {
                environmentVariables.Add("Chaptarr_DeletedPaths", string.Join("|", message.OldFiles.Select(e => e.Path)));
                environmentVariables.Add("Chaptarr_DeletedDateAdded", string.Join("|", message.OldFiles.Select(e => e.DateAdded)));
            }

            ExecuteScript(environmentVariables);
        }

        public override void OnRename(Author author, List<RenamedBookFile> renamedFiles)
        {
            var environmentVariables = new StringDictionary();

            environmentVariables.Add("Chaptarr_EventType", "Rename");
            environmentVariables.Add("Chaptarr_Author_Id", author.Id.ToString());
            environmentVariables.Add("Chaptarr_Author_Name", author.Name);
            environmentVariables.Add("Chaptarr_Author_Path", author.Path);
            environmentVariables.Add("Chaptarr_Author_GRId", author.GoodreadsAuthorId?.ToString() ?? string.Empty);

            ExecuteScript(environmentVariables);
        }

        public override void OnAuthorAdded(Author author)
        {
            var environmentVariables = new StringDictionary();

            environmentVariables.Add("Chaptarr_EventType", "AuthorAdded");
            environmentVariables.Add("Chaptarr_Author_Id", author.Id.ToString());
            environmentVariables.Add("Chaptarr_Author_Name", author.Name);
            environmentVariables.Add("Chaptarr_Author_Path", author.Path);
            environmentVariables.Add("Chaptarr_Author_GRId", author.GoodreadsAuthorId?.ToString() ?? string.Empty);

            ExecuteScript(environmentVariables);
        }

        public override void OnBookAdded(Book book)
        {
            var author = book.Author;
            var environmentVariables = new StringDictionary();

            environmentVariables.Add("Chaptarr_EventType", "BookAdded");
            environmentVariables.Add("Chaptarr_Author_Id", author.Id.ToString());
            environmentVariables.Add("Chaptarr_Author_Name", author.Name);
            environmentVariables.Add("Chaptarr_Author_Path", author.Path);
            environmentVariables.Add("Chaptarr_Author_GoodreadsId", author.GoodreadsAuthorId ?? string.Empty);
            environmentVariables.Add("Chaptarr_Book_Id", book.Id.ToString());
            environmentVariables.Add("Chaptarr_Book_Title", book.Title);
            environmentVariables.Add("Chaptarr_Book_GoodreadsId", book.GoodreadsBookId ?? book.GoodreadsWorkId ?? string.Empty);

            ExecuteScript(environmentVariables);
        }

        public override void OnAuthorDelete(AuthorDeleteMessage deleteMessage)
        {
            var author = deleteMessage.Author;
            var environmentVariables = new StringDictionary();

            environmentVariables.Add("Chaptarr_EventType", "AuthorDelete");
            environmentVariables.Add("Chaptarr_Author_Id", author.Id.ToString());
            environmentVariables.Add("Chaptarr_Author_Name", author.Name);
            environmentVariables.Add("Chaptarr_Author_Path", author.Path);
            environmentVariables.Add("Chaptarr_Author_GoodreadsId", author.GoodreadsAuthorId ?? string.Empty);
            environmentVariables.Add("Chaptarr_Author_DeletedFiles", deleteMessage.DeletedFiles.ToString());

            ExecuteScript(environmentVariables);
        }

        public override void OnBookDelete(BookDeleteMessage deleteMessage)
        {
            var author = deleteMessage.Book.Author;
            var book = deleteMessage.Book;

            var environmentVariables = new StringDictionary();

            environmentVariables.Add("Chaptarr_EventType", "BookDelete");
            environmentVariables.Add("Chaptarr_Author_Id", author.Id.ToString());
            environmentVariables.Add("Chaptarr_Author_Name", author.Name);
            environmentVariables.Add("Chaptarr_Author_Path", author.Path);
            environmentVariables.Add("Chaptarr_Author_GoodreadsId", author.GoodreadsAuthorId ?? string.Empty);
            environmentVariables.Add("Chaptarr_Book_Id", book.Id.ToString());
            environmentVariables.Add("Chaptarr_Book_Title", book.Title);
            environmentVariables.Add("Chaptarr_Book_GoodreadsId", book.GoodreadsBookId ?? book.GoodreadsWorkId ?? string.Empty);
            environmentVariables.Add("Chaptarr_Book_DeletedFiles", deleteMessage.DeletedFiles.ToString());

            ExecuteScript(environmentVariables);
        }

        public override void OnBookFileDelete(BookFileDeleteMessage deleteMessage)
        {
            var author = deleteMessage.Book.Author;
            var book = deleteMessage.Book;
            var bookFile = deleteMessage.BookFile;
            var edition = bookFile.Edition;

            var environmentVariables = new StringDictionary();

            environmentVariables.Add("Chaptarr_EventType", "BookFileDelete");
            environmentVariables.Add("Chaptarr_Delete_Reason", deleteMessage.Reason.ToString());
            environmentVariables.Add("Chaptarr_Author_Id", author.Id.ToString());
            environmentVariables.Add("Chaptarr_Author_Name", author.Name);
            environmentVariables.Add("Chaptarr_Author_GoodreadsId", author.GoodreadsAuthorId ?? string.Empty);
            environmentVariables.Add("Chaptarr_Book_Id", book.Id.ToString());
            environmentVariables.Add("Chaptarr_Book_Title", book.Title);
            environmentVariables.Add("Chaptarr_Book_GoodreadsId", book.GoodreadsBookId ?? book.GoodreadsWorkId ?? string.Empty);
            environmentVariables.Add("Chaptarr_BookFile_Id", bookFile.Id.ToString());
            environmentVariables.Add("Chaptarr_BookFile_Path", bookFile.Path);
            environmentVariables.Add("Chaptarr_BookFile_Quality", bookFile.Quality.Quality.Name);
            environmentVariables.Add("Chaptarr_BookFile_QualityVersion", bookFile.Quality.Revision.Version.ToString());
            environmentVariables.Add("Chaptarr_BookFile_ReleaseGroup", bookFile.ReleaseGroup ?? string.Empty);
            environmentVariables.Add("Chaptarr_BookFile_SceneName", bookFile.SceneName ?? string.Empty);
            environmentVariables.Add("Chaptarr_BookFile_Edition_Id", edition.Id.ToString());
            environmentVariables.Add("Chaptarr_BookFile_Edition_Name", edition.Title);
            environmentVariables.Add("Chaptarr_BookFile_Edition_GoodreadsId", edition.ForeignEditionId);
            environmentVariables.Add("Chaptarr_BookFile_Edition_Isbn13", edition.Isbn13);
            environmentVariables.Add("Chaptarr_BookFile_Edition_Asin", edition.Asin);

            ExecuteScript(environmentVariables);
        }

        public override void OnBookRetag(BookRetagMessage message)
        {
            var author = message.Author;
            var book = message.Book;
            var bookFile = message.BookFile;
            var environmentVariables = new StringDictionary();
            var editionId = bookFile?.Edition?.ForeignEditionId;

            if (editionId.IsNullOrWhiteSpace())
            {
                editionId = GetMonitoredEditionForeignId(book);
            }

            environmentVariables.Add("Chaptarr_EventType", "TrackRetag");
            environmentVariables.Add("Chaptarr_Author_Id", author.Id.ToString());
            environmentVariables.Add("Chaptarr_Author_Name", author.Name);
            environmentVariables.Add("Chaptarr_Author_Path", author.Path);
            environmentVariables.Add("Chaptarr_Author_GRId", author.GoodreadsAuthorId?.ToString() ?? string.Empty);
            environmentVariables.Add("Chaptarr_Book_Id", book.Id.ToString());
            environmentVariables.Add("Chaptarr_Book_Title", book.Title);
            environmentVariables.Add("Chaptarr_Book_GRId", editionId);
            environmentVariables.Add("Chaptarr_Book_ReleaseDate", book.ReleaseDate.ToString());
            environmentVariables.Add("Chaptarr_BookFile_Id", bookFile.Id.ToString());
            environmentVariables.Add("Chaptarr_BookFile_Path", bookFile.Path);
            environmentVariables.Add("Chaptarr_BookFile_Quality", bookFile.Quality.Quality.Name);
            environmentVariables.Add("Chaptarr_BookFile_QualityVersion", bookFile.Quality.Revision.Version.ToString());
            environmentVariables.Add("Chaptarr_BookFile_ReleaseGroup", bookFile.ReleaseGroup ?? string.Empty);
            environmentVariables.Add("Chaptarr_BookFile_SceneName", bookFile.SceneName ?? string.Empty);
            environmentVariables.Add("Chaptarr_Tags_Diff", message.Diff.ToJson());
            environmentVariables.Add("Chaptarr_Tags_Scrubbed", message.Scrubbed.ToString());

            ExecuteScript(environmentVariables);
        }

        public override void OnHealthIssue(HealthCheck.HealthCheck healthCheck)
        {
            var environmentVariables = new StringDictionary();

            environmentVariables.Add("Chaptarr_EventType", "HealthIssue");
            environmentVariables.Add("Chaptarr_Health_Issue_Level", Enum.GetName(typeof(HealthCheckResult), healthCheck.Type));
            environmentVariables.Add("Chaptarr_Health_Issue_Message", healthCheck.Message);
            environmentVariables.Add("Chaptarr_Health_Issue_Type", healthCheck.Source.Name);
            environmentVariables.Add("Chaptarr_Health_Issue_Wiki", healthCheck.WikiUrl.ToString() ?? string.Empty);

            ExecuteScript(environmentVariables);
        }

        public override void OnApplicationUpdate(ApplicationUpdateMessage updateMessage)
        {
            var environmentVariables = new StringDictionary();

            environmentVariables.Add("Chaptarr_EventType", "ApplicationUpdate");
            environmentVariables.Add("Chaptarr_Update_Message", updateMessage.Message);
            environmentVariables.Add("Chaptarr_Update_NewVersion", updateMessage.NewVersion.ToString());
            environmentVariables.Add("Chaptarr_Update_PreviousVersion", updateMessage.PreviousVersion.ToString());

            ExecuteScript(environmentVariables);
        }

        public override ValidationResult Test()
        {
            var failures = new List<ValidationFailure>();

            if (!_diskProvider.FileExists(Settings.Path))
            {
                failures.Add(new NzbDroneValidationFailure("Path", "File does not exist"));
            }

            if (failures.Empty())
            {
                try
                {
                    var environmentVariables = new StringDictionary();
                    environmentVariables.Add("Chaptarr_EventType", "Test");

                    var processOutput = ExecuteScript(environmentVariables);

                    if (processOutput.ExitCode != 0)
                    {
                        failures.Add(new NzbDroneValidationFailure(string.Empty, $"Script exited with code: {processOutput.ExitCode}"));
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex);
                    failures.Add(new NzbDroneValidationFailure(string.Empty, ex.Message));
                }
            }

            return new ValidationResult(failures);
        }

        private void EnsureEditionsLoaded(List<Book> books)
        {
            if (books == null || books.Count == 0)
            {
                return;
            }

            var booksNeedingEditions = books
                .Where(book => book != null && book.Id > 0 && book.Editions == null)
                .ToList();

            if (booksNeedingEditions.Count == 0)
            {
                return;
            }

            var bookIds = booksNeedingEditions.Select(book => book.Id).Distinct().ToList();
            var editions = _editionService.GetEditionsByBook(bookIds) ?? new List<Edition>();

            var editionsByBook = editions
                .GroupBy(edition => edition.BookId)
                .ToDictionary(group => group.Key, group => group.ToList());

            foreach (var book in booksNeedingEditions)
            {
                book.Editions = editionsByBook.TryGetValue(book.Id, out var bookEditions)
                    ? bookEditions
                    : new List<Edition>();
            }
        }

        private static string GetMonitoredEditionForeignId(Book book)
        {
            var editions = book?.Editions;
            if (editions == null)
            {
                return string.Empty;
            }

            return editions.FirstOrDefault(edition => edition?.Monitored == true)?.ForeignEditionId
                ?? editions.FirstOrDefault(edition => edition != null)?.ForeignEditionId
                ?? string.Empty;
        }

        private static void AddLegacyReadarrEnvironmentVariables(StringDictionary environmentVariables)
        {
            if (environmentVariables == null || environmentVariables.Count == 0)
            {
                return;
            }

            var keys = environmentVariables.Keys.Cast<string>().ToList();
            foreach (var key in keys)
            {
                if (!key.StartsWith("Chaptarr_", StringComparison.Ordinal))
                {
                    continue;
                }

                var legacyKey = "Readarr_" + key.Substring("Chaptarr_".Length);
                if (!environmentVariables.ContainsKey(legacyKey))
                {
                    environmentVariables.Add(legacyKey, environmentVariables[key]);
                }
            }
        }

        private ProcessOutput ExecuteScript(StringDictionary environmentVariables)
        {
            AddLegacyReadarrEnvironmentVariables(environmentVariables);

            _logger.Debug("Executing external script: {0}", Settings.Path);

            // Custom script arguments are intentionally no longer supported (validator enforces empty),
            // but old DB values could still exist. Do not execute any persisted arguments.
            var processOutput = _processProvider.StartAndCapture(Settings.Path, null, environmentVariables);

            _logger.Debug("Executed external script: {0} - Status: {1}", Settings.Path, processOutput.ExitCode);
            _logger.Debug($"Script Output: {System.Environment.NewLine}{string.Join(System.Environment.NewLine, processOutput.Lines)}");

            return processOutput;
        }
    }
}
