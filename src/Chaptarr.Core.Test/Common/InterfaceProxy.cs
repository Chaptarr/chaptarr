using System;
using System.Reflection;

namespace Chaptarr.Core.Test.Common
{
    public class InterfaceProxy : DispatchProxy
    {
        public Func<MethodInfo, object[], object> Handler { get; set; }

        public static T Proxy<T>(Func<MethodInfo, object[], object> handler) where T : class
        {
            var proxy = Create<T, InterfaceProxy>();
            ((InterfaceProxy)(object)proxy).Handler = handler;
            return proxy;
        }

        protected override object Invoke(MethodInfo method, object[] args)
        {
            return Handler(method, args);
        }
    }
}
