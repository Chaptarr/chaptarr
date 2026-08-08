using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.Parser
{
    /// <summary>
    /// How strongly a release title asserts a file format. A release title can mention several
    /// formats for very different reasons ("Title, epub, please...thanks - Title.mobi" is a request
    /// post whose payload is a mobi), so the tokens are tiered by the STRUCTURE around them rather
    /// than by reading the surrounding words. Structure is language-neutral; prose is not.
    /// </summary>
    public enum FormatEvidenceTier
    {
        /// <summary>No format token found.</summary>
        None = 0,

        /// <summary>An isolated format word somewhere in the title. Weakest: it may describe a request, not the payload.</summary>
        LooseToken = 1,

        /// <summary>Format tokens inside a delimited group ("[azw3 epub mobi]") or in a run of adjacent tokens. This is a deliberate format list.</summary>
        FormatGroup = 2,

        /// <summary>The filename extension terminating the title's last path segment. Strongest title evidence: it names the actual payload.</summary>
        TerminalExtension = 3
    }

    /// <summary>
    /// Formats detected from the strongest title evidence; a package list may supplement a member extension.
    /// </summary>
    public class TitleFormatEvidence
    {
        public FormatEvidenceTier Tier { get; set; } = FormatEvidenceTier.None;

        public List<Quality> Qualities { get; set; } = new List<Quality>();

        /// <summary>The first quality came from the payload's own filename extension.</summary>
        public bool PrimaryFromExtension { get; set; }

        public bool Any => Tier != FormatEvidenceTier.None && Qualities.Any();
    }
}
