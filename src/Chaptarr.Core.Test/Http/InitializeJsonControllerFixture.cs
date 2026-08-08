using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NUnit.Framework;
using Chaptarr.Http.Frontend;
using NzbDrone.Core.Configuration;

namespace Chaptarr.Core.Test.Http
{
    [TestFixture]
    public class InitializeJsonControllerFixture
    {
        private class ConfigFileProviderProxy : DispatchProxy
        {
            public Dictionary<string, object> Values { get; } = new(StringComparer.Ordinal);

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IConfigFileProvider.GetConfigDictionary))
                {
                    return new Dictionary<string, object>(Values, StringComparer.Ordinal);
                }

                if (targetMethod?.Name == nameof(IConfigFileProvider.SaveConfigDictionary) ||
                    targetMethod?.Name == nameof(IConfigFileProvider.EnsureDefaultConfigFile))
                {
                    return null;
                }

                if (targetMethod?.Name != null && targetMethod.Name.StartsWith("get_", StringComparison.Ordinal))
                {
                    var propertyName = targetMethod.Name.Substring(4);
                    if (Values.TryGetValue(propertyName, out var value))
                    {
                        return value;
                    }
                }

                throw new NotImplementedException($"Test proxy does not implement {targetMethod?.Name}");
            }

            public static IConfigFileProvider Create(string urlBase, string apiKey, string instanceName, string theme, string branch)
            {
                var proxy = DispatchProxy.Create<IConfigFileProvider, ConfigFileProviderProxy>();
                var state = (ConfigFileProviderProxy)(object)proxy;
                state.Values[nameof(IConfigFileProvider.UrlBase)] = urlBase;
                state.Values[nameof(IConfigFileProvider.ApiKey)] = apiKey;
                state.Values[nameof(IConfigFileProvider.InstanceName)] = instanceName;
                state.Values[nameof(IConfigFileProvider.Theme)] = theme;
                state.Values[nameof(IConfigFileProvider.Branch)] = branch;
                return proxy;
            }
        }

        [Test]
        public void should_disable_cache_and_serialize_expected_initialize_shape()
        {
            var controller = new InitializeJsonController(
                ConfigFileProviderProxy.Create("/chaptarr", "api-key", "Library", "dark", "DEVELOP"))
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext()
                }
            };

            var result = controller.Index() as JsonResult;

            Assert.That(result, Is.Not.Null);
            Assert.That(controller.Response.Headers["Cache-Control"].ToString(), Is.EqualTo("no-cache, no-store, must-revalidate"));
            Assert.That(controller.Response.Headers["Pragma"].ToString(), Is.EqualTo("no-cache"));
            Assert.That(controller.Response.Headers["Expires"].ToString(), Is.EqualTo("0"));

            using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
            var root = json.RootElement;

            Assert.That(root.GetProperty("ApiRoot").GetString(), Is.EqualTo("/chaptarr/api/v1"));
            Assert.That(root.GetProperty("ApiKey").GetString(), Is.EqualTo("api-key"));
            Assert.That(root.GetProperty("InstanceName").GetString(), Is.EqualTo("Library"));
            Assert.That(root.GetProperty("Theme").GetString(), Is.EqualTo("dark"));
            Assert.That(root.GetProperty("Branch").GetString(), Is.EqualTo("develop"));
            Assert.That(root.GetProperty("UrlBase").GetString(), Is.EqualTo("/chaptarr"));
            Assert.That(root.GetProperty("UserHash").GetString(), Is.Not.Empty);

            var buildTime = root.GetProperty("BuildTime").GetString();
            Assert.That(DateTime.TryParseExact(
                buildTime,
                "yyyy-MM-dd'T'HH:mm:ss'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out _), Is.True, "BuildTime should stay invariant ISO-8601 UTC");
        }
    }
}
