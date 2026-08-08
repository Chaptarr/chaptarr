using System;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using NLog;

namespace NzbDrone.Core.MetadataSource.BookInfo.V5
{
    /// <summary>
    /// Custom JSON converter that handles invalid DateTime values gracefully.
    /// Returns null for dates that are outside the valid DateTime range.
    /// </summary>
    public class SafeDateTimeConverter : DateTimeConverterBase
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        // .NET DateTime valid range
        private static readonly DateTime MinDate = DateTime.MinValue;
        private static readonly DateTime MaxDate = DateTime.MaxValue;

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
            {
                return null;
            }

            if (reader.TokenType == JsonToken.Date)
            {
                return reader.Value;
            }

            if (reader.TokenType == JsonToken.String)
            {
                var dateString = reader.Value?.ToString();

                if (string.IsNullOrWhiteSpace(dateString))
                {
                    return null;
                }

                // Try to parse the date
                if (DateTime.TryParse(
                        dateString,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var date))
                {
                    // Check if the date is within valid range
                    if (date >= MinDate && date <= MaxDate)
                    {
                        return date;
                    }
                    else
                    {
                        Logger.Trace("Date '{0}' is outside valid DateTime range, returning null", dateString);
                        return null;
                    }
                }

                // Special handling for year-only dates or dates with invalid years
                if (dateString.Contains("-"))
                {
                    var parts = dateString.Split('-');
                    if (parts.Length >= 1 && int.TryParse(parts[0], out var year))
                    {
                        // Check if year is reasonable (between 1000 and 3000)
                        if (year < 1000 || year > 3000)
                        {
                            Logger.Trace("Invalid year {0} in date '{1}', returning null", year, dateString);
                            return null;
                        }
                    }
                }

                Logger.Trace("Could not parse date string '{0}', returning null", dateString);
                return null;
            }

            throw new JsonSerializationException($"Unexpected token type {reader.TokenType} when parsing date");
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            if (value == null)
            {
                writer.WriteNull();
            }
            else if (value is DateTime dateTime)
            {
                writer.WriteValue(dateTime.ToString("yyyy-MM-dd"));
            }
            else
            {
                throw new JsonSerializationException($"Unexpected value type {value.GetType()} when writing date");
            }
        }
    }
}
