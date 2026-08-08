using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NLog;

namespace NzbDrone.Core.Books.Services
{
    public static class MatchThresholds
    {
        public const double AUTO_IMPORT = 7.0;      // High confidence - auto import
        public const double MANUAL_REVIEW = 5.0;    // Medium confidence - show for review
        public const double REJECTION = 5.0;        // Below this = no match
    }

    public static class MatchScoreValues
    {
        public const double ISBN_ASIN_MATCH = 10.0;
        public const double EXACT_MATCH = 8.0;
        public const double SERIES_MATCH = 7.0;  // Only if book confirmed in series
        public const double NARRATOR_MATCH = 6.0;
        public const double PARTIAL_MATCH = 5.0;
        public const double AUTHOR_ONLY = 2.0;
    }

    public static class FieldWeights
    {
        public const double PRIMARY = 1.0;     // Artist, AlbumArtist, Author
        public const double SECONDARY = 0.7;   // Album
        public const double TERTIARY = 0.6;    // Composer (often narrator)
        public const double LOWER = 0.4;       // Title (often has chapter noise)
        public const double MULTI_FIELD_BOOST = 0.2;  // When multiple fields agree
    }

    public class MatchScore
    {
        public double Total { get; set; }
        public List<MatchComponent> Components { get; set; } = new List<MatchComponent>();
        public double TrigramSimilarity { get; set; }
        public string UniqueOverlap { get; set; }
        public MatchConfidence Confidence => GetConfidence();

        private MatchConfidence GetConfidence()
        {
            if (Total >= MatchThresholds.AUTO_IMPORT)
                return MatchConfidence.High;
            if (Total >= MatchThresholds.MANUAL_REVIEW)
                return MatchConfidence.Medium;
            return MatchConfidence.Low;
        }

        public void AddComponent(string field, double score, string reason = null)
        {
            Components.Add(new MatchComponent
            {
                Field = field,
                Score = score,
                Reason = reason
            });
            var oldTotal = Total;
            Total = Components.Sum(c => c.Score);

            // Log for debugging
            NLog.LogManager.GetCurrentClassLogger().Trace(
                "[MATCH-SCORE] Added component: Field='{0}', Score={1:F2}, Reason='{2}' (Total: {3:F2} -> {4:F2})",
                field, score, reason ?? "<none>", oldTotal, Total);
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Total Score: {Total:F1} ({Confidence})");
            foreach (var component in Components.OrderByDescending(c => c.Score))
            {
                sb.AppendLine($"  {component.Field}: {component.Score:F1} {component.Reason}");
            }
            if (TrigramSimilarity > 0)
            {
                sb.AppendLine($"  Trigram Similarity: {TrigramSimilarity:F2}");
            }
            if (!string.IsNullOrEmpty(UniqueOverlap))
            {
                sb.AppendLine($"  Unique Overlap: \"{UniqueOverlap}\"");
            }
            return sb.ToString();
        }
    }

    public class MatchComponent
    {
        public string Field { get; set; }
        public double Score { get; set; }
        public string Reason { get; set; }
    }

    public enum MatchConfidence
    {
        Low,     // < 5.0 - Reject
        Medium,  // 5.0-7.0 - Manual review
        High     // >= 7.0 - Auto import
    }

    public class MatchLogger
    {
        private readonly Logger _logger;

        public MatchLogger(Logger logger)
        {
            _logger = logger;
        }

        public void LogMatch(Book book, Dictionary<string, string> tags, MatchScore score)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[MATCH] \"{tags.GetValueOrDefault("album", "Unknown")}\" matched to \"{book.Title}\"");
            sb.AppendLine($"        Book ID: {book.Id}");
            sb.AppendLine($"        Score: {score.Total:F1} (Confidence: {score.Confidence})");
            sb.AppendLine($"        Components ({score.Components.Count}):");

            foreach (var component in score.Components.OrderByDescending(c => c.Score))
            {
                sb.AppendLine($"          {component.Field}: {component.Score:F1} {(component.Reason != null ? "(" + component.Reason + ")" : "")}");
            }

            if (score.TrigramSimilarity > 0)
            {
                sb.AppendLine($"        Trigram similarity: {score.TrigramSimilarity:F2}");
            }

            if (!string.IsNullOrEmpty(score.UniqueOverlap))
            {
                sb.AppendLine($"        Unique overlap: \"{score.UniqueOverlap}\"");
            }

            sb.AppendLine($"        Tags used:");
            foreach (var tag in tags.Where(t => !string.IsNullOrWhiteSpace(t.Value)).OrderBy(t => t.Key))
            {
                sb.AppendLine($"          {tag.Key}: \"{tag.Value.Substring(0, Math.Min(tag.Value.Length, 100))}\"");
            }

            _logger.Debug(sb.ToString());

            // Also log a debug version with all details
            _logger.Debug("[MATCH-DETAIL] Full match data: Book='{0}' (ID:{1}), Score={2:F2}, Components={3}, Trigram={4:F4}",
                book.Title, book.Id, score.Total, score.Components.Count, score.TrigramSimilarity);
        }

        public void LogNoMatch(Dictionary<string, string> tags, string reason)
        {
            _logger.Debug($"[NO MATCH] \"{tags.GetValueOrDefault("album", "Unknown")}\" - {reason}");

            // Log debug details
            _logger.Debug("[NO MATCH-DETAIL] Reason: {0}", reason);
            _logger.Debug("[NO MATCH-DETAIL] Tags attempted:");
            foreach (var tag in tags.Where(t => !string.IsNullOrWhiteSpace(t.Value)))
            {
                _logger.Debug("[NO MATCH-DETAIL]   {0}: '{1}'", tag.Key, tag.Value);
            }
        }

        public void LogMultipleMatches(Dictionary<string, string> tags, List<(Book book, MatchScore score)> matches)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[MULTIPLE MATCHES] \"{tags.GetValueOrDefault("album", "Unknown")}\" matched {matches.Count} books:");

            var topMatches = matches.OrderByDescending(m => m.score.Total).Take(5).ToList();
            for (int i = 0; i < topMatches.Count; i++)
            {
                var (book, score) = topMatches[i];
                sb.AppendLine($"  {i + 1}. \"{book.Title}\" (ID: {book.Id}, Score: {score.Total:F1}, Confidence: {score.Confidence})");

                // Show score breakdown for top 3
                if (i < 3 && score.Components.Any())
                {
                    var topComponents = score.Components.OrderByDescending(c => c.Score).Take(3);
                    foreach (var comp in topComponents)
                    {
                        sb.AppendLine($"     - {comp.Field}: {comp.Score:F1}");
                    }
                }
            }

            _logger.Debug(sb.ToString());

            // Debug logging with full details
            _logger.Debug("[MULTIPLE-DETAIL] Full list of {0} matches:", matches.Count);
            foreach (var (book, score) in matches.OrderByDescending(m => m.score.Total))
            {
                _logger.Debug("[MULTIPLE-DETAIL]   Book: '{0}' (ID:{1}), Score: {2:F2}, Components: {3}",
                    book.Title, book.Id, score.Total, string.Join(", ",
                    score.Components.Select(c => $"{c.Field}:{c.Score:F1}")));
            }
        }
    }
}
