using System;
using System.Data;
using System.Globalization;
using Dapper;

namespace NzbDrone.Core.Datastore
{
    public class SqliteDecimalHandler : SqlMapper.ITypeHandler
    {
        public void SetValue(IDbDataParameter parameter, object value)
        {
            // Keep the provider's existing decimal binding and SQLite NUMERIC affinity behavior.
            parameter.Value = value ?? (object)DBNull.Value;
        }

        public object Parse(Type destinationType, object value)
        {
            switch (value)
            {
                case decimal decimalValue:
                    return decimalValue;
                case long longValue:
                    return (decimal)longValue;
                case int intValue:
                    return (decimal)intValue;
                case double doubleValue:
                    return (decimal)doubleValue;
                case float floatValue:
                    return (decimal)floatValue;
                case string stringValue when decimal.TryParse(stringValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed):
                    return parsed;
                default:
                    return Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            }
        }
    }

    public class SqliteNullableDecimalHandler : SqlMapper.ITypeHandler
    {
        private readonly SqliteDecimalHandler _inner = new SqliteDecimalHandler();

        public void SetValue(IDbDataParameter parameter, object value)
        {
            _inner.SetValue(parameter, value);
        }

        public object Parse(Type destinationType, object value)
        {
            if (value == null || value is DBNull)
            {
                return null;
            }

            return _inner.Parse(typeof(decimal), value);
        }
    }
}
