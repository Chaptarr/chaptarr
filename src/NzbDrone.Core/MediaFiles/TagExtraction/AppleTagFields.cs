using System;
using System.Collections.Generic;
using System.Linq;
using TagLib;
using TagLib.Mpeg4;

namespace NzbDrone.Core.MediaFiles.TagExtraction
{
    internal static class AppleTagFields
    {
        internal const string ToneNamespace = "com.pilabor.tone";
        private const string TonePartName = "PART";
        internal const string ToneAsinName = "AUDIBLE_ASIN";
        internal const string ToneIsbnName = "ISBN";
        internal const string ToneAsinKey = "MP4:----:" + ToneNamespace + ":" + ToneAsinName;
        internal const string ToneIsbnKey = "MP4:----:" + ToneNamespace + ":" + ToneIsbnName;
        internal const string ToneSeriesKey = "MP4:----:" + ToneNamespace + ":SERIES";
        internal const string TonePartKey = "MP4:----:" + ToneNamespace + ":" + TonePartName;

        internal static string FieldName(string mean, string key) =>
            string.Equals(mean, ToneNamespace, StringComparison.OrdinalIgnoreCase) && key == "SERIES-PART" ? TonePartName : key;

        internal static string SeriesKey(string mean, string name)
        {
            if (!string.Equals(mean, ToneNamespace, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(mean, "com.apple.iTunes", StringComparison.OrdinalIgnoreCase)) return null;
            return new[] { "SERIES", "SERIES-PART" }.FirstOrDefault(key => string.Equals(name, FieldName(mean, key), StringComparison.OrdinalIgnoreCase));
        }

        internal static bool IsTonePublisher(string mean, string name) =>
            string.Equals(mean, ToneNamespace, StringComparison.OrdinalIgnoreCase) && string.Equals(name, "PUBLISHER", StringComparison.OrdinalIgnoreCase);

        internal static string ReadPublisher(AppleTag tag)
        {
            var tone = Read(tag).Where(field => IsTonePublisher(field.Mean, field.Name) && !string.IsNullOrEmpty(field.Value))
                .Select(field => field.Value).Distinct(StringComparer.Ordinal).ToList();
            return tone.Count > 0 ? string.Join("; ", tone) : tag.Publisher;
        }

        internal static void WritePublisher(AppleTag tag, string value)
        {
            var tone = Read(tag).Where(field => IsTonePublisher(field.Mean, field.Name)).ToList();
            if (tone.Count == 0)
            {
                tag.Publisher = value;
                return;
            }

            foreach (var field in tone) SetText(tag, field.Mean, field.Name, null);
            SetText(tag, "com.apple.iTunes", "publisher", null);
            if (!string.IsNullOrEmpty(value)) SetText(tag, ToneNamespace, "PUBLISHER", value);
        }

        internal static void SetText(AppleTag tag, string mean, string name, string value)
        {
            var count = Read(tag).Count(field => field.Mean == mean && field.Name == name);
            for (var i = 0; i < count; i++) tag.SetDashBox(mean, name, null);
            // Malformed shadow boxes can prevent TagLib from removing the real field.
            // Fail this file rather than loop forever or add a competing value.
            if (Read(tag).Any(field => field.Mean == mean && field.Name == name))
                throw new TagLib.CorruptFileException("Unable to replace malformed MP4 custom tag");
            if (!string.IsNullOrEmpty(value)) tag.SetDashBox(mean, name, value);
        }

        internal static IEnumerable<(string Mean, string Name, string Value)> Read(AppleTag tag)
        {
            foreach (var box in tag.Where(box => box.BoxType == new ByteVector("----")))
            {
                var mean = (box.GetChild(new ByteVector("mean")) as AppleAdditionalInfoBox)?.Text;
                var name = (box.GetChild(new ByteVector("name")) as AppleAdditionalInfoBox)?.Text;
                foreach (var data in box.Children.OfType<AppleDataBox>())
                {
                    yield return (mean, name, data.Text);
                }
            }
        }
    }
}
