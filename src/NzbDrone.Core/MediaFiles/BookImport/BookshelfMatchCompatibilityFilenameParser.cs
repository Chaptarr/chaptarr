using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NzbDrone.Common.Extensions;

namespace NzbDrone.Core.MediaFiles.BookImport
{
    internal static class BookshelfMatchCompatibilityFilenameParser
    {
        private static readonly (string Characters, string Separators)[] CharacterSets =
        {
            (@"a-z0-9,\(\)\.&'’\s", @"\s_\-"),
            (@"a-z0-9,\(\)\.&'’_", @"\s\-")
        };

        internal sealed class TrackEvidence
        {
            public string Title { get; init; }
            public string Author { get; init; }
        }

        public static IReadOnlyList<TrackEvidence> Parse(IReadOnlyList<string> paths, bool allowUnstructuredSingleFileTitle)
        {
            if (paths == null || paths.Count == 0 || paths.Any(string.IsNullOrWhiteSpace))
            {
                return Array.Empty<TrackEvidence>();
            }

            var filenames = paths
                .Select(path => Path.GetFileNameWithoutExtension(path)?.RemoveAccent() ?? string.Empty)
                .ToList();

            if (allowUnstructuredSingleFileTitle && filenames.Count == 1)
            {
                var filename = Path.GetFileNameWithoutExtension(paths[0])?.Trim();
                if (!string.IsNullOrWhiteSpace(filename) &&
                    filename.Any(char.IsWhiteSpace) &&
                    filename.IndexOfAny(new[] { '-', '_' }) < 0)
                {
                    return new[] { new TrackEvidence { Title = filename } };
                }
            }

            foreach (var (characters, separators) in CharacterSets)
            {
                foreach (var pattern in BuildPatterns(characters, separators))
                {
                    var matches = filenames.Select(filename => pattern.Match(filename)).ToArray();
                    if (matches.Any(match => !match.Success || !match.Groups[0].Success))
                    {
                        continue;
                    }

                    var groupNames = pattern.GetGroupNames();
                    if (groupNames.Contains("tag") && !AllEqual(matches, "tag"))
                    {
                        continue;
                    }

                    string titleField;
                    string author = null;
                    if (groupNames.Contains("author"))
                    {
                        if (AllEqual(matches, "author"))
                        {
                            author = matches[0].Groups["author"].Value.Trim();
                            titleField = "title";
                        }
                        else if (AllEqual(matches, "title"))
                        {
                            author = matches[0].Groups["title"].Value.Trim();
                            titleField = "author";
                        }
                        else
                        {
                            continue;
                        }
                    }
                    else
                    {
                        titleField = "title";
                    }

                    return matches.Select(match => new TrackEvidence
                    {
                        Title = match.Groups[titleField].Value.Trim(),
                        Author = author
                    }).ToList();
                }
            }

            return Array.Empty<TrackEvidence>();
        }

        private static Regex[] BuildPatterns(string characters, string separators)
        {
            var separator = $@"(?<sep>[{separators}]+)";
            var repeatedSeparator = @"\k<sep>";
            var author = $@"(?<author>[{characters}]+)";
            var track = @"(?<track>\d+)";
            var title = $@"(?<title>[{characters}]+)";
            var tag = $@"(?<tag>[{characters}]+)";

            return new[]
            {
                new Regex($@"^{track}{separator}{author}{repeatedSeparator}{title}{repeatedSeparator}{tag}$", RegexOptions.IgnoreCase),
                new Regex($@"^{track}{separator}{author}{repeatedSeparator}{tag}{repeatedSeparator}{title}$", RegexOptions.IgnoreCase),
                new Regex($@"^{track}{separator}{author}{repeatedSeparator}{title}$", RegexOptions.IgnoreCase),
                new Regex($@"^{author}{separator}{tag}{repeatedSeparator}{track}{repeatedSeparator}{title}$", RegexOptions.IgnoreCase),
                new Regex($@"^{author}{separator}{track}{repeatedSeparator}{title}{repeatedSeparator}{tag}$", RegexOptions.IgnoreCase),
                new Regex($@"^{author}{separator}{track}{repeatedSeparator}{title}$", RegexOptions.IgnoreCase),
                new Regex($@"^{author}{separator}{title}{repeatedSeparator}{tag}$", RegexOptions.IgnoreCase),
                new Regex($@"^{author}{separator}{tag}{repeatedSeparator}{title}$", RegexOptions.IgnoreCase),
                new Regex($@"^{author}{separator}{title}$", RegexOptions.IgnoreCase),
                new Regex($@"^{track}{separator}{title}$", RegexOptions.IgnoreCase),
                new Regex($@"^{track}{separator}{tag}{repeatedSeparator}{title}$", RegexOptions.IgnoreCase),
                new Regex($@"^{track}{separator}{title}{repeatedSeparator}{tag}$", RegexOptions.IgnoreCase),
                new Regex($@"^{title}$", RegexOptions.IgnoreCase)
            };
        }

        private static bool AllEqual(IEnumerable<Match> matches, string groupName)
        {
            return matches.Select(match => match.Groups[groupName].Value).Distinct(StringComparer.Ordinal).Count() == 1;
        }
    }
}
