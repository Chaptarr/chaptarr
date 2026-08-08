using System;
using System.Collections.Generic;
using System.Text.Json;
using Newtonsoft.Json;

namespace NzbDrone.Core.Organizer.NamingPattern
{
    [JsonConverter(typeof(PatternNodeConverter))]
    [System.Text.Json.Serialization.JsonConverter(typeof(PatternNodeStjConverter))]
    public abstract class PatternNode
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public abstract string Kind { get; }
    }

    public class TokenNode : PatternNode
    {
        public override string Kind => "token";
        public string TokenKey { get; set; }
        public Dictionary<string, object> Args { get; set; } = new Dictionary<string, object>();
    }

    public class SeparatorNode : PatternNode
    {
        public override string Kind => "separator";
        public string Value { get; set; }
    }

    public class GroupNode : PatternNode
    {
        public override string Kind => "group";
        public string Mode { get; set; } = "paren";
        public List<string> Children { get; set; } = new List<string>();
        public bool OmitIfEmpty { get; set; } = true;
    }

    public class PatternAst
    {
        public Dictionary<string, PatternNode> NodesById { get; set; } = new Dictionary<string, PatternNode>();
        public List<string> RootIds { get; set; } = new List<string>();
    }

    public sealed class PatternNodeStjConverter : System.Text.Json.Serialization.JsonConverter<PatternNode>
    {
        public override PatternNode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return null;
            }

            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new System.Text.Json.JsonException($"Expected StartObject, got {reader.TokenType}.");
            }

            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;

            if (!root.TryGetProperty("kind", out var kindProp) || kindProp.ValueKind != JsonValueKind.String)
            {
                throw new System.Text.Json.JsonException("Missing required property 'kind'.");
            }

            var kind = kindProp.GetString();

            PatternNode node = kind switch
            {
                "token" => new TokenNode(),
                "separator" => new SeparatorNode(),
                "group" => new GroupNode(),
                _ => throw new System.Text.Json.JsonException($"Unknown node kind: {kind}")
            };

            if (root.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
            {
                node.Id = idProp.GetString() ?? Guid.NewGuid().ToString();
            }

            switch (node)
            {
                case TokenNode token:
                    if (root.TryGetProperty("tokenKey", out var tokenKeyProp) && tokenKeyProp.ValueKind == JsonValueKind.String)
                    {
                        token.TokenKey = tokenKeyProp.GetString();
                    }

                    if (root.TryGetProperty("args", out var argsProp) && argsProp.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
                    {
                        token.Args = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(argsProp.GetRawText(), options) ??
                                     new Dictionary<string, object>();
                    }
                    break;

                case SeparatorNode separator:
                    if (root.TryGetProperty("value", out var valueProp) && valueProp.ValueKind == JsonValueKind.String)
                    {
                        separator.Value = valueProp.GetString();
                    }
                    break;

                case GroupNode group:
                    if (root.TryGetProperty("mode", out var modeProp) && modeProp.ValueKind == JsonValueKind.String)
                    {
                        group.Mode = modeProp.GetString() ?? "paren";
                    }

                    if (root.TryGetProperty("children", out var childrenProp) && childrenProp.ValueKind == JsonValueKind.Array)
                    {
                        group.Children = System.Text.Json.JsonSerializer.Deserialize<List<string>>(childrenProp.GetRawText(), options) ??
                                         new List<string>();
                    }

                    if (root.TryGetProperty("omitIfEmpty", out var omitProp) &&
                        (omitProp.ValueKind == JsonValueKind.True || omitProp.ValueKind == JsonValueKind.False))
                    {
                        group.OmitIfEmpty = omitProp.GetBoolean();
                    }
                    break;
            }

            return node;
        }

        public override void Write(Utf8JsonWriter writer, PatternNode value, JsonSerializerOptions options)
        {
            if (value is null)
            {
                writer.WriteNullValue();
                return;
            }

            writer.WriteStartObject();
            writer.WriteString("id", value.Id);
            writer.WriteString("kind", value.Kind);

            switch (value)
            {
                case TokenNode token:
                    writer.WriteString("tokenKey", token.TokenKey);
                    if (token.Args is { Count: > 0 })
                    {
                        writer.WritePropertyName("args");
                        System.Text.Json.JsonSerializer.Serialize(writer, token.Args, options);
                    }
                    break;

                case SeparatorNode separator:
                    writer.WriteString("value", separator.Value);
                    break;

                case GroupNode group:
                    writer.WriteString("mode", group.Mode);
                    writer.WritePropertyName("children");
                    System.Text.Json.JsonSerializer.Serialize(writer, group.Children ?? new List<string>(), options);
                    if (!group.OmitIfEmpty)
                    {
                        writer.WriteBoolean("omitIfEmpty", group.OmitIfEmpty);
                    }
                    break;
            }

            writer.WriteEndObject();
        }
    }

    // Custom JSON converter to handle polymorphic serialization
    public class PatternNodeConverter : JsonConverter<PatternNode>
    {
        public override void WriteJson(JsonWriter writer, PatternNode value, Newtonsoft.Json.JsonSerializer serializer)
        {
            var jo = new Newtonsoft.Json.Linq.JObject();
            jo.Add("id", value.Id);
            jo.Add("kind", value.Kind);

            switch (value)
            {
                case TokenNode token:
                    jo.Add("tokenKey", token.TokenKey);
                    if (token.Args.Count > 0)
                        jo.Add("args", Newtonsoft.Json.Linq.JObject.FromObject(token.Args));
                    break;
                case SeparatorNode separator:
                    jo.Add("value", separator.Value);
                    break;
                case GroupNode group:
                    jo.Add("mode", group.Mode);
                    jo.Add("children", Newtonsoft.Json.Linq.JArray.FromObject(group.Children));
                    if (!group.OmitIfEmpty)
                        jo.Add("omitIfEmpty", group.OmitIfEmpty);
                    break;
            }

            jo.WriteTo(writer);
        }

        public override PatternNode ReadJson(JsonReader reader, Type objectType, PatternNode existingValue, bool hasExistingValue, Newtonsoft.Json.JsonSerializer serializer)
        {
            var jo = Newtonsoft.Json.Linq.JObject.Load(reader);
            var kind = jo["kind"]?.ToString();

            PatternNode node = kind switch
            {
                "token" => new TokenNode(),
                "separator" => new SeparatorNode(),
                "group" => new GroupNode(),
                _ => throw new Newtonsoft.Json.JsonException($"Unknown node kind: {kind}")
            };

            node.Id = jo["id"]?.ToString() ?? Guid.NewGuid().ToString();

            switch (node)
            {
                case TokenNode token:
                    token.TokenKey = jo["tokenKey"]?.ToString();
                    if (jo["args"] != null)
                        token.Args = jo["args"].ToObject<Dictionary<string, object>>();
                    break;
                case SeparatorNode separator:
                    separator.Value = jo["value"]?.ToString();
                    break;
                case GroupNode group:
                    group.Mode = jo["mode"]?.ToString() ?? "paren";
                    group.Children = jo["children"]?.ToObject<List<string>>() ?? new List<string>();
                    group.OmitIfEmpty = jo["omitIfEmpty"]?.ToObject<bool>() ?? true;
                    break;
            }

            return node;
        }
    }
}
