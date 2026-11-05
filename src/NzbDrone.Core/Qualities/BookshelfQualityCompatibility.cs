using System.Collections.Generic;
using System.Linq;

namespace NzbDrone.Core.Qualities
{
    public static class BookshelfQualityCompatibility
    {
        public static List<QualityDefinition> GetDefaultDefinitions()
        {
            return Quality.DefaultQualityDefinitions
                .Select(CloneWithBookshelfOrdering)
                .OrderBy(definition => definition.GroupWeight)
                .ThenBy(definition => definition.Weight)
                .ToList();
        }

        public static int GetWeight(Quality quality)
        {
            return quality.Id switch
            {
                10 => 100, // MP3
                12 => 105, // M4B
                11 => 110, // FLAC
                _ => Quality.DefaultQualityDefinitions.Single(definition => definition.Quality == quality).Weight
            };
        }

        public static int Compare(Quality left, Quality right)
        {
            return GetWeight(left).CompareTo(GetWeight(right));
        }

        private static QualityDefinition CloneWithBookshelfOrdering(QualityDefinition definition)
        {
            var weight = GetWeight(definition.Quality);

            return new QualityDefinition(definition.Quality)
            {
                Id = definition.Id,
                Title = definition.Title,
                GroupName = definition.GroupName,
                GroupWeight = weight,
                Weight = weight,
                MinSize = definition.MinSize,
                MaxSize = definition.MaxSize
            };
        }
    }
}
