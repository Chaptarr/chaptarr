using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace NzbDrone.Core.Organizer
{
    public static class SmartNarratorToken
    {
        public const string DefaultOrder = "AudioProductionType,FullCast,Narrators";

        private static readonly IReadOnlyDictionary<string, string> Choices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "AudioProductionType", "{AudioProductionType}" },
            { "GraphicAudio", "{GraphicAudio}" },
            { "FullCast", "{FullCast}" },
            { "Narrator", "{Narrator}" },
            { "Narrators", "{Narrators}" },
            { "FirstNarrator", "{First Narrator}" },
            { "NarratorCleanName", "{Narrator CleanName}" },
            { "NarratorFirst", "{Narrator First}" },
            { "NarratorLast", "{Narrator Last}" },
            { "NarratorInitials", "{Narrator Initials}" }
        };

        public static readonly Regex Pattern = new Regex(@"\{(?<prefix>[- ._,\[(]*)(?<token>Smart(?<separator>[- ._]*)Narrator)(?::(?<customFormat>[^{}]*?))?(?<suffix>[- ._)\]]*)\}", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static string[] ParseOrder(string order)
        {
            var choices = (order ?? DefaultOrder).Split(',').Select(choice => choice.Trim()).ToArray();
            if (choices.Any(choice => !Choices.ContainsKey(choice)) || choices.Distinct(StringComparer.OrdinalIgnoreCase).Count() != choices.Length)
            {
                throw new NamingFormatException("Smart Narrator requires an ordered list of distinct narrator or production tokens.");
            }

            return choices;
        }

        public static void Validate(string pattern)
        {
            foreach (Match match in Pattern.Matches(pattern ?? string.Empty))
            {
                ParseOrder(match.Groups["customFormat"].Success ? match.Groups["customFormat"].Value : null);
            }
        }

        internal static string Resolve(string order, Dictionary<string, Func<TokenMatch, string>> handlers)
        {
            foreach (var choice in ParseOrder(order))
            {
                var value = handlers[Choices[choice]](new TokenMatch()).Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return string.Empty;
        }
    }
}
