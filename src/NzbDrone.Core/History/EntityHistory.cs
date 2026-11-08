using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.History
{
    public class EntityHistory : ModelBase
    {
        public const string DOWNLOAD_CLIENT = "downloadClient";
        public const string RELEASE_SOURCE = "releaseSource";
        public const string RELEASE_GROUP = "releaseGroup";
        public const string SIZE = "size";
        public const string INDEXER = "indexer";
        public const string ALTERNATIVE_COPIES = "AlternativeCopies";
        public const string INDEXER_ID = "IndexerId";
        public const string ALTERNATIVE_COPY_INCOMPATIBLE = "alternativeCopyIncompatible";

        public EntityHistory()
        {
            Data = new Dictionary<string, string>();
        }

        public int BookId { get; set; }
        public int AuthorId { get; set; }
        public int EditionId { get; set; }
        public string SourceTitle { get; set; }
        public QualityModel Quality { get; set; }
        public DateTime Date { get; set; }
        public Book Book { get; set; }
        public Author Author { get; set; }
        public EntityHistoryEventType EventType { get; set; }
        public Dictionary<string, string> Data { get; set; }

        public string DownloadId { get; set; }

        public string GetDataValue(string key)
        {
            TryGetDataValue(Data, key, out var value);
            return value;
        }

        internal static bool TryGetDataValue(IDictionary<string, string> data, string key, out string value)
        {
            value = null;
            if (data == null)
            {
                return false;
            }

            if (data.TryGetValue(key, out value))
            {
                return true;
            }

            var matched = data.FirstOrDefault(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase));
            value = matched.Value;
            return matched.Key != null;
        }
    }

    public enum EntityHistoryEventType
    {
        Unknown = 0,
        Grabbed = 1,
        BookFileImported = 3,
        DownloadFailed = 4,
        BookFileDeleted = 5,
        BookFileRenamed = 6,
        BookImportIncomplete = 7,
        DownloadImported = 8,
        BookFileRetagged = 9,
        DownloadIgnored = 10,
        BookFileConverted = 11,
        BookFileConversionFailed = 12,
        AuthorsMerged = 13,
        AuthorSplit = 14
    }
}
