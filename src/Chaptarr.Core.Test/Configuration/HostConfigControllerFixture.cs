using System;
using System.Reflection;
using System.Threading.Tasks;
using Chaptarr.Api.V1.Config;
using Chaptarr.Http.REST;
using Chaptarr.Http.Middleware;
using Microsoft.AspNetCore.Http;
using Moq;
using FluentValidation;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Http.Proxy;
using NzbDrone.Core.Authentication;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Validation.Paths;

namespace Chaptarr.Core.Test.Configuration
{
    [TestFixture]
    public class HostConfigControllerFixture
    {
        private class ThrowingProxy<T> : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"Test proxy does not implement {typeof(T).Name}.{targetMethod?.Name}");
            }
        }

        private sealed class StubProxyTestService : IProxyTestService
        {
            public bool WasCalled { get; private set; }

            public Task<ProxyTestResult> TestProxy(string hostname, int port, ProxyType proxyType, string username = null, string password = null)
            {
                WasCalled = true;
                return Task.FromResult(new ProxyTestResult { IsValid = true, Message = "OK" });
            }
        }

        [TestCase(true, "Readarr", true)]
        [TestCase(true, "Readarr Audiobooks", true)]
        [TestCase(false, "Readarr", false)]
        [TestCase(false, "Chaptarr Audiobooks", true)]
        [TestCase(true, "Unrelated", false)]
        public void should_validate_source_names_in_legacy_settings_roundtrip(bool legacy, string name, bool expected)
        {
            var controller = CreateController();
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            controller.HttpContext.Items[ReadarrFacadeContext.ItemKey] = new ReadarrFacadeContext("hc", null, "/api", isBookshelfCompatibility: legacy);
            var validator = (IValidator<HostConfigResource>)typeof(RestController<HostConfigResource>)
                .GetProperty("SharedValidator", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller);
            var resource = CreateResource();
            resource.InstanceName = name;
            var nameErrors = validator.Validate(resource).Errors.Where(error => error.PropertyName == nameof(HostConfigResource.InstanceName)).ToList();
            Assert.That(nameErrors.Count == 0, Is.EqualTo(expected));
        }

        [TestCase(true, "Readarr Audiobooks")]
        [TestCase(false, "Chaptarr")]
        public void host_config_should_preserve_route_identity_and_hide_stored_password(bool legacy, string expected)
        {
            var config = new Mock<IConfigFileProvider>();
            config.SetupGet(value => value.InstanceName).Returns("Chaptarr");
            config.Setup(value => value.GetBookshelfInstanceName()).Returns("Readarr Audiobooks");
            var account = new User { Username = "fixture", Password = "fixture-stored-hash", Salt = "fixture-salt", Iterations = 10000 };
            var users = new Mock<IUserService>();
            users.Setup(value => value.FindUser()).Returns(account);
            var controller = new HostConfigController(config.Object, Mock.Of<IConfigService>(), users.Object,
                new StubProxyTestService(), Mock.Of<IProxyService>(), new FileExistsValidator(Mock.Of<IDiskProvider>()));
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            controller.HttpContext.Items[ReadarrFacadeContext.ItemKey] = new ReadarrFacadeContext("hc", null, "/api", isBookshelfCompatibility: legacy);
            var result = controller.GetHostConfig();
            Assert.That(result.InstanceName, Is.EqualTo(expected));
            Assert.That(result.Username, Is.EqualTo("fixture"));
            Assert.That(result.Password, Is.Empty);
            Assert.That(result.PasswordConfirmation, Is.Empty);
            Assert.That(account.Password, Is.EqualTo("fixture-stored-hash"));
            users.Verify(value => value.Upsert(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Test]
        public void save_should_reject_invalid_proxy_mode_before_persisting()
        {
            var controller = CreateController();
            var resource = CreateResource();
            resource.ProxyMode = (ProxyMode)99;

            var exception = Assert.ThrowsAsync<BadRequestException>(async () => await controller.SaveHostConfig(resource));

            Assert.That(exception.Content.ToString(), Does.Contain("Proxy mode"));
        }

        [Test]
        public void save_should_reject_invalid_proxy_type_before_persisting()
        {
            var controller = CreateController();
            var resource = CreateResource();
            resource.ProxyType = (ProxyType)99;

            var exception = Assert.ThrowsAsync<BadRequestException>(async () => await controller.SaveHostConfig(resource));

            Assert.That(exception.Content.ToString(), Does.Contain("Proxy type"));
        }

        [Test]
        public async Task test_proxy_should_reject_invalid_proxy_type_before_testing()
        {
            var proxyTestService = new StubProxyTestService();
            var controller = CreateController(proxyTestService);

            var response = await controller.TestProxy(new ProxyTestRequest
            {
                ProxyHostname = "example.invalid",
                ProxyPort = 8080,
                ProxyType = (ProxyType)99
            });

            Assert.That(proxyTestService.WasCalled, Is.False);
            Assert.That(response.Result, Is.TypeOf<BadRequestObjectResult>());

            var badRequest = (BadRequestObjectResult)response.Result;
            var result = (ProxyTestResult)badRequest.Value;
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Message, Does.Contain("Proxy type"));
        }

        [Test]
        public void selecting_a_different_proxy_should_not_reapply_unchanged_legacy_fields()
        {
            var current = CreateResource();
            current.GlobalProxyId = 1;
            current.ProxyHostname = "legacy.example";
            current.ProxyPort = 8080;
            current.ProxyUsername = "user";
            current.ProxyPassword = "secret";

            var submitted = CreateResource();
            submitted.GlobalProxyId = 2;
            submitted.ProxyHostname = current.ProxyHostname;
            submitted.ProxyPort = current.ProxyPort;
            submitted.ProxyUsername = current.ProxyUsername;
            submitted.ProxyPassword = current.ProxyPassword;

            Assert.That(HostConfigController.LegacyProxyFieldsChanged(submitted, current), Is.False);
        }

        [Test]
        public void changed_legacy_proxy_fields_should_still_support_quickstart_configuration()
        {
            var current = CreateResource();
            var submitted = CreateResource();
            submitted.ProxyHostname = "proxy.example";
            submitted.ProxyPort = 8080;

            Assert.That(HostConfigController.LegacyProxyFieldsChanged(submitted, current), Is.True);
        }

        private static HostConfigController CreateController(IProxyTestService proxyTestService = null)
        {
            return new HostConfigController(
                Throwing<IConfigFileProvider>(),
                Throwing<IConfigService>(),
                Throwing<IUserService>(),
                proxyTestService ?? new StubProxyTestService(),
                Throwing<IProxyService>(),
                new FileExistsValidator(Throwing<IDiskProvider>()));
        }

        private static HostConfigResource CreateResource()
        {
            return new HostConfigResource
            {
                BindAddress = "*",
                Port = 8787,
                SslPort = 6868,
                AuthenticationMethod = AuthenticationType.None,
                ProxyMode = ProxyMode.Disabled,
                ProxyType = ProxyType.Http,
                Branch = "main",
                BackupFolder = string.Empty,
                BackupInterval = 7,
                BackupRetention = 28
            };
        }

        private static T Throwing<T>()
            where T : class
        {
            return DispatchProxy.Create<T, ThrowingProxy<T>>();
        }
    }
}
