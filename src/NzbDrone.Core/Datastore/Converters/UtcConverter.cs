using System;
using System.Data;
using Dapper;

namespace NzbDrone.Core.Datastore.Converters
{
    public class DapperUtcConverter : SqlMapper.TypeHandler<DateTime>
    {
        public override void SetValue(IDbDataParameter parameter, DateTime value)
        {
            var utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
            parameter.Value = utc;
            parameter.DbType = DbType.DateTime;
        }

        public override DateTime Parse(object value)
        {
            if (value is DateTime dt)
            {
                return dt.Kind == DateTimeKind.Utc ? dt : DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            }
            if (value is string s)
            {
                if (DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
                {
                    return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
                }
                // Fallback: try exact without timezone as UTC
                if (DateTime.TryParse(s, out parsed))
                {
                    return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
                }
            }
            throw new InvalidCastException($"Unable to convert value of type '{value?.GetType()}' to DateTime");
        }
    }
}
