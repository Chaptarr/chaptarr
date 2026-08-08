using System.Collections.Generic;
using System.IO;
using System.Linq;
using NzbDrone.Core.Books;

namespace NzbDrone.Core.Parser.Model
{
    public class LocalEdition
    {
        public LocalEdition()
        {
            LocalBooks = new List<LocalBook>();
        }

        public LocalEdition(List<LocalBook> tracks)
        {
            LocalBooks = tracks;
        }

        public List<LocalBook> LocalBooks { get; set; }
        public int TrackCount => LocalBooks.Count;

        public Edition Edition { get; set; }
        public List<LocalBook> ExistingTracks { get; set; }
        public bool NewDownload { get; set; }
        public bool IsImportContext { get; set; } // Added for performance optimization during import

        public void PopulateMatch(bool keepAllEditions)
        {
            if (Edition != null)
            {
                LocalBooks = LocalBooks.Concat(ExistingTracks).DistinctBy(x => x.Path).ToList();

                if (!keepAllEditions)
                {
                    // Manually clone the edition / book to avoid holding references to *every* edition we have
                    // seen during the matching process
                    var edition = new Edition();
                    edition.UseMetadataFrom(Edition);
                    edition.UseDbFieldsFrom(Edition);
                    edition.BookFiles = Edition.BookFiles;

                    // Add null checks to prevent crashes when matching fails
                    if (Edition.Book == null)
                    {
                        // No book matched - skip population
                        return;
                    }

                    var fullBook = Edition.Book;

                    var book = new Book();
                    book.UseMetadataFrom(fullBook);
                    book.UseDbFieldsFrom(fullBook);

                    // Ensure author data exists before accessing
                    if (fullBook.Author != null)
                    {
                        book.Author.UseMetadataFrom(fullBook.Author);
                        book.Author.UseDbFieldsFrom(fullBook.Author);

                        // Author metadata is now integrated into Author - no separate assignment needed
                    }

                    book.BookFiles = fullBook.BookFiles;
                    book.Editions = new List<Edition> { edition };

                    if (fullBook.SeriesLinks != null)
                    {
                        book.SeriesLinks = fullBook.SeriesLinks.Select(l => new SeriesBookLink
                        {
                            Book = book,
                            Series = new Series
                            {
                                Title = l.Series.Value.Title,
                                Description = l.Series.Value.Description,
                                Numbered = l.Series.Value.Numbered,
                                WorkCount = l.Series.Value.WorkCount,
                                PrimaryWorkCount = l.Series.Value.PrimaryWorkCount,
                                // Copy provider IDs for matching
                                GoodreadsSeriesId = l.Series.Value.GoodreadsSeriesId,
                                HardcoverSeriesId = l.Series.Value.HardcoverSeriesId,
                                OpenLibrarySeriesId = l.Series.Value.OpenLibrarySeriesId,
                                AmazonSeriesAsin = l.Series.Value.AmazonSeriesAsin
                            },
                            IsPrimary = l.IsPrimary,
                            Position = l.Position,
                            SeriesPosition = l.SeriesPosition
                        }).ToList();
                    }
                    else
                    {
                        book.SeriesLinks = new List<SeriesBookLink>();
                    }

                    edition.Book = book;

                    Edition = edition;

                    foreach (var localTrack in LocalBooks)
                    {
                        // MULTI-EDITION FIX: Assign tracks to directory-specific editions if available
                        var assignedEdition = edition;
                        if (book.Editions?.Count > 1)
                        {
                            // Look for a directory-specific edition that matches this track's directory
                            var trackDirectory = Path.GetDirectoryName(localTrack.Path);
                            var trackDirHash = trackDirectory?.GetHashCode().ToString();

                            var matchingEdition = book.Editions.FirstOrDefault(e =>
                                e.ForeignEditionId != null && e.ForeignEditionId.Contains($"_dir{trackDirHash}"));

                            if (matchingEdition != null)
                            {
                                assignedEdition = matchingEdition;
                            }
                        }

                        localTrack.Edition = assignedEdition;
                        localTrack.Book = book;
                        localTrack.Author = book.Author;
                        localTrack.PartCount = LocalBooks.Count;
                    }
                }
                else
                {
                    foreach (var localTrack in LocalBooks)
                    {
                        // MULTI-EDITION FIX: Assign tracks to directory-specific editions if available
                        var assignedEdition = Edition;
                        if (Edition.Book?.Editions?.Count > 1)
                        {
                            // Look for a directory-specific edition that matches this track's directory
                            var trackDirectory = Path.GetDirectoryName(localTrack.Path);
                            var trackDirHash = trackDirectory?.GetHashCode().ToString();

                            var matchingEdition = Edition.Book.Editions.FirstOrDefault(e =>
                                e.ForeignEditionId != null && e.ForeignEditionId.Contains($"_dir{trackDirHash}"));

                            if (matchingEdition != null)
                            {
                                assignedEdition = matchingEdition;
                            }
                        }

                        localTrack.Edition = assignedEdition;

                        // Add null checks for book and author
                        if (assignedEdition.Book != null)
                        {
                            localTrack.Book = assignedEdition.Book;

                            if (assignedEdition.Book.Author != null)
                            {
                                localTrack.Author = assignedEdition.Book.Author;
                            }
                        }

                        localTrack.PartCount = LocalBooks.Count;
                    }
                }
            }
        }

        public override string ToString()
        {
            return "[" + string.Join(", ", LocalBooks.Select(x => Path.GetDirectoryName(x.Path)).Distinct()) + "]";
        }
    }
}
