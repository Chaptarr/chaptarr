using System;
using System.Data;
using System.Globalization;
using Dapper;

namespace NzbDrone.Core.Datastore
{
    // A decimal column in sqlite does not hold one storage class. `Microsoft.Data.Sqlite`
    // binds decimal parameters as `TEXT`-typed, and sqlite's `NUMERI`C affinity subsequently
    // collapses the value to INTEGER when it is exactly integral and REAL when it is not.
    // That means values like `98.0` come back as `Int64`, but `42.5` comes back as `DOUBLE`
    // from the same column. `Microsoft.Data.Sqlite` reports the CLR type per row from the
    // value itself, but Dapper compiles a single deserializer from the first row it sees and
    // bakes that type in, so whichever class it happened to compile against throws on every
    // row of the other one. Parsing the value here makes the read independent of which class
    // Dapper saw first.
    public class SqliteDecimalHandler : SqlMapper.ITypeHandler
    {
        public void SetValue(IDbDataParameter parameter, object value)
        {
            // Deliberately left to the provider's own binding: changing the write path
            // would alter how existing columns are stored, and the read is the broken half.
            parameter.Value = value ?? (object)DBNull.Value;
        }

        public object Parse(Type destinationType, object value)
        {
            switch (value)
            {
                case decimal val:
                    return val;
                case long val:
                    return (decimal)val;
                case int val:
                    return (decimal)val;
                case double val:
                    return (decimal)val;
                case float val:
                    return (decimal)val;
                case string val when decimal.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed):
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
