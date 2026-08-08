using System;
using System.Collections.Generic;

namespace NzbDrone.Core.Books.Services
{
    public class MatchingConfiguration
    {
        // Title token matching thresholds (simplified from Database Engineer B's recommendation)
        public double ShortTitleThreshold { get; set; } = 1.0;   // 1-2 words: 100%
        public double MediumTitleThreshold { get; set; } = 0.75; // 3-6 words: 75%
        public double LongTitleThreshold { get; set; } = 0.60;   // 7+ words: 60%

        // Author discovery threshold for FTS5
        public double AuthorSimilarityThreshold { get; set; } = 0.08;

        // Field priorities for author discovery (user can adjust)
        public string[] AuthorFieldPriority { get; set; } =
        {
            "album_artist", "artist", "composer", "albumartist", "author"
        };

        // Calculate required matches based on simplified thresholds
        public int CalculateRequiredMatches(List<string> bookTokens)
        {
            var count = bookTokens.Count;

            var threshold = count switch
            {
                <= 2 => ShortTitleThreshold,
                <= 6 => MediumTitleThreshold,
                _ => LongTitleThreshold
            };

            return (int)Math.Ceiling(count * threshold);
        }
    }
}
