using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Datastore.Converters
{
    public class CommandConverter : EmbeddedDocumentConverter<Command>
    {
        private static readonly IReadOnlyDictionary<string, Type> AllowedCommandTypes = BuildAllowedCommandTypes();

        public override Command Parse(object value)
        {
            var stringValue = (string)value;

            if (stringValue.IsNullOrWhiteSpace())
            {
                return null;
            }

            string contract;
            using (var body = JsonDocument.Parse(stringValue))
            {
                contract = body.RootElement.GetProperty("name").GetString();
            }

            if (contract.IsNullOrWhiteSpace() || !AllowedCommandTypes.TryGetValue(contract, out var impType))
            {
                var result = JsonSerializer.Deserialize<UnknownCommand>(stringValue, SerializerSettings);

                result.ContractName = contract;

                return result;
            }

            return (Command)JsonSerializer.Deserialize(stringValue, impType, SerializerSettings);
        }

        public override void SetValue(IDbDataParameter parameter, Command value)
        {
            // Cast to object to get all properties written out
            // https://github.com/dotnet/corefx/issues/38650
            parameter.Value = value == null ? null : JsonSerializer.Serialize((object)value, SerializerSettings);
        }

        private static IReadOnlyDictionary<string, Type> BuildAllowedCommandTypes()
        {
            return typeof(Command).Assembly.GetTypes()
                .Where(t => typeof(Command).IsAssignableFrom(t) &&
                            t.IsClass &&
                            !t.IsAbstract &&
                            t.Name.EndsWith("Command", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(t => t.Name.Substring(0, t.Name.Length - "Command".Length), t => t, StringComparer.OrdinalIgnoreCase);
        }
    }
}
