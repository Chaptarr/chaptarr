using System;
using System.Collections.Generic;
using Chaptarr.Api.V1.Config;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Newtonsoft.Json;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    [NonParallelizable]
    public class HardcoverConfigControllerFixture
    {
        private static readonly string[] EnvironmentKeys = { "HARDCOVER_AUTH", "HARDCOVER_API_KEY", "HARDCOVER", "HARDCOVER_NATIVE" };

        private readonly Dictionary<string, string> _previousEnvironment = new();

        [SetUp]
        public void ClearHardcoverEnvironment()
        {
            foreach (var key in EnvironmentKeys)
            {
                _previousEnvironment[key] = Environment.GetEnvironmentVariable(key);
                Environment.SetEnvironmentVariable(key, null);
            }
        }

        [TearDown]
        public void RestoreHardcoverEnvironment()
        {
            foreach (var pair in _previousEnvironment)
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
        }

        [Test]
        public void should_never_return_saved_hardcover_token()
        {
            const string token = "hardcover-secret";
            var config = new Mock<IConfigService>();
            config.SetupGet(value => value.HardcoverEnabled).Returns(true);
            config.SetupGet(value => value.HardcoverApiToken).Returns(token);
            config.SetupGet(value => value.HardcoverUsername).Returns("reader");
            config.SetupGet(value => value.HardcoverUserImageUrl).Returns("https://example.test/avatar.png");

            var controller = new HardcoverConfigController(config.Object, Mock.Of<IHttpClient>());
            var response = controller.GetConfig();
            var result = response.Result as OkObjectResult;

            Assert.That(result, Is.Not.Null);
            var resource = result.Value as HardcoverConfigResource;
            Assert.That(resource, Is.Not.Null);
            Assert.That(resource.HasToken, Is.True);
            Assert.That(resource.Username, Is.EqualTo("reader"));
            Assert.That(JsonConvert.SerializeObject(resource), Does.Not.Contain(token));
        }
    }
}
