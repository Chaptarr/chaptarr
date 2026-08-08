using System;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Chaptarr.Http.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using NzbDrone.Core.Authentication;

namespace Chaptarr.Core.Test.Http.Authentication
{
    [TestFixture]
    public class BasicAuthenticationHandlerFixture
    {
        private sealed class ThrowingAuthService : Chaptarr.Http.Authentication.IAuthenticationService
        {
            public void LogUnauthorized(HttpRequest context) => throw new NotImplementedException();
            public User Login(HttpRequest request, string username, string password) => throw new NotImplementedException();
            public void Logout(HttpContext context) => throw new NotImplementedException();
        }

        private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
        {
            private readonly T _value;

            public StaticOptionsMonitor(T value)
            {
                _value = value;
            }

            public T CurrentValue => _value;
            public T Get(string name) => _value;
            public IDisposable OnChange(Action<T, string> listener) => NullDisposable.Instance;

            private sealed class NullDisposable : IDisposable
            {
                public static readonly NullDisposable Instance = new();
                public void Dispose()
                {
                }
            }
        }

        private static async Task<BasicAuthenticationHandler> CreateHandler(HttpContext context)
        {
            var optionsMonitor = new StaticOptionsMonitor<AuthenticationSchemeOptions>(new AuthenticationSchemeOptions());
            var handler = new BasicAuthenticationHandler(new ThrowingAuthService(), optionsMonitor, LoggerFactory.Create(_ => { }), UrlEncoder.Default);
            var scheme = new AuthenticationScheme(AuthenticationType.Basic.ToString(), AuthenticationType.Basic.ToString(), typeof(BasicAuthenticationHandler));
            await handler.InitializeAsync(scheme, context);
            return handler;
        }

        [Test]
        public async Task should_return_no_result_when_authorization_header_missing()
        {
            var context = new DefaultHttpContext();
            var handler = await CreateHandler(context);

            var result = await handler.AuthenticateAsync();

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Failure, Is.Null);
        }

        [Test]
        public async Task should_return_no_result_when_authorization_header_is_not_basic()
        {
            var context = new DefaultHttpContext();
            context.Request.Headers.Authorization = "Bearer abc123";
            var handler = await CreateHandler(context);

            var result = await handler.AuthenticateAsync();

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Failure, Is.Null);
        }
    }
}
