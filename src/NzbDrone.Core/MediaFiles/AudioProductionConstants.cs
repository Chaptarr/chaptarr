namespace NzbDrone.Core.MediaFiles
{
    public static class AudioProductionConstants
    {
        public const string DetectedDramatizedFullCastType = "Dramatized / Full-Cast";

        public static readonly string[] GraphicAudioIndicators =
        {
            "graphicaudio",
            "graphic audio",
            "movie in your mind",
            "a movie in your mind",
            "fullcast",
            "full cast",
            "full cast audio",
            "full cast production",
            "dramatized",
            "dramatised",
            "dramatized adaptation",
            "dramatised adaptation",
            "radio dramatization",
            "radio dramatisation",
            "radio drama",
            "multicast",
            "multi cast",
            "multi-cast",
            "audio drama",
            "audio theatre",
            "audio theater",
            "cast performance",
            "ensemble cast",
            "voice cast",
            "immersive audio experience",
            "sound effects and music"
        };

        public static readonly string[] AudiobookIndicators =
        {
            "audible",
            "audio",
            "audiobook",
            "audio book",
            "unabridged",
            "abridged"
        };
    }
}
