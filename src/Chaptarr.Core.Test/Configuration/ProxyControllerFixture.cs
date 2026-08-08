using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Chaptarr.Api.V1.Configuration;
using NzbDrone.Common.Http.Proxy;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Configuration;
using NUnit.Framework;

namespace Chaptarr.Core.Test.Configuration
{
    [TestFixture]
    public class ProxyControllerFixture
    {
        private sealed class FakeProxyService : IProxyService
        {
            private readonly Dictionary<int, ProxyDefinition> _proxies;

            public ProxyDefinition LastUpdated { get; private set; }
            public bool AddCalled { get; private set; }

            public FakeProxyService(params ProxyDefinition[] proxies)
            {
                _proxies = proxies.ToDictionary(p => p.Id);
            }

            public List<ProxyDefinition> All()
            {
                return _proxies.Values.ToList();
            }

            public ProxyDefinition Get(int id)
            {
                return _proxies[id];
            }

            public ProxyDefinition Find(int id)
            {
                return _proxies.TryGetValue(id, out var proxy) ? proxy : null;
            }

            public ProxyDefinition Add(ProxyDefinition proxy)
            {
                AddCalled = true;
                proxy.Id = _proxies.Any() ? _proxies.Keys.Max() + 1 : 1;
                _proxies[proxy.Id] = proxy;
                return proxy;
            }

            public ProxyDefinition Update(ProxyDefinition proxy)
            {
                LastUpdated = proxy;
                _proxies[proxy.Id] = proxy;
                return proxy;
            }

            public void Delete(int id)
            {
                throw new System.NotImplementedException();
            }
        }

        private sealed class FakeProxyTestService : IProxyTestService
        {
            public bool TestCalled { get; private set; }

            public Task<ProxyTestResult> TestProxy(string hostname, int port, ProxyType proxyType, string username = null, string password = null)
            {
                TestCalled = true;
                throw new System.NotImplementedException();
            }
        }

        [Test]
        public void get_all_should_not_serialize_proxy_password()
        {
            var proxy = new ProxyDefinition
            {
                Id = 1,
                Name = "Test Proxy",
                ProxyType = ProxyType.Http,
                Hostname = "example.invalid",
                Port = 8080,
                Username = "user",
                Password = "supersecret"
            };

            var controller = new ProxyController(new FakeProxyService(proxy), new FakeProxyTestService());

            var resources = controller.GetAll();
            Assert.That(resources, Has.Count.EqualTo(1));
            Assert.That(resources.Single().Password, Is.EqualTo(string.Empty));

            var json = STJson.ToJson(resources);
            Assert.That(json, Does.Not.Contain("supersecret"));
            Assert.That(json, Does.Not.Contain("bypassLocalAddresses"));
            Assert.That(json, Does.Not.Contain("bypassFilter"));
        }

        [Test]
        public void update_should_preserve_password_when_blank()
        {
            var proxy = new ProxyDefinition
            {
                Id = 1,
                Name = "Test Proxy",
                ProxyType = ProxyType.Http,
                Hostname = "example.invalid",
                Port = 8080,
                Username = "user",
                Password = "supersecret"
            };

            var proxyService = new FakeProxyService(proxy);
            var controller = new ProxyController(proxyService, new FakeProxyTestService());

            controller.Update(1, new ProxyResource
            {
                Id = 1,
                Name = "Test Proxy Updated",
                Type = ProxyType.Http,
                Hostname = "example.invalid",
                Port = 8080,
                Username = "user",
                Password = string.Empty
            });

            Assert.That(proxyService.LastUpdated, Is.Not.Null);
            Assert.That(proxyService.LastUpdated.Password, Is.EqualTo("supersecret"));
        }

        [Test]
        public void create_should_reject_invalid_proxy_type()
        {
            var proxyService = new FakeProxyService();
            var controller = new ProxyController(proxyService, new FakeProxyTestService());

            var exception = Assert.Throws<Chaptarr.Http.REST.BadRequestException>(() => controller.Create(new ProxyResource
            {
                Name = "Bad Proxy",
                Type = (ProxyType)99,
                Hostname = "example.invalid",
                Port = 8080
            }));

            Assert.That(exception.Content.ToString(), Does.Contain("Proxy type"));
            Assert.That(proxyService.AddCalled, Is.False);
        }

        [Test]
        public void test_should_reject_invalid_proxy_type()
        {
            var proxyTestService = new FakeProxyTestService();
            var controller = new ProxyController(new FakeProxyService(), proxyTestService);

            var exception = Assert.ThrowsAsync<Chaptarr.Http.REST.BadRequestException>(async () => await controller.Test(new ProxyResource
            {
                Name = "Bad Proxy",
                Type = (ProxyType)99,
                Hostname = "example.invalid",
                Port = 8080
            }));

            Assert.That(exception.Content.ToString(), Does.Contain("Proxy type"));
            Assert.That(proxyTestService.TestCalled, Is.False);
        }
    }
}
