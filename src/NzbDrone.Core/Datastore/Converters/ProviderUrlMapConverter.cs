using System.Data;
using System.Text.Json;
using Dapper;
using NzbDrone.Core.Books;

namespace NzbDrone.Core.Datastore.Converters
{
    public sealed class ProviderUrlMapConverter : SqlMapper.TypeHandler<ProviderUrlMap>
    {
        private static readonly JsonSerializerOptions SerializerSettings = new JsonSerializerOptions
        {
            AllowTrailingCommas = true,
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };

        public override void SetValue(IDbDataParameter parameter, ProviderUrlMap value)
        {
            parameter.Value = JsonSerializer.Serialize((object)(value ?? new ProviderUrlMap()), SerializerSettings);
        }

        public override ProviderUrlMap Parse(object value)
        {
            if (value is not string json || string.IsNullOrWhiteSpace(json))
            {
                return new ProviderUrlMap();
            }

            try
            {
                using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return new ProviderUrlMap();
                }

                var map = new ProviderUrlMap();
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    map.SetNormalized(prop.Name, prop.Value.GetString());
                }

                return map;
            }
            catch
            {
                return new ProviderUrlMap();
            }
        }
    }
}
