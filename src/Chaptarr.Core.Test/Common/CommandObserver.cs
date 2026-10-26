using System;
using System.Data;
using System.Reflection;

namespace Chaptarr.Core.Test.Common
{
    public class CommandObserver : DispatchProxy
    {
        public object Target { get; set; }
        public Action<IDbCommand> Observe { get; set; }
        public bool IncludeWrites { get; set; }

        public static T Wrap<T>(T target, Action<IDbCommand> observe, bool includeWrites = false)
            where T : class
        {
            var proxy = Create<T, CommandObserver>();
            var state = (CommandObserver)(object)proxy;
            state.Target = target;
            state.Observe = observe;
            state.IncludeWrites = includeWrites;
            return proxy;
        }

        protected override object Invoke(MethodInfo method, object[] args)
        {
            if (Target is IDbCommand command && (method.Name == nameof(IDbCommand.ExecuteReader) ||
                (IncludeWrites && method.Name is nameof(IDbCommand.ExecuteNonQuery) or nameof(IDbCommand.ExecuteScalar))))
            {
                Observe(command);
            }

            var result = method.Invoke(Target, args);
            return result is IDbCommand created ? Wrap(created, Observe, IncludeWrites) : result;
        }
    }
}
