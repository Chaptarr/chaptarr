using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Profiles;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Common
{
    internal static class SettingsBackupTestValues
    {
        public static object DifferentValue(Type type, object original)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (type == typeof(string))
            {
                return original as string == "backup-coverage-value" ? "other-coverage-value" : "backup-coverage-value";
            }

            if (type == typeof(bool))
            {
                return original == null || !(bool)original;
            }

            if (type == typeof(int))
            {
                return original == null ? 41 : (int)original + 1;
            }

            if (type == typeof(double))
            {
                return original == null ? 1.5 : (double)original + 1;
            }

            if (type == typeof(DateTime))
            {
                return original == null ? new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc) : ((DateTime)original).AddMinutes(1);
            }

            if (type.IsEnum)
            {
                return Enum.GetValues(type).Cast<object>().First(value => !value.Equals(original));
            }

            if (type.IsGenericType && typeof(IEnumerable).IsAssignableFrom(type))
            {
                var elementType = type.GetGenericArguments().Single();
                var collectionType = type.IsInterface ? typeof(List<>).MakeGenericType(elementType) : type;
                var collection = Activator.CreateInstance(collectionType);
                collectionType.GetMethod("Add").Invoke(collection, new[] { DifferentValue(elementType, null) });
                return collection;
            }

            if (type == typeof(ProfileFormatItem))
            {
                return new ProfileFormatItem { Format = new CustomFormat { Id = 41, Name = "Coverage format" }, Score = 42 };
            }

            if (type == typeof(CustomFormat))
            {
                return new CustomFormat { Id = 41, Name = "Coverage format" };
            }

            if (type == typeof(Quality))
            {
                return Equals(original, Quality.MP3) ? Quality.M4B : Quality.MP3;
            }

            if (type == typeof(QualityProfileQualityItem))
            {
                return new QualityProfileQualityItem { Quality = Quality.MP3, Allowed = true };
            }

            if (type == typeof(ICustomFormatSpecification))
            {
                return new SizeSpecification { Name = "Coverage size", Min = 1, Max = 2 };
            }

            if (type.IsClass && type.GetConstructor(Type.EmptyTypes) != null)
            {
                var value = Activator.CreateInstance(type);
                foreach (var property in type.GetProperties()
                    .Where(property => property.GetMethod?.IsPublic == true && property.SetMethod?.IsPublic == true && property.GetIndexParameters().Length == 0))
                {
                    property.SetValue(value, DifferentValue(property.PropertyType, property.GetValue(value)));
                }

                return value;
            }

            throw new NotSupportedException($"Add a type-based value generator for {type}; do not add a per-setting whitelist");
        }
    }
}
