using System;
using NzbDrone.Core.Books;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.IndexerSearch
{
    public class InteractiveAuthorSearchCommand : Command
    {
        public override bool SupportsCancellation => true;
        public override bool IsTypeExclusive => true;

        public string AuthorName { get; set; }
        public int BooksDone { get; set; }
        public int BooksTotal { get; set; }
        public DateTime? ResultsExpireAt { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public string ProgressMessage => $"Searching {AuthorName ?? $"author {AuthorId}"} ({(MediaType == BookMediaType.Audiobook ? "Audiobooks" : "eBooks")}): {BooksDone} of {BooksTotal} books";

        public string SearchId { get; set; }
        public int AuthorId { get; set; }
        public BookMediaType MediaType { get; set; }

        public override bool UpdateScheduledTask => false;
        public override bool IsLongRunning => true;
    }
}
