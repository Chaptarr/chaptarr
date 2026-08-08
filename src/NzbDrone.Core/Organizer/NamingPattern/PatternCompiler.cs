using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace NzbDrone.Core.Organizer.NamingPattern
{
    public interface IPatternCompiler
    {
        string Compile(PatternAst ast);
        PatternAst Decompile(string pattern);
    }

    public class PatternCompiler : IPatternCompiler
    {
        private readonly Dictionary<string, string> _tokenKeyToPattern = new Dictionary<string, string>
        {
            // Author tokens
            { "AuthorName", "{Author Name}" },
            { "AuthorNameFirstLast", "{Author NameFirstLast}" },
            { "AuthorNameThe", "{Author NameThe}" },
            { "AuthorNameFirstCharacter", "{Author NameFirstCharacter}" },
            { "AuthorCleanName", "{Author CleanName}" },
            { "AuthorSortName", "{Author SortName}" },
            { "AuthorDisambiguation", "{Author Disambiguation}" },

            // Book tokens
            { "BookTitle", "{Book Title}" },
            { "BookTitleThe", "{Book TitleThe}" },
            { "BookCleanTitle", "{Book CleanTitle}" },
            { "BookTitleNoSub", "{Book TitleNoSub}" },
            { "BookTitleTheNoSub", "{Book TitleTheNoSub}" },
            { "BookCleanTitleNoSub", "{Book CleanTitleNoSub}" },
            { "BookSubtitle", "{Book Subtitle}" },
            { "BookSubtitleThe", "{Book SubtitleThe}" },
            { "BookCleanSubtitle", "{Book CleanSubtitle}" },
            { "BookDisambiguation", "{Book Disambiguation}" },
            { "BookSeries", "{Book Series}" },
            { "BookSeriesPosition", "{Book SeriesPosition}" },
            { "BookSeriesTitle", "{Book SeriesTitle}" },

            // Part tokens
            { "PartNumber", "{PartNumber}" },
            { "PartCount", "{PartCount}" },

            // Quality tokens
            { "QualityFull", "{Quality Full}" },
            { "QualityTitle", "{Quality Title}" },
            { "QualityProper", "{Quality Proper}" },

            // Media Info tokens
            { "MediaInfoAudioCodec", "{MediaInfo AudioCodec}" },
            { "MediaInfoAudioChannels", "{MediaInfo AudioChannels}" },
            { "MediaInfoAudioBitRate", "{MediaInfo AudioBitRate}" },
            { "MediaInfoAudioBitsPerSample", "{MediaInfo AudioBitsPerSample}" },
            { "MediaInfoAudioSampleRate", "{MediaInfo AudioSampleRate}" },

            // Date tokens
            { "ReleaseYear", "{Release Year}" },
            { "ReleaseYearFirst", "{Release YearFirst}" },
            { "EditionYear", "{Edition Year}" },

            // GraphicAudio tokens
            { "GraphicAudio", "{GraphicAudio}" },
            { "AudioProductionType", "{AudioProductionType}" },

            // Other tokens
            { "ReleaseGroup", "{Release Group}" },
            { "CustomFormats", "{Custom Formats}" },
            { "OriginalTitle", "{Original Title}" },
            { "OriginalFilename", "{Original Filename}" }
            ,
            // Narrator tokens (audiobook organization)
            { "NarratorName", "{Narrator}" },
            { "NarratorNameMultiple", "{Narrator}" },
            { "NarratorCleanName", "{Narrator CleanName}" },
            { "NarratorFirst", "{Narrator First}" },
            { "NarratorLast", "{Narrator Last}" },
            { "NarratorInitials", "{Narrator Initials}" },
            { "Narrators", "{Narrators}" }
        };

        private readonly Dictionary<string, string> _patternToTokenKey;

        public PatternCompiler()
        {
            _patternToTokenKey = new Dictionary<string, string>();

            foreach (var kvp in _tokenKeyToPattern)
            {
                // Some tokens intentionally share the same rendered pattern (ex: "{Narrator}").
                // For decompile purposes, prefer the first registered token key and ignore duplicates.
                _patternToTokenKey.TryAdd(kvp.Value, kvp.Key);
            }
        }

        public string Compile(PatternAst ast)
        {
            var result = new StringBuilder();
            
            foreach (var rootId in ast.RootIds)
            {
                if (ast.NodesById.TryGetValue(rootId, out var node))
                {
                    result.Append(CompileNode(node, ast));
                }
            }

            return result.ToString();
        }

        private string CompileNode(PatternNode node, PatternAst ast)
        {
            return node switch
            {
                TokenNode token => CompileToken(token),
                SeparatorNode separator => separator.Value,
                GroupNode group => CompileGroup(group, ast),
                _ => ""
            };
        }

        private string CompileToken(TokenNode token)
        {
            if (!_tokenKeyToPattern.TryGetValue(token.TokenKey, out var pattern))
            {
                return $"{{{token.TokenKey}}}"; // Fallback
            }

            // Handle custom formatting for PartNumber and PartCount
            if ((token.TokenKey == "PartNumber" || token.TokenKey == "PartCount") && 
                token.Args.TryGetValue("format", out var formatObj))
            {
                var format = formatObj.ToString();
                return $"{{{token.TokenKey}:{format}}}";
            }

            return pattern;
        }

        private string CompileGroup(GroupNode group, PatternAst ast)
        {
            if (group.Mode != "paren")
                return "";

            var children = new StringBuilder();
            foreach (var childId in group.Children)
            {
                if (ast.NodesById.TryGetValue(childId, out var child))
                {
                    children.Append(CompileNode(child, ast));
                }
            }

            var content = children.ToString();
            
            // Handle conditional parentheses - wrap with { } for conditional logic
            if (group.OmitIfEmpty && !string.IsNullOrEmpty(content))
            {
                return $"{{({content})}}";
            }
            
            return $"({content})";
        }

        public PatternAst Decompile(string pattern)
        {
            var ast = new PatternAst();
            var tokens = TokenizePattern(pattern);

            foreach (var token in tokens)
            {
                var (node, childNodes) = ParseTokenWithChildren(token);
                if (node != null)
                {
                    // Add the main node
                    ast.NodesById[node.Id] = node;
                    ast.RootIds.Add(node.Id);
                    
                    // Add any child nodes from groups
                    foreach (var childNode in childNodes)
                    {
                        ast.NodesById[childNode.Id] = childNode;
                    }
                }
            }

            return ast;
        }

        private List<string> TokenizePattern(string pattern)
        {
            var tokens = new List<string>();
            var current = new StringBuilder();
            var inToken = false;
            var braceDepth = 0;

            for (int i = 0; i < pattern.Length; i++)
            {
                var c = pattern[i];

                if (c == '{')
                {
                    if (!inToken)
                    {
                        // Save any text before token
                        if (current.Length > 0)
                        {
                            tokens.Add(current.ToString());
                            current.Clear();
                        }
                        inToken = true;
                        braceDepth = 1;
                        continue; // Don't append the opening brace
                    }
                    braceDepth++;
                }
                else if (c == '}' && inToken)
                {
                    braceDepth--;
                    if (braceDepth == 0)
                    {
                        // Complete token - wrap with braces
                        tokens.Add("{" + current.ToString() + "}");
                        current.Clear();
                        inToken = false;
                        continue; // Don't append the closing brace
                    }
                }

                current.Append(c);
            }

            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
            }

            return tokens.Where(t => !string.IsNullOrEmpty(t)).ToList();
        }

        private (PatternNode node, List<PatternNode> childNodes) ParseTokenWithChildren(string token)
        {
            var childNodes = new List<PatternNode>();

            // Check if it's a known token pattern
            if (_patternToTokenKey.TryGetValue(token, out var tokenKey))
            {
                return (new TokenNode { TokenKey = tokenKey }, childNodes);
            }

            // Check for formatted tokens like {PartNumber:00}
            var formatMatch = Regex.Match(token, @"^\{(PartNumber|PartCount):([^}]+)\}$");
            if (formatMatch.Success)
            {
                return (new TokenNode 
                { 
                    TokenKey = formatMatch.Groups[1].Value,
                    Args = new Dictionary<string, object> { { "format", formatMatch.Groups[2].Value } }
                }, childNodes);
            }

            // Check for conditional groups like {(content)}
            var conditionalMatch = Regex.Match(token, @"^\{\((.+)\)\}$");
            if (conditionalMatch.Success)
            {
                var content = conditionalMatch.Groups[1].Value;
                var group = new GroupNode { Mode = "paren", OmitIfEmpty = true };
                
                // Recursively parse content
                var contentTokens = TokenizePattern(content);
                foreach (var contentToken in contentTokens)
                {
                    var (childNode, grandChildren) = ParseTokenWithChildren(contentToken);
                    if (childNode != null)
                    {
                        childNodes.Add(childNode);
                        childNodes.AddRange(grandChildren);
                        group.Children.Add(childNode.Id);
                    }
                }

                return (group, childNodes);
            }

            // Check for simple parentheses
            if (token.StartsWith("(") && token.EndsWith(")"))
            {
                var content = token.Substring(1, token.Length - 2);
                var group = new GroupNode { Mode = "paren", OmitIfEmpty = false };
                
                var contentTokens = TokenizePattern(content);
                foreach (var contentToken in contentTokens)
                {
                    var (childNode, grandChildren) = ParseTokenWithChildren(contentToken);
                    if (childNode != null)
                    {
                        childNodes.Add(childNode);
                        childNodes.AddRange(grandChildren);
                        group.Children.Add(childNode.Id);
                    }
                }

                return (group, childNodes);
            }

            // Treat as separator/literal text
            if (!token.StartsWith("{"))
            {
                return (new SeparatorNode { Value = token }, childNodes);
            }

            // Preserve unknown tokens as literal text so decompile→compile round-trips don't drop user tokens.
            // This is important for patterns that include tokens supported by FileNameBuilder but not yet mapped here.
            return (new SeparatorNode { Value = token }, childNodes);
        }
    }
}
