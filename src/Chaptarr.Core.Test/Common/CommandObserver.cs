using System;
using System.Data;
using System.Reflection;

namespace Chaptarr.Core.Test.Common
{
    public class CommandObserver : DispatchProxy
    {
        public object Target { get; set; }
        public Action<IDbCommand> Observe { get; set; }

        public static T Wrap<T>(T target, Action<IDbCommand> observe)
            where T : class
        {
            var proxy = Create<T, CommandObserver>();
            var state = (CommandObserver)(object)proxy;
            state.Target = target;
            state.Observe = observe;
            return proxy;
        }

        protected override object Invoke(MethodInfo method, object[] args)
        {
            if (Target is IDbCommand command && method.Name == nameof(IDbCommand.ExecuteReader))
            {
                Observe(command);
            }

            var result = method.Invoke(Target, args);
            return result is IDbCommand created ? Wrap(created, Observe) : result;
        }
    }
}
