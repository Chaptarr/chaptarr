using System;
using System.Data;
using Dapper;

namespace NzbDrone.Core.Datastore
{
    // Ensures all DateTimes are stored and read as UTC with Microsoft.Data.Sqlite via Dapper
    public class SqliteUtcDateTimeHandler : SqlMapper.ITypeHandler
    {
        public void SetValue(IDbDataParameter parameter, object value)
        {
            if (value is DateTime dt)
            {
                if (dt.Kind != DateTimeKind.Utc)
                {
                    dt = dt.ToUniversalTime();
                    dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
                }
                parameter.Value = dt;
                parameter.DbType = DbType.DateTime;
            }
            else
            {
                parameter.Value = value ?? DBNull.Value;
            }
        }

        public object Parse(Type destinationType, object value)
        {
            if (value is DateTime dt)
            {
                if (dt.Kind != DateTimeKind.Utc)
                {
                    dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
                }
                return dt;
            }

            if (value is string s && DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
            {
                return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            }

            return value;
        }
    }

    public class SqliteNullableUtcDateTimeHandler : SqlMapper.ITypeHandler
    {
        private readonly SqliteUtcDateTimeHandler _inner = new SqliteUtcDateTimeHandler();

        public void SetValue(IDbDataParameter parameter, object value)
        {
            if (value == null)
            {
                parameter.Value = DBNull.Value;
                return;
            }
            _inner.SetValue(parameter, value);
        }

        public object Parse(Type destinationType, object value)
        {
            if (value == null || value is DBNull) return null;
            return _inner.Parse(typeof(DateTime), value);
        }
    }
}

