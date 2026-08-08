using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using NUnit.Framework;
using Chaptarr.Http.Validation;

namespace Chaptarr.Core.Test.Http
{
    [TestFixture]
    public class DuplicateEndpointDetectorFixture
    {
        private sealed class StubEndpointDataSource : EndpointDataSource
        {
            private sealed class NeverChangedToken : IChangeToken
            {
                public bool HasChanged => false;
                public bool ActiveChangeCallbacks => false;

                public IDisposable RegisterChangeCallback(Action<object> callback, object state)
                {
                    return EmptyDisposable.Instance;
                }
            }

            private sealed class EmptyDisposable : IDisposable
            {
                public static readonly EmptyDisposable Instance = new();

                public void Dispose()
                {
                }
            }

            public override IReadOnlyList<Endpoint> Endpoints { get; } = Array.Empty<Endpoint>();

            public override IChangeToken GetChangeToken()
            {
                return new NeverChangedToken();
            }
        }

        [Test]
        public void should_return_empty_when_internal_matcher_builder_service_is_unavailable()
        {
            var services = new ServiceCollection().BuildServiceProvider();
            var detector = new DuplicateEndpointDetector(services);

            var duplicates = detector.GetDuplicateEndpoints(new StubEndpointDataSource());

            Assert.That(duplicates, Is.Empty);
        }
    }
}
