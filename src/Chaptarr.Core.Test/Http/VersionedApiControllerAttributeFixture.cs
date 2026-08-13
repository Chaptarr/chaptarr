using Chaptarr.Http;
using Microsoft.AspNetCore.Cors.Infrastructure;
using NUnit.Framework;

namespace Chaptarr.Core.Test.Http
{
    [TestFixture]
    public class VersionedApiControllerAttributeFixture
    {
        private static void AssertApiCorsPolicy(object attribute)
        {
            Assert.That(attribute, Is.InstanceOf<IEnableCorsAttribute>());
            Assert.That(((IEnableCorsAttribute)attribute).PolicyName, Is.EqualTo(VersionedApiControllerAttribute.API_CORS_POLICY));
        }

        [Test]
        public void v1_api_should_enable_the_registered_cors_policy()
        {
            AssertApiCorsPolicy(new V1ApiControllerAttribute());
        }

        [Test]
        public void v5_api_should_enable_the_registered_cors_policy()
        {
            AssertApiCorsPolicy(new V5ApiControllerAttribute());
        }
    }
}
