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

            foreach (var field in tone)
            {
                while (Read(tag).Any(stored => stored.Mean == field.Mean && stored.Name == field.Name)) tag.SetDashBox(field.Mean, field.Name, null);
            }
            while (Read(tag).Any(field => field.Mean == "com.apple.iTunes" && field.Name == "publisher"))
                tag.SetDashBox("com.apple.iTunes", "publisher", null);
            if (!string.IsNullOrEmpty(value)) tag.SetDashBox(ToneNamespace, "PUBLISHER", value);
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
