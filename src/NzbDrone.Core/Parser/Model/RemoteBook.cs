using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.History;
using NzbDrone.Core.Parser;

namespace NzbDrone.Core.Parser.Model
{
    public class RemoteBook
    {
        public ReleaseInfo Release { get; set; }
        public ParsedBookInfo ParsedBookInfo { get; set; }
        public Author Author { get; set; }
        public List<Book> Books { get; set; }
        public bool DownloadAllowed { get; set; }
        [JsonIgnore]
        public bool AlternativeCopies { get; set; }

        [JsonIgnore]
        public DateTime? AlternativeCopyGrabDate { get; set; }
        [JsonIgnore]
        public Dictionary<int, int> TargetEditionIds { get; set; } = new();
        [JsonIgnore]
        public Dictionary<int, int> TargetCustomFormatScores { get; set; } = new();

        [JsonIgnore]
        public IReadOnlySet<int> PositivelyIncompatibleBookIds { get; set; }

        [JsonIgnore]
        internal IReadOnlyDictionary<int, TitleMatchResult> MatchesByBook { get; set; }

        [JsonIgnore]
        public IReadOnlyDictionary<int, EntityHistory> PreparedBookHistory { get; set; }
        [JsonIgnore]
        public IReadOnlyDictionary<(int AuthorId, int BookId), DateTime> PreparedPendingReleaseDates { get; set; }
        public TorrentSeedConfiguration SeedConfiguration { get; set; }
        public List<CustomFormat> CustomFormats { get; set; }
        public int CustomFormatScore { get; set; }
        public ReleaseSourceType ReleaseSource { get; set; }
        public TitleMatchResult SearchCriteriaMatch { get; set; }
        public ReleasePackDetection PackDetection { get; set; }

        public RemoteBook()
        {
            Books = new List<Book>();
            CustomFormats = new List<CustomFormat>();
            PackDetection = ReleasePackDetection.None();
        }

        public RemoteBook ForBooks(IEnumerable<Book> books)
        {
            var copy = (RemoteBook)MemberwiseClone();
            copy.Books = books.Where(book => book != null).ToList();
            var ids = copy.Books.Select(book => book.Id).ToHashSet();
            copy.CustomFormats = CustomFormats?.ToList() ?? new List<CustomFormat>();
            copy.TargetEditionIds = TargetEditionIds.Where(pair => ids.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value);
            copy.TargetCustomFormatScores = TargetCustomFormatScores.Where(pair => ids.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value);
            if (copy.Books.Count == 1 && MatchesByBook != null && MatchesByBook.TryGetValue(copy.Books[0].Id, out var match))
            {
                copy.SearchCriteriaMatch = match;
            }

            return copy;
        }

        internal static List<EntityHistory> GetCapturedGrabHistory(IEnumerable<EntityHistory> history)
        {
            // Included-media history links acquisition to the bonus row without changing the grabbed targets.
            var grabs = history.Where(row => row.EventType == EntityHistoryEventType.Grabbed && row.GetDataValue("IncludedFromGrabId") == null).ToList();
            var latest = grabs.OrderByDescending(row => row.Id).FirstOrDefault();
            if (latest?.GetDataValue(EntityHistory.ALTERNATIVE_COPIES) != null)
            {
                return grabs.Where(row => row.Date == latest.Date).OrderByDescending(row => row.Id).ToList();
            }

            return grabs.OrderByDescending(row => row.Date).ToList();
        }

        internal void RestoreGrabbedTargets(IEnumerable<EntityHistory> history)
        {
            var captured = GetCapturedGrabHistory(history);
            AlternativeCopies = captured.Count > 0 && bool.TryParse(captured[0].GetDataValue(EntityHistory.ALTERNATIVE_COPIES), out var alternatives) && alternatives;
            AlternativeCopyGrabDate = AlternativeCopies ? captured[0].Date : null;
            var targets = Books.Select(book => book.Id).ToHashSet();
            var byBook = captured.Where(row => targets.Contains(row.BookId)).GroupBy(row => row.BookId).Select(group => group.First()).ToList();
            TargetEditionIds = byBook.ToDictionary(row => row.BookId, row => row.EditionId);
            TargetCustomFormatScores = byBook.ToDictionary(row => row.BookId,
                row => int.TryParse(row.GetDataValue("CustomFormatScore"), out var score) ? score : 0);
        }

        public bool IsRecentBook()
        {
            return Books.Any(e => e.ReleaseDate >= DateTime.UtcNow.Date.AddDays(-14));
        }

        public override string ToString()
        {
            return Release.Title;
        }
    }

    public enum ReleaseSourceType
    {
        Unknown = 0,
        Rss = 1,
        Search = 2,
        UserInvokedSearch = 3,
        InteractiveSearch = 4,
        ReleasePush = 5
    }
}
