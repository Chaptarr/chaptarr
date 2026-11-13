using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Instrumentation;
using TagLib;
using TagLib.Id3v2;

namespace NzbDrone.Core.MediaFiles.TagExtraction
{
    public class TagLibSharpExtractor : ITagExtractorWithDuration
    {
        private static readonly Logger Logger = NzbDroneLogger.GetLogger(typeof(TagLibSharpExtractor));

        public bool IsAvailable => true; // Managed fallback
        public int Priority => 2;
        public string Name => "TagLibSharp";

        public Dictionary<string, List<string>> ExtractTags(string path)
        {
            using var file = TagLib.File.Create(path, ReadStyle.None);
            return ExtractTags(file);
        }

        // Share container interpretation with callers that already hold the file open.
        public static Dictionary<string, List<string>> ExtractTags(TagLib.File file)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (file.TagTypesOnDisk.HasFlag(TagTypes.Id3v2))
            {
                ExtractId3v2Tags(file, result);
            }
            if (file.TagTypesOnDisk.HasFlag(TagTypes.Xiph))
            {
                ExtractXiphTags(file, result);
            }
            if (file.TagTypesOnDisk.HasFlag(TagTypes.Ape))
            {
                ExtractApeTags(file, result);
            }
            if (file.TagTypesOnDisk.HasFlag(TagTypes.Asf))
            {
                ExtractAsfTags(file, result);
            }
            if (file.TagTypesOnDisk.HasFlag(TagTypes.Apple))
            {
                ExtractAppleTags(file, result);
            }
            return result;
        }

        public (Dictionary<string, List<string>> Tags, int? DurationSeconds) ExtractTagsAndDuration(string path)
        {
            var isMp3 = Mp3DurationReader.IsMp3Path(path);
            // MP3 duration comes from AudioDurationResolver, not TagLib's first-frame estimate.
            using var file = TagLib.File.Create(path, isMp3 ? ReadStyle.None : ReadStyle.Average);
            var duration = isMp3 ? TimeSpan.Zero : file.Properties?.Duration ?? TimeSpan.Zero;
            var result = ExtractTags(file);

            // AudioDurationResolver owns MP3 duration. Returning no MP3 duration here
            // prevents both duplicate frame scans and first-frame estimates.
            var durationSeconds = isMp3 ? null : MediaDuration.FromTimeSpan(duration);
            return (result, durationSeconds);
        }

        private static void Add(Dictionary<string, List<string>> dict, string key, params string[] values)
        {
            if (string.IsNullOrWhiteSpace(key) || values == null || values.Length == 0)
                return;
            var nonEmpty = values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToList();
            if (nonEmpty.Count == 0) return;
            if (!dict.TryGetValue(key, out var list))
            {
                list = new List<string>();
                dict[key] = list;
            }
            list.AddRange(nonEmpty);
        }

        private static void ExtractId3v2Tags(TagLib.File file, Dictionary<string, List<string>> result)
        {
            try
            {
                var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagTypes.Id3v2);
                foreach (var frame in id3.GetFrames())
                {
                    switch (frame)
                    {
                        case UserTextInformationFrame userText:
                            if (userText.Text != null)
                                Add(result, $"ID3v2:TXXX:{userText.Description}", userText.Text);
                            break;
                        case TextInformationFrame text:
                            if (text.Text != null)
                                Add(result, $"ID3v2:{text.FrameId}", text.Text);
                            break;
                        case CommentsFrame comm:
                            Add(result, $"ID3v2:COMM:{comm.Description}", comm.Text);
                            break;
                        case UnsynchronisedLyricsFrame uslt:
                            Add(result, $"ID3v2:USLT:{uslt.Description}", uslt.Text);
                            break;
                        case UrlLinkFrame url when url is UserUrlLinkFrame u:
                            Add(result, $"ID3v2:WXXX:{u.Description}", u.Text?.FirstOrDefault());
                            break;
                        case UrlLinkFrame url2:
                            Add(result, $"ID3v2:URL:{url2.FrameId}", url2.Text?.FirstOrDefault());
                            break;
                        case PopularimeterFrame popm:
                            Add(result, $"ID3v2:POPM:{popm.User}", popm.Rating.ToString());
                            break;
                        case UniqueFileIdentifierFrame ufid:
                            Add(result, $"ID3v2:UFID:{ufid.Owner}", "[Binary Data]");
                            break;
                        case PrivateFrame _:
                        case AttachmentFrame _:
                            // Binary frames -> placeholder only
                            Add(result, $"ID3v2:BIN:{frame.FrameId}", "[Binary Data]");
                            break;
                        default:
                            if (frame != null)
                            {
                                Add(result, $"ID3v2:{frame.FrameId}", "[Unknown Frame]");
                            }
                            break;
                    }
                }
            }
            catch (Exception ex) { Logger.Debug(ex, "Unable to extract Id3v2 tags from {0}", file.Name); }
        }

        private static void ExtractXiphTags(TagLib.File file, Dictionary<string, List<string>> result)
        {
            try
            {
                var xiph = (TagLib.Ogg.XiphComment)file.GetTag(TagTypes.Xiph);
                // Common fields
                Add(result, "XIPH:TITLE", xiph.Title);
                Add(result, "XIPH:ALBUM", xiph.Album);
                Add(result, "XIPH:ARTIST", xiph.Performers);
                Add(result, "XIPH:ALBUMARTIST", xiph.AlbumArtists);
                Add(result, "XIPH:COMPOSER", xiph.Composers);
                Add(result, "XIPH:COMMENT", xiph.Comment);
                Add(result, "XIPH:GENRE", xiph.Genres);
                if (xiph.Year != 0) Add(result, "XIPH:DATE", xiph.Year.ToString());

                // Attempt to capture a broad set of additional fields commonly used
                var fields = new[]
                {
                    "TITLE","VERSION","ALBUM","TRACKNUMBER","ARTIST","PERFORMER","COPYRIGHT","LICENSE","ORGANIZATION","DESCRIPTION","GENRE","DATE",
                    "LOCATION","CONTACT","ISRC","ALBUMARTIST","COMPOSER","LYRICIST","CONDUCTOR","REMIXER","ARRANGER","ENGINEER","PRODUCER","DJMIXER","MIXER","LABEL",
                    "GROUPING","SUBTITLE","DISCNUMBER","TOTALDISCS","TOTALTRACKS","TRACKTOTAL","COMPILATION","COMMENT","NARRATOR","PUBLISHER","LANGUAGE","ISBN","ASIN","BARCODE",
                    "CATALOGNUMBER","SERIES","PART","BOOK","AUTHOR","READER","ENCODED-BY","ENCODER","ENCODING","ENCODEDBY","SOURCEMEDIA","MEDIA","ORIGINALDATE","ORIGINALYEAR","RELEASEDATE",
                    "RELEASETYPE","RELEASESTATUS","RELEASECOUNTRY","ALBUMARTISTSORT","ARTISTSORT","ALBUMSORT","TITLESORT","MOOD","BPM","KEY","CONTENTGROUP","TLEN"
                };

                foreach (var name in fields)
                {
                    var v1 = xiph.GetField(name);
                    if (v1 != null && v1.Length > 0) Add(result, $"XIPH:{name}", v1);
                }
            }
            catch (Exception ex) { Logger.Debug(ex, "Unable to extract Xiph tags from {0}", file.Name); }
        }

        private static void ExtractApeTags(TagLib.File file, Dictionary<string, List<string>> result)
        {
            try
            {
                var ape = (TagLib.Ape.Tag)file.GetTag(TagTypes.Ape);
                var keys = new[] { "Title","Artist","Album","Comment","Year","Track","Genre","AlbumArtist","Composer","Disc","Compilation","Lyrics","Media","Publisher","Mood","Narrator","Language","ISBN","ASIN" };
                foreach (var key in keys)
                {
                    var item = ape.GetItem(key);
                    if (item != null)
                    {
                        Add(result, $"APE:{key}", item.ToString());
                    }
                }
            }
            catch (Exception ex) { Logger.Debug(ex, "Unable to extract Ape tags from {0}", file.Name); }
        }

        private static void ExtractAsfTags(TagLib.File file, Dictionary<string, List<string>> result)
        {
            try
            {
                var asf = (TagLib.Asf.Tag)file.GetTag(TagTypes.Asf);
                var descriptors = new[]
                {
                    "WM/AlbumTitle","WM/AlbumArtist","WM/Composer","WM/Conductor","WM/ContentDistributor","WM/Publisher","WM/Media","WM/Narrator","WM/Language","WM/PartOfSet","WM/TrackNumber","WM/SharedUserRating","WM/SubTitle","WM/Writer","WM/ISBN","WM/ASIN","WM/OriginalReleaseTime","WM/OriginalReleaseYear"
                };
                foreach (var d in descriptors)
                {
                    Add(result, $"ASF:{d}", asf.GetDescriptorString(d));
                }
            }
            catch (Exception ex) { Logger.Debug(ex, "Unable to extract Asf tags from {0}", file.Name); }
        }

        private static void ExtractAppleTags(TagLib.File file, Dictionary<string, List<string>> result)
        {
            try
            {
                var tag = (TagLib.Mpeg4.AppleTag)file.GetTag(TagTypes.Apple);
                // Read known Apple text boxes; canonical mappings consume a subset of these fields.
                var boxes = new[] { "©nam","©ART","©alb","©day","©cmt","©gen","©wrt","©too","©cpy","aART","©grp","©lyr","©dir","©pub","©wrk" };
                foreach (var b in boxes)
                {
                    try
                    {
                        var val = ReadAppleText(tag, b);
                        Add(result, $"MP4:{b}", val);
                    }
                    catch (Exception ex) { Logger.Debug(ex, "Unable to extract MP4 box {0} from {1}", b, file.Name); }
                }

                Add(result, "MP4:----:com.apple.iTunes:publisher", tag.GetDashBox("com.apple.iTunes", "publisher"));
                // Preserve other freeform text as raw evidence.
                foreach (var box in tag.DataBoxes(FixAppleId("----")))
                {
                    Add(result, "MP4:----", box.Text);
                }
            }
            catch (Exception ex) { Logger.Debug(ex, "Unable to extract Apple tags from {0}", file.Name); }
        }

        internal static string ReadAppleText(TagLib.Mpeg4.AppleTag tag, string id)
        {
            return tag.DataBoxes(FixAppleId(id)).FirstOrDefault()?.Text;
        }

        private static ReadOnlyByteVector FixAppleId(string id)
        {
            // Apple box IDs are 4 bytes. Tags commonly use "©" which is 0xA9 as a single byte (Latin-1),
            // not the 2-byte UTF-8 sequence (0xC2 0xA9). Using UTF-8 here breaks lookups for all "©" boxes
            // (©nam/©alb/©ART/©cmt/etc) and results in missing tags for many M4B files.
            try
            {
                var bytes = System.Text.Encoding.Latin1.GetBytes(id);
                if (bytes.Length == 4) return new ReadOnlyByteVector(bytes);
                if (bytes.Length == 3) return new ReadOnlyByteVector(0xa9, bytes[0], bytes[1], bytes[2]);
            }
            catch
            {
                // best-effort; fall through to empty
            }

            return new ReadOnlyByteVector();
        }
    }
}
