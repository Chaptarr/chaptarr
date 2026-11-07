using NzbDrone.Core.Configuration;

namespace NzbDrone.Core.Datastore
{
    internal static class BookshelfMatchSettingMigration
    {
        public static bool TryMapMinimumMatchPercent(int percent, out BookMatchingStrictness strictness)
        {
            if (percent < 50 || percent > 99)
            {
                strictness = default;
                return false;
            }

            strictness = percent switch
            {
                <= 66 => BookMatchingStrictness.Aggressive,
                <= 82 => BookMatchingStrictness.Balanced,
                _ => BookMatchingStrictness.Strict
            };

            return true;
        }
    }
}
