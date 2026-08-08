namespace NzbDrone.Core.Parser
{
    public static class SceneChecker
    {
        //This method should prefer false negatives over false positives.
        //It's better not to use a title that might be scene than to use one that isn't scene
        public static bool IsSceneTitle(string title)
        {
            if (!title.Contains("."))
            {
                return false;
            }

            if (title.Contains(" "))
            {
                return false;
            }

            var parsedTitle = Parser.ParseBookTitle(title);

            if (parsedTitle == null ||
                parsedTitle.Quality == null ||
                parsedTitle.Quality.Quality == Qualities.Quality.Unknown ||
                string.IsNullOrWhiteSpace(parsedTitle.AuthorName))
            {
                return false;
            }

            return true;
        }
    }
}
