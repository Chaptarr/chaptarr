using System;
using System.Collections.Generic;
using System.Linq;
using TagLib;
using TagLib.Id3v2;

namespace NzbDrone.Core.MediaFiles.TagExtraction
{
    public class TagLibSharpExtractor : ITagExtractorWithDuration
    {
        public bool IsAvailable => true; // Managed fallback
        public int Priority => 2;
        public string Name => "TagLibSharp";

        public Dictionary<string, List<string>> ExtractTags(string path)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            TagLib.File file = null;
            try
            {
                file = TagLib.File.Create(path, ReadStyle.None);

                // Enumerate container-specific tags and add textual values only
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
            }
            finally
            {
                file?.Dispose();
            }

            return result;
        }

        public (Dictionary<string, List<string>> Tags, int? DurationSeconds) ExtractTagsAndDuration(string path)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            TagLib.File file = null;
            TimeSpan duration = TimeSpan.Zero;
            var isMp3 = Mp3DurationReader.IsMp3Path(path);

            try
            {
                // For MP3, do not ask TagLibSharp for duration: it estimates CBR
                // duration from the first valid frame, which is the failure mode
                // the shared AudioDurationResolver deliberately rejects.
                file = TagLib.File.Create(path, isMp3 ? ReadStyle.None : ReadStyle.Average);
                duration = isMp3 ? TimeSpan.Zero : file.Properties?.Duration ?? TimeSpan.Zero;

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
            }
            finally
            {
                file?.Dispose();
            }

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
            catch { /* ignore */ }
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
                    "GROUPING","SUBTITLE","DISCNUMBER","TOTALDISCS","TOTALTRACKS","COMPILATION","COMMENT","NARRATOR","PUBLISHER","LANGUAGE","ISBN","ASIN","BARCODE",
                    "CATALOGNUMBER","SERIES","PART","BOOK","AUTHOR","READER","ENCODED-BY","ENCODER","ENCODING","ENCODEDBY","SOURCEMEDIA","MEDIA","ORIGINALDATE","ORIGINALYEAR","RELEASEDATE",
                    "RELEASETYPE","RELEASESTATUS","RELEASECOUNTRY","ALBUMARTISTSORT","ARTISTSORT","ALBUMSORT","TITLESORT","MOOD","BPM","KEY","CONTENTGROUP","TLEN"
                };

                foreach (var name in fields)
                {
                    var v1 = xiph.GetField(name);
                    if (v1 != null && v1.Length > 0) Add(result, $"XIPH:{name}", v1);

                    if (!name.Equals(name.ToLowerInvariant()))
                    {
                        var v2 = xiph.GetField(name.ToLowerInvariant());
                        if (v2 != null && v2.Length > 0) Add(result, $"XIPH:{name.ToLowerInvariant()}", v2);
                    }
                }
            }
            catch { /* ignore */ }
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
            catch { /* ignore */ }
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
            catch { /* ignore */ }
        }

        private static void ExtractAppleTags(TagLib.File file, Dictionary<string, List<string>> result)
        {
            try
            {
                var tag = (TagLib.Mpeg4.AppleTag)file.GetTag(TagTypes.Apple);
                // A limited set of common boxes (field-agnostic enumeration is not available in TagLib#)
                var boxes = new[] { "©nam","©ART","©alb","©day","©cmt","©gen","©wrt","©too","©cpy","aART","©grp","©lyr","©dir","©pub","©wrk" };
                foreach (var b in boxes)
                {
                    try
                    {
                        var val = tag.DataBoxes(FixAppleId(b)).FirstOrDefault()?.Text;
                        Add(result, $"MP4:{b}", val);
                    }
                    catch { /* ignore */ }
                }

                // Attempt to extract vendor-specific freeform atoms ("----")
                // Using reflection for compatibility across TagLib# versions
                try
                {
                    var tagType = tag.GetType();
                    var getDashBoxes = tagType.GetMethod("GetDashBoxes", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                    if (getDashBoxes != null)
                    {
                        var dashBoxes = getDashBoxes.Invoke(tag, null) as System.Collections.IEnumerable;
                        if (dashBoxes != null)
                        {
                            foreach (var box in dashBoxes)
                            {
                                try
                                {
                                    var bt = box.GetType();
                                    var mean = bt.GetProperty("Mean")?.GetValue(box) as string;
                                    var name = bt.GetProperty("Name")?.GetValue(box) as string;

                                    var values = new List<string>();
                                    var textProp = bt.GetProperty("Text");
                                    if (textProp != null)
                                    {
                                        var tv = textProp.GetValue(box);
                                        if (tv is string s) values.Add(s);
                                        else if (tv is string[] sa) values.AddRange(sa.Where(x => !string.IsNullOrWhiteSpace(x)));
                                        else if (tv != null) values.Add(tv.ToString());
                                    }
                                    else
                                    {
                                        var dataProp = bt.GetProperty("Data");
                                        var dv = dataProp?.GetValue(box);
                                        if (dv != null)
                                        {
                                            var str = dv.ToString();
                                            if (!string.IsNullOrWhiteSpace(str)) values.Add(str);
                                        }
                                    }

                                    if (!string.IsNullOrWhiteSpace(mean) && !string.IsNullOrWhiteSpace(name) && values.Count > 0)
                                    {
                                        Add(result, $"MP4:----:{mean}:{name}", values.ToArray());
                                    }
                                }
                                catch { /* per-box best effort */ }
                            }
                        }
                    }
                    else
                    {
                        // Fallback: try to access DataBoxes("----") and record opaque values
                        try
                        {
                            var freeform = tag.DataBoxes(FixAppleId("----"));
                            foreach (var db in freeform)
                            {
                                try
                                {
                                    var text = db?.Text;
                                    if (text != null && text.Length > 0)
                                    {
                                        Add(result, "MP4:----", text);
                                    }
                                }
                                catch { }
                            }
                        }
                        catch { }
                    }
                }
                catch { /* ignore reflection errors */ }
            }
            catch { /* ignore */ }
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
