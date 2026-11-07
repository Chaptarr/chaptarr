using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Chaptarr.Http.Middleware;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaCover;

namespace Chaptarr.Api.V1.Books
{
    /// <summary>
    /// One edition in terms a person chooses between. Details the catalog does not
    /// provide are reported as unknown rather than guessed.
    /// </summary>
    public class EditionCatalogResource
    {
        public int EditionId { get; set; }
        public string ForeignEditionId { get; set; }
        public string Title { get; set; }
        public string Language { get; set; }
        public string Format { get; set; }
        public bool IsEbook { get; set; }
        public List<string> Narrators { get; set; } = new List<string>();

        /// <summary>solo, full-cast, or unknown.</summary>
        public string Production { get; set; } = "unknown";

        /// <summary>abridged, unabridged, or unknown.</summary>
        public string Abridged { get; set; } = "unknown";

        public int? DurationSeconds { get; set; }
        public string Publisher { get; set; }
        public DateTime? ReleaseDate { get; set; }
        public string ImageUrl { get; set; }
        public bool Monitored { get; set; }
        public bool Owned { get; set; }
    }

    public static class EditionCatalogMapper
    {
        private static readonly Regex Unabridged = new Regex(@"\bunabridged\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Abridged = new Regex(@"\babridged\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex FullCast = new Regex(@"\b(full[- ]?cast|dramati[sz]ed|graphic ?audio|multi[- ]?cast)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static List<string> NarratorsOf(Edition edition)
        {
            var names = edition.NarratorNames?.Where(name => !string.IsNullOrWhiteSpace(name)).ToArray();
            if (names == null || names.Length == 0)
            {
                names = new[] { edition.Narrator };
            }

            return names
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .SelectMany(name => name.Split(new[] { ',', ';', '&' }, StringSplitOptions.RemoveEmptyEntries))
                .Select(name => name.Trim())
                .Where(name => name.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static string ProductionOf(Edition edition, IReadOnlyCollection<string> narrators)
        {
            var hints = string.Join(" ", new[]
            {
                edition.Title,
                edition.Subtitle,
                edition.EditionInfo,
                edition.Disambiguation,
                edition.AudioProductionType
            });

            if (edition.IsGraphicAudio || FullCast.IsMatch(hints))
            {
                return "full-cast";
            }

            return narrators.Count == 1 ? "solo" : "unknown";
        }

        public static string AbridgementOf(Edition edition)
        {
            var hints = string.Join(" ", new[]
            {
                edition.Title,
                edition.Subtitle,
                edition.EditionInfo,
                edition.Disambiguation
            });

            if (Unabridged.IsMatch(hints))
            {
                return "unabridged";
            }

            return Abridged.IsMatch(hints) ? "abridged" : "unknown";
        }

        public static EditionCatalogResource ToCatalogResource(this Edition edition)
        {
            return edition.ToCatalogResource(edition.BookFiles?.Any() == true, null);
        }

        public static EditionCatalogResource ToCatalogResource(
            this Edition edition,
            bool owned,
            ReadarrFacadeContext facadeContext)
        {
            var narrators = NarratorsOf(edition);
            var cover = edition.Images?.FirstOrDefault(image => image.CoverType == MediaCoverTypes.Cover);

            return new EditionCatalogResource
            {
                EditionId = edition.Id,
                ForeignEditionId = ReadarrFacadeEditionIdentity.BuildForeignEditionId(edition, facadeContext),
                Title = edition.Title,
                Language = edition.Language,
                Format = edition.EditionFormat ?? edition.Format,
                IsEbook = edition.IsEbook,
                Narrators = narrators,
                Production = edition.IsEbook ? "unknown" : ProductionOf(edition, narrators),
                Abridged = edition.IsEbook ? "unknown" : AbridgementOf(edition),
                DurationSeconds = edition.DurationSeconds,
                Publisher = edition.Publisher,
                ReleaseDate = edition.ReleaseDate,
                ImageUrl = facadeContext?.IsBookshelfCompatibility == true ? cover?.RemoteUrl ?? cover?.Url : cover?.Url,
                Monitored = edition.Monitored,
                Owned = owned
            };
        }
    }
}
