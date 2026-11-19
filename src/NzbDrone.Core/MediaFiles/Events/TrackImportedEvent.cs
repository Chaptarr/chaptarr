using System;
using System.Collections.Generic;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Download;
using NzbDrone.Core.History;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles.Events
{
    // Scoped to one import operation; never a process-wide history cache. Keeping the same rows
    // also preserves alternate-grab mutations across later per-file events in this batch.
    public sealed class ImportHistoryContext
    {
        private readonly Dictionary<string, List<EntityHistory>> _history = new(StringComparer.OrdinalIgnoreCase);

        public ImportHistoryContext(string downloadId, List<EntityHistory> history = null)
        {
            Seed(downloadId, history);
        }

        public void Seed(string downloadId, List<EntityHistory> history)
        {
            if (!string.IsNullOrWhiteSpace(downloadId) && history != null)
            {
                _history.TryAdd(downloadId, history);
            }
        }

        public List<EntityHistory> Get(string downloadId, Func<string, List<EntityHistory>> load)
        {
            if (!_history.TryGetValue(downloadId, out var history))
            {
                history = load(downloadId) ?? new List<EntityHistory>();
                _history.Add(downloadId, history);
            }

            return history;
        }
    }

    public class TrackImportedEvent : IEvent
    {
        public LocalBook BookInfo { get; private set; }
        public BookFile ImportedBook { get; private set; }
        public List<BookFile> OldFiles { get; private set; }
        public bool NewDownload { get; private set; }
        public DownloadClientItemClientInfo DownloadClientInfo { get; set; }
        public string DownloadId { get; private set; }
        public ImportHistoryContext HistoryContext { get; set; }
        public string Message { get; set; }

        public TrackImportedEvent(LocalBook bookInfo, BookFile importedBook, List<BookFile> oldFiles, bool newDownload, DownloadClientItem downloadClientItem)
        {
            BookInfo = bookInfo;
            ImportedBook = importedBook;
            OldFiles = oldFiles;
            NewDownload = newDownload;

            if (downloadClientItem != null)
            {
                DownloadClientInfo = downloadClientItem.DownloadClientInfo;
                DownloadId = downloadClientItem.DownloadId;
            }
        }
    }
}
