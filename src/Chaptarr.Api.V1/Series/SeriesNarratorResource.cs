using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Books;

namespace Chaptarr.Api.V1.Series
{
    public class SeriesNarratorAnalysisResource
    {
        public int SeriesId { get; set; }
        public string SeriesTitle { get; set; }
        public string PreferredNarrator { get; set; }
        public List<NarratorAnalysisResource> NarratorAnalysis { get; set; } = new List<NarratorAnalysisResource>();
        public List<string> SequentialOverlapNarrators { get; set; } = new List<string>();
        public int BooksWithoutNarratorCount { get; set; }
        public int TotalBooksInSeries { get; set; }
    }

    public class NarratorAnalysisResource
    {
        public string Narrator { get; set; }
        public int BookCount { get; set; }
        public double Percentage { get; set; }
        public bool HasSequentialOverlap { get; set; }
        public bool IsMainNarrator { get; set; }
        public bool IsSporadicNarrator { get; set; }
    }

    public class SeriesNarratorSearchResource
    {
        public int SeriesId { get; set; }
        public string SeriesTitle { get; set; }
        public List<string> FoundNarrators { get; set; } = new List<string>();
        public string RecommendedNarrator { get; set; }
        public int SearchDurationMs { get; set; }
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
    }

    public class SeriesNarratorPreferenceRequest
    {
        public string Narrator { get; set; }
        public bool ApplyToAllBooks { get; set; } = false;
        public bool OverrideExisting { get; set; } = false;
    }


    public class SeriesNarratorPreferenceResponseResource
    {
        public string Message { get; set; }
        public bool AppliedToBooks { get; set; }
    }

    public class SeriesNarratorInheritanceResource
    {
        public int SeriesId { get; set; }
        public string SeriesTitle { get; set; }
        public string PreferredSeriesNarrator { get; set; }
        public List<BookNarratorInheritanceInfo> BooksWithoutNarrator { get; set; } = new List<BookNarratorInheritanceInfo>();
        public bool CanApplyInheritance { get; set; }
    }

    public class BookNarratorInheritanceInfo
    {
        public int BookId { get; set; }
        public string BookTitle { get; set; }
        public string CurrentNarrator { get; set; }
        public bool CanInheritFromSeries { get; set; }
    }
    public class SeriesNarratorDiscoveryResource
    {
        public int SeriesId { get; set; }
        public List<NarratorDiscoveryInfoResource> CompleteNarrators { get; set; } = new List<NarratorDiscoveryInfoResource>();
        public List<NarratorDiscoveryInfoResource> PartialNarrators { get; set; } = new List<NarratorDiscoveryInfoResource>();
        public List<SeriesResource> ExistingVariants { get; set; } = new List<SeriesResource>();
        public NarratorDiscoveryInfoResource RecommendedNarrator { get; set; }
    }

    public class NarratorDiscoveryInfoResource
    {
        public string NarratorName { get; set; }
        public int BookCount { get; set; }
        public int TotalBooksInSeries { get; set; }
        public double AverageRating { get; set; }
        public bool HasCompleteSet { get; set; }
        public List<string> BookTitles { get; set; } = new List<string>();
        public bool HasExistingVariant { get; set; }
    }

    public static class SeriesNarratorDiscoveryResourceMapper
    {
        public static SeriesNarratorDiscoveryResource ToResource(this SeriesNarratorDiscoveryResult result)
        {
            if (result == null)
            {
                return null;
            }

            return new SeriesNarratorDiscoveryResource
            {
                SeriesId = result.SeriesId,
                CompleteNarrators = result.CompleteNarrators.Select(ToResource).ToList(),
                PartialNarrators = result.PartialNarrators.Select(ToResource).ToList(),
                ExistingVariants = result.ExistingVariants.ToResource(),
                RecommendedNarrator = result.RecommendedNarrator.ToResource()
            };
        }

        private static NarratorDiscoveryInfoResource ToResource(this NarratorInfo result)
        {
            if (result == null)
            {
                return null;
            }

            return new NarratorDiscoveryInfoResource
            {
                NarratorName = result.NarratorName,
                BookCount = result.BookCount,
                TotalBooksInSeries = result.TotalBooksInSeries,
                AverageRating = result.AverageRating,
                HasCompleteSet = result.HasCompleteSet,
                BookTitles = result.BookTitles?.ToList() ?? new List<string>(),
                HasExistingVariant = result.HasExistingVariant
            };
        }
    }

}
