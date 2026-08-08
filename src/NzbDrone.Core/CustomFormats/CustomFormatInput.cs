using System.Collections.Generic;
using NzbDrone.Core.Books;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.CustomFormats
{
    public class CustomFormatInput
    {
        public ParsedBookInfo BookInfo { get; set; }
        public Author Author { get; set; }
        public long Size { get; set; }
        public IndexerFlags IndexerFlags { get; set; }
        public string Filename { get; set; }
        public BookMediaType? MediaType { get; set; }
        public bool IsGraphicAudio { get; set; }
        public string AudioProductionType { get; set; }
        public string Narrator { get; set; }
        public List<string> AudioProductionFields { get; set; } = new List<string>();
        public List<string> PreferredNarratorNames { get; set; } = new List<string>();
        public bool PreferredNarratorHasUnresolvedNames { get; set; }

        // public CustomFormatInput(ParsedEpisodeInfo episodeInfo, Series series)
        // {
        //     EpisodeInfo = episodeInfo;
        //     Series = series;
        // }
        //
        // public CustomFormatInput(ParsedEpisodeInfo episodeInfo, Series series, long size, List<Language> languages)
        // {
        //     EpisodeInfo = episodeInfo;
        //     Series = series;
        //     Size = size;
        //     Languages = languages;
        // }
        //
        // public CustomFormatInput(ParsedEpisodeInfo episodeInfo, Series series, long size, List<Language> languages, string filename)
        // {
        //     EpisodeInfo = episodeInfo;
        //     Series = series;
        //     Size = size;
        //     Languages = languages;
        //     Filename = filename;
        // }
    }
}
