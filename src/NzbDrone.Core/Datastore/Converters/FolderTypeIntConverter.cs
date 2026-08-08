using System;
using System.Data;
using Dapper;
using NLog;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.Datastore.Converters
{
    public class FolderTypeIntConverter : SqlMapper.TypeHandler<FolderType>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public override void SetValue(IDbDataParameter parameter, FolderType value)
        {
            parameter.Value = (int)value;
        }

        public override FolderType Parse(object value)
        {
            if (value == null || value is DBNull)
            {
                return FolderType.Mixed;
            }

            // Handle both string and int values for backward compatibility
            if (value is string stringValue)
            {
                if (int.TryParse(stringValue, out var intValue))
                {
                    return ParseInt(intValue, stringValue);
                }

                // Try to parse as enum name - handle legacy "Unknown" as Mixed
                if (stringValue.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                {
                    return FolderType.Mixed;
                }
                
                if (Enum.TryParse<FolderType>(stringValue, true, out var enumValue))
                {
                    return enumValue;
                }

                Logger.Warn("Invalid root folder type '{0}'. Defaulting to {1}.", stringValue, FolderType.Mixed);
                return FolderType.Mixed;
            }

            if (value is int || value is long)
            {
                return ParseInt(Convert.ToInt32(value), value);
            }

            Logger.Warn("Invalid root folder type '{0}' ({1}). Defaulting to {2}.", value, value.GetType().FullName, FolderType.Mixed);
            return FolderType.Mixed;
        }

        private static FolderType ParseInt(int intValue, object originalValue)
        {
            if (!Enum.IsDefined(typeof(FolderType), intValue))
            {
                Logger.Warn("Invalid root folder type '{0}'. Defaulting to {1}.", originalValue, FolderType.Mixed);
                return FolderType.Mixed;
            }

            return (FolderType)intValue;
        }
    }
}
