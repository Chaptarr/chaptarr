using System;
using System.Collections.Generic;
using System.Reflection;

namespace Chaptarr.Core.Test.Datastore
{
    // Minimal hand-rolled stub: members return a configured value (by member name) or the type default,
    // and every call is recorded.
    public class InterfaceStub<T> : DispatchProxy
        where T : class
    {
        public Dictionary<string, object> Values { get; } = new Dictionary<string, object>();
        public Dictionary<string, Action<object[]>> Handlers { get; } = new Dictionary<string, Action<object[]>>();
        public List<(string Name, object[] Args)> Calls { get; } = new List<(string, object[])>();

        public static InterfaceStub<T> Create(out T instance)
        {
            instance = Create<T, InterfaceStub<T>>();
            return (InterfaceStub<T>)(object)instance;
        }

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            Calls.Add((targetMethod.Name, args));

            if (Handlers.TryGetValue(targetMethod.Name, out var handler))
            {
                try
                {
                    handler(args);
                }
                catch (TargetInvocationException)
                {
                    throw;
                }
            }

            var name = targetMethod.Name.StartsWith("get_", StringComparison.Ordinal) ? targetMethod.Name.Substring(4) : targetMethod.Name;

            if (Values.TryGetValue(name, out var value))
            {
                return value;
            }

            var rt = targetMethod.ReturnType;
            return rt.IsValueType && rt != typeof(void) ? Activator.CreateInstance(rt) : null;
        }
    }
}
