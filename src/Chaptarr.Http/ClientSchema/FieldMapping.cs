using System;

namespace Chaptarr.Http.ClientSchema
{
    public class FieldMapping
    {
        public Field Field { get; set; }
        public Type PropertyType { get; set; }
        public bool IsSensitive { get; set; }
        public Func<object, object> GetterFunc { get; set; }
        public Action<object, object> SetterFunc { get; set; }
    }
}
