using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Chaptarr.Api.V1.Books;
using NzbDrone.Core.Books;

namespace Chaptarr.Api.V1.Series
{
    public class SeriesNarratorMatrixResource
    {
        public int SeriesId { get; set; }
        public string SeriesTitle { get; set; }
        public int TotalBooks { get; set; }
        public List<NarratorCoverageResource> Narrators { get; set; } = new List<NarratorCoverageResource>();
        public List<MatrixBookResource> Books { get; set; } = new List<MatrixBookResource>();
    }

    public class NarratorCoverageResource
    {
        public string Name { get; set; }
        public string Production { get; set; } = "unknown";
        public List<int> CoveredBookIds { get; set; } = new List<int>();
        public List<int> MissingBookIds { get; set; } = new List<int>();
        public List<int> OwnedBookIds { get; set; } = new List<int>();
    }

    public class MatrixBookResource
    {
        public int BookId { get; set; }
        public string Title { get; set; }
        public int? Position { get; set; }
        public List<MatrixNarratorResource> Narrators { get; set; } = new List<MatrixNarratorResource>();
    }

    public class MatrixNarratorResource
    {
        public string Name { get; set; }
        public int EditionId { get; set; }
        public bool Owned { get; set; }
    }

    public static class SeriesNarratorMatrixBuilder
    {
        public sealed record Volume(int BookId, string Title, int Position, IReadOnlyList<Edition> Editions);

        public static string Normalize(string value)
        {
            var builder = new StringBuilder();
            foreach (var c in (value ?? string.Empty).Normalize(NormalizationForm.FormD))
            {
                if (char.IsLetterOrDigit(c))
                {
                    builder.Append(char.ToLowerInvariant(c));
                }
            }

            return builder.ToString();
        }

        /// <summary>
        /// Builds the per-narrator coverage of a series. A volume counts as covered by a narrator
        /// when any known audiobook edition of it lists that narrator; "owned" additionally needs a
        /// file for that edition.
        /// </summary>
        public static SeriesNarratorMatrixResource Build(int seriesId, string seriesTitle, IReadOnlyList<Volume> volumes)
        {
            var result = new SeriesNarratorMatrixResource
            {
                SeriesId = seriesId,
                SeriesTitle = seriesTitle,
                TotalBooks = volumes.Count
            };

            var byNarrator = new Dictionary<string, NarratorCoverageResource>(StringComparer.OrdinalIgnoreCase);
            foreach (var volume in volumes)
            {
                var row = new MatrixBookResource { BookId = volume.BookId, Title = volume.Title, Position = volume.Position };
                foreach (var edition in volume.Editions)
                {
                    var narrators = EditionCatalogMapper.NarratorsOf(edition);
                    var owned = edition.BookFiles?.Any() == true;
                    var production = EditionCatalogMapper.ProductionOf(edition, narrators);
                    foreach (var narrator in narrators)
                    {
                        if (!byNarrator.TryGetValue(narrator, out var coverage))
                        {
                            coverage = new NarratorCoverageResource { Name = narrator, Production = production };
                            byNarrator[narrator] = coverage;
                        }

                        // A full-cast production anywhere marks the narrator's group as full cast.
                        if (production == "full-cast")
                        {
                            coverage.Production = "full-cast";
                        }
                        else if (coverage.Production == "unknown")
                        {
                            coverage.Production = production;
                        }

                        if (!coverage.CoveredBookIds.Contains(volume.BookId))
                        {
                            coverage.CoveredBookIds.Add(volume.BookId);
                        }

                        if (owned && !coverage.OwnedBookIds.Contains(volume.BookId))
                        {
                            coverage.OwnedBookIds.Add(volume.BookId);
                        }

                        if (!row.Narrators.Any(n => string.Equals(n.Name, narrator, StringComparison.OrdinalIgnoreCase) && n.EditionId == edition.Id))
                        {
                            row.Narrators.Add(new MatrixNarratorResource { Name = narrator, EditionId = edition.Id, Owned = owned });
                        }
                    }
                }

                result.Books.Add(row);
            }

            foreach (var coverage in byNarrator.Values)
            {
                coverage.MissingBookIds = volumes.Select(v => v.BookId).Where(id => !coverage.CoveredBookIds.Contains(id)).ToList();
            }

            result.Narrators = byNarrator.Values
                .OrderByDescending(n => n.CoveredBookIds.Count)
                .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return result;
        }
    }
}
