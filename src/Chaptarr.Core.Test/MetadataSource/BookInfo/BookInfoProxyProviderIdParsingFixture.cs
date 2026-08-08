using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using NzbDrone.Core.MetadataSource.BookInfo;
using NzbDrone.Core.MetadataSource.BookInfo.V5;

namespace Chaptarr.Core.Test.MetadataSource.BookInfo
{
    [TestFixture]
    public class BookInfoProxyProviderIdParsingFixture
    {
        [Test]
        public void should_read_first_provider_id_from_list_value()
        {
            var providerIds = new Dictionary<string, object>
            {
                ["gr"] = new List<string> { "gr:231260754", "gr:3046572" }
            };

            var result = TryGetV5ProviderId(providerIds, out var value, "gr");

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.True);
                Assert.That(value, Is.EqualTo("gr:231260754"));
            });
        }

        [Test]
        public void should_read_first_provider_id_from_json_array_value()
        {
            var providerIds = new Dictionary<string, object>
            {
                ["hc"] = new JArray("hc:383236", "hc:999999")
            };

            var result = TryGetV5ProviderId(providerIds, out var value, "hc");

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.True);
                Assert.That(value, Is.EqualTo("hc:383236"));
            });
        }

        [Test]
        public void should_ignore_unknown_provider_id_object_instead_of_stringifying_type_name()
        {
            var providerIds = new Dictionary<string, object>
            {
                ["gr"] = new PoisonProviderIds()
            };

            var result = TryGetV5ProviderId(providerIds, out var value, "gr");

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.False);
                Assert.That(value, Is.Null);
            });
        }

        [Test]
        public void v5_resource_provider_map_should_ignore_unknown_provider_id_object()
        {
            var work = new V5Book
            {
                LegacyProviderIdsCamel = new Dictionary<string, object>
                {
                    ["gr"] = new PoisonProviderIds()
                }
            };

            Assert.That(work.ProviderIds["gr"], Is.Empty);
        }

        private static bool TryGetV5ProviderId(Dictionary<string, object> providerIds, out string value, params string[] keys)
        {
            value = null;
            var method = typeof(BookInfoProxy).GetMethod("EnumerateProviderValues", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);

            foreach (var key in keys)
            {
                if (!providerIds.TryGetValue(key, out var raw))
                {
                    continue;
                }

                value = ((IEnumerable<string>)method.Invoke(null, new[] { raw })).FirstOrDefault();
                if (value != null)
                {
                    return true;
                }
            }

            return false;
        }

        private sealed class PoisonProviderIds
        {
            public override string ToString()
            {
                return "System.Collections.Generic.List`1[System.String]";
            }
        }
    }
}
