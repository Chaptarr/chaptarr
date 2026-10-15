using System;
using System.Reflection;
using System.Collections.Generic;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using System.Threading.Tasks;
using Chaptarr.Api.V1.Config;
using Chaptarr.Http.REST;
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

        [TestCase(-1, false)]
        [TestCase(0, true)]
        [TestCase(7, true)]
        [TestCase(8, false)]
        public void api_validation_must_accept_schedule_off_and_reject_out_of_range_intervals(int interval, bool valid)
        {
            var controller = CreateController();
            var resource = CreateResource();
            resource.Id = 1;
            resource.BackupInterval = interval;
            if (valid) Assert.DoesNotThrow(() => ValidateRequest(controller, nameof(HostConfigController.SaveHostConfig), resource));
            else Assert.Throws<ValidationException>(() => ValidateRequest(controller, nameof(HostConfigController.SaveHostConfig), resource));
        }

        [TestCase(1, "RssSyncInterval", "0 to disable or between 10 and 120 minutes")]
        [TestCase(-1, "Retention", "greater than or equal to 0")]
        public void settings_api_must_use_shared_validation_and_specific_messages(int value, string property, string message)
        {
            var controller = new IndexerConfigController(Throwing<IConfigService>());
            var resource = new IndexerConfigResource { Id = 1, RssSyncInterval = 15 };
            typeof(IndexerConfigResource).GetProperty(property).SetValue(resource, value);
            var error = Assert.Throws<ValidationException>(() => ValidateRequest(controller, nameof(IndexerConfigController.SaveConfig), resource));
            Assert.That(error.Errors, Has.Some.Matches<FluentValidation.Results.ValidationFailure>(failure => failure.PropertyName == property && failure.ErrorMessage.Contains(message)));
        }

        private static void ValidateRequest(Controller controller, string actionName, object resource)
        {
            var http = new DefaultHttpContext();
            http.Request.Method = "PUT";
            var descriptor = new ControllerActionDescriptor { MethodInfo = controller.GetType().GetMethod(actionName) };
            var action = new ActionContext(http, new RouteData(), descriptor);
            controller.ControllerContext = new ControllerContext(action);
            controller.OnActionExecuting(new ActionExecutingContext(action, new List<IFilterMetadata>(), new Dictionary<string, object> { ["resource"] = resource }, controller));
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
                Branch = "master",
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
