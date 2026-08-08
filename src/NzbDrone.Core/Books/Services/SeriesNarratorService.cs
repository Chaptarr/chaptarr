using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books.Repositories;

namespace NzbDrone.Core.Books.Services
{
    public interface ISeriesNarratorService
    {
        List<string> GetSeriesNarrators(int seriesId);
        List<string> DetectSequentialOverlapNarrators(int seriesId);
        Dictionary<string, int> GetNarratorBookCounts(int seriesId);
        List<SeriesNarratorAnalysis> AnalyzeSeriesNarrators(int seriesId);
        string GetPreferredSeriesNarrator(int seriesId);
        void SetPreferredSeriesNarrator(int seriesId, string narrator);
        List<Book> GetBooksWithoutPreferredNarrator(int seriesId);
        bool ApplySeriesNarratorToBooks(int seriesId, string narrator, bool overrideExisting = false);
    }

    public class SeriesNarratorService : ISeriesNarratorService
    {
        private readonly ISeriesRepository _seriesRepository;
        private readonly IBookRepository _bookRepository;
        private readonly IBookNarratorOptionRepository _narratorOptionRepository;
        private readonly IBookNarratorService _bookNarratorService;
        private readonly Logger _logger;

        public SeriesNarratorService(ISeriesRepository seriesRepository,
                                     IBookRepository bookRepository,
                                     IBookNarratorOptionRepository narratorOptionRepository,
                                     IBookNarratorService bookNarratorService,
                                     Logger logger)
        {
            _seriesRepository = seriesRepository;
            _bookRepository = bookRepository;
            _narratorOptionRepository = narratorOptionRepository;
            _bookNarratorService = bookNarratorService;
            _logger = logger;
        }

        public List<string> GetSeriesNarrators(int seriesId)
        {
            _logger.Debug("Getting all narrators for series ID: {0}", seriesId);

            try
            {
                var series = _seriesRepository.Get(seriesId);
                if (series == null)
                {
                    _logger.Warn("Series not found: {0}", seriesId);
                    return new List<string>();
                }

                // Get all books in the series
                var booksInSeries = _bookRepository.GetBooksBySeries(seriesId);
                var narrators = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var book in booksInSeries)
                {
                    // Add book-level narrator
                    if (book.Narrator.IsNotNullOrWhiteSpace())
                    {
                        narrators.Add(book.Narrator);
                    }

                    // Add preferred narrator options
                    var preferredOptions = _narratorOptionRepository.GetPreferredByBookId(book.Id);
                    foreach (var option in preferredOptions)
                    {
                        if (option.Narrator.IsNotNullOrWhiteSpace())
                        {
                            narrators.Add(option.Narrator);
                        }
                    }
                }

                var result = narrators.ToList();
                _logger.Debug("Found {0} unique narrators for series {1}", result.Count, seriesId);
                return result;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error getting series narrators for series {0}", seriesId);
                return new List<string>();
            }
        }

        public List<string> DetectSequentialOverlapNarrators(int seriesId)
        {
            _logger.Debug("Detecting sequential overlap narrators for series ID: {0}", seriesId);

            try
            {
                var booksInSeries = _bookRepository.GetBooksBySeries(seriesId)
                    .OrderBy(b => b.SeriesLinks?.FirstOrDefault()?.SeriesPosition ?? 0)
                    .ToList();

                if (booksInSeries.Count < 2)
                {
                    _logger.Debug("Series {0} has fewer than 2 books, no overlap detection needed", seriesId);
                    return new List<string>();
                }

                var narratorContinuity = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

                // Track narrator positions across the series
                for (var i = 0; i < booksInSeries.Count; i++)
                {
                    var book = booksInSeries[i];
                    var position = i + 1; // 1-based position

                    var bookNarrators = new List<string>();

                    // Get narrator from book
                    if (book.Narrator.IsNotNullOrWhiteSpace())
                    {
                        bookNarrators.Add(book.Narrator);
                    }

                    // Get preferred narrators
                    var preferredOptions = _narratorOptionRepository.GetPreferredByBookId(book.Id);
                    bookNarrators.AddRange(preferredOptions.Select(o => o.Narrator).Where(n => n.IsNotNullOrWhiteSpace()));

                    // Remove duplicates (case-insensitive)
                    bookNarrators = bookNarrators
                        .GroupBy(n => n.ToLowerInvariant())
                        .Select(g => g.First())
                        .ToList();

                    foreach (var narrator in bookNarrators)
                    {
                        if (!narratorContinuity.ContainsKey(narrator))
                        {
                            narratorContinuity[narrator] = new List<int>();
                        }

                        narratorContinuity[narrator].Add(position);
                    }
                }

                // Analyze continuity patterns
                var overlappingNarrators = new List<string>();

                foreach (var kvp in narratorContinuity)
                {
                    var narrator = kvp.Key;
                    var positions = kvp.Value.OrderBy(p => p).ToList();

                    // Check for sequential overlap (narrator appears in 3+ consecutive books)
                    if (HasSequentialOverlap(positions, 3))
                    {
                        overlappingNarrators.Add(narrator);
                        _logger.Debug("Narrator '{0}' has sequential overlap in series {1} at positions: {2}", narrator, seriesId, string.Join(",", positions));
                    }
                }

                _logger.Debug("Found {0} narrators with sequential overlap for series {1}", overlappingNarrators.Count, seriesId);
                return overlappingNarrators;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error detecting sequential overlap narrators for series {0}", seriesId);
                return new List<string>();
            }
        }

        private bool HasSequentialOverlap(List<int> positions, int minConsecutive)
        {
            if (positions.Count < minConsecutive)
            {
                return false;
            }

            var consecutiveCount = 1;
            for (var i = 1; i < positions.Count; i++)
            {
                if (positions[i] == positions[i - 1] + 1)
                {
                    consecutiveCount++;
                    if (consecutiveCount >= minConsecutive)
                    {
                        return true;
                    }
                }
                else
                {
                    consecutiveCount = 1;
                }
            }

            return false;
        }

        public Dictionary<string, int> GetNarratorBookCounts(int seriesId)
        {
            _logger.Debug("Getting narrator book counts for series ID: {0}", seriesId);

            var narratorCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var booksInSeries = _bookRepository.GetBooksBySeries(seriesId);

            foreach (var book in booksInSeries)
            {
                var bookNarrators = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // Add book narrator
                if (book.Narrator.IsNotNullOrWhiteSpace())
                {
                    bookNarrators.Add(book.Narrator);
                }

                // Add preferred narrators
                var preferredOptions = _narratorOptionRepository.GetPreferredByBookId(book.Id);
                foreach (var option in preferredOptions)
                {
                    if (option.Narrator.IsNotNullOrWhiteSpace())
                    {
                        bookNarrators.Add(option.Narrator);
                    }
                }

                // Count each unique narrator once per book
                foreach (var narrator in bookNarrators)
                {
                    narratorCounts[narrator] = narratorCounts.GetValueOrDefault(narrator, 0) + 1;
                }
            }

            return narratorCounts;
        }

        public List<SeriesNarratorAnalysis> AnalyzeSeriesNarrators(int seriesId)
        {
            _logger.Debug("Analyzing series narrators for series ID: {0}", seriesId);

            var narratorCounts = GetNarratorBookCounts(seriesId);
            var sequentialNarrators = DetectSequentialOverlapNarrators(seriesId);
            var totalBooks = _bookRepository.GetBooksBySeries(seriesId).Count;

            var analysis = narratorCounts.Select(kvp => new SeriesNarratorAnalysis
            {
                Narrator = kvp.Key,
                BookCount = kvp.Value,
                Percentage = totalBooks > 0 ? (kvp.Value * 100.0) / totalBooks : 0,
                HasSequentialOverlap = sequentialNarrators.Contains(kvp.Key, StringComparer.OrdinalIgnoreCase),
                IsMainNarrator = totalBooks > 0 && (kvp.Value * 100.0) / totalBooks >= 70, // 70%+ coverage
                IsSporadicNarrator = totalBooks > 0 && (kvp.Value * 100.0) / totalBooks < 30 // <30% coverage
            })
            .OrderByDescending(a => a.BookCount)
            .ThenBy(a => a.Narrator)
            .ToList();

            _logger.Debug("Analyzed {0} narrators for series {1}", analysis.Count, seriesId);
            return analysis;
        }

        public string GetPreferredSeriesNarrator(int seriesId)
        {
            var series = _seriesRepository.Get(seriesId);
            return series?.Narrator;
        }

        public void SetPreferredSeriesNarrator(int seriesId, string narrator)
        {
            _logger.Debug("Setting preferred series narrator for series {0}: {1}", seriesId, narrator);

            var series = _seriesRepository.Get(seriesId);
            if (series != null)
            {
                series.Narrator = narrator;
                _seriesRepository.Update(series);
                _logger.Debug("Updated series {0} with preferred narrator: {1}", seriesId, narrator);
            }
        }

        public List<Book> GetBooksWithoutPreferredNarrator(int seriesId)
        {
            var booksInSeries = _bookRepository.GetBooksBySeries(seriesId);
            var booksWithoutNarrator = new List<Book>();

            foreach (var book in booksInSeries)
            {
                var hasNarrator = book.Narrator.IsNotNullOrWhiteSpace();
                var hasPreferredOption = _narratorOptionRepository.GetPreferredByBookId(book.Id).Any();

                if (!hasNarrator && !hasPreferredOption)
                {
                    booksWithoutNarrator.Add(book);
                }
            }

            return booksWithoutNarrator;
        }

        public bool ApplySeriesNarratorToBooks(int seriesId, string narrator, bool overrideExisting = false)
        {
            _logger.Debug("Applying series narrator '{0}' to books in series {1} (override: {2})", narrator, seriesId, overrideExisting);

            try
            {
                var booksInSeries = _bookRepository.GetBooksBySeries(seriesId);
                var updatedCount = 0;

                foreach (var book in booksInSeries)
                {
                    var shouldUpdate = overrideExisting || book.Narrator.IsNullOrWhiteSpace();

                    if (shouldUpdate)
                    {
                        _bookNarratorService.SetPreferredNarrator(book.Id, narrator);
                        updatedCount++;
                    }
                }

                _logger.Info("Applied series narrator '{0}' to {1} books in series {2}", narrator, updatedCount, seriesId);
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error applying series narrator to books for series {0}", seriesId);
                return false;
            }
        }
    }

    public class SeriesNarratorAnalysis
    {
        public string Narrator { get; set; }
        public int BookCount { get; set; }
        public double Percentage { get; set; }
        public bool HasSequentialOverlap { get; set; }
        public bool IsMainNarrator { get; set; }
        public bool IsSporadicNarrator { get; set; }
    }
}
