using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Chaptarr.Http;
using Chaptarr.Http.REST;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Configuration;
using NzbDrone.Common.Http.Proxy;

namespace Chaptarr.Api.V1.Configuration
{
    [V1ApiController("settings/proxy")]
    public class ProxyController : RestController<ProxyResource>
    {
        private readonly IProxyService _proxyService;
        private readonly IProxyTestService _proxyTestService;

        public ProxyController(IProxyService proxyService, IProxyTestService proxyTestService)
        {
            _proxyService = proxyService;
            _proxyTestService = proxyTestService;

            SharedValidator.RuleFor(x => x.Name).NotEmpty();
            SharedValidator.RuleFor(x => x.Hostname).NotEmpty();
            SharedValidator.RuleFor(x => x.Port).InclusiveBetween(1, 65535);
            SharedValidator.RuleFor(x => x.Type)
                .Must(x => Enum.IsDefined(typeof(ProxyType), x))
                .WithMessage("Proxy type must be Http, Socks4, or Socks5");
        }

        protected override ProxyResource GetResourceById(int id)
        {
            var proxy = _proxyService.Get(id);
            return MapToResource(proxy);
        }

        [HttpGet]
        public List<ProxyResource> GetAll()
        {
            return _proxyService.All().Select(MapToResource).ToList();
        }

        [HttpPost]
        public ActionResult<ProxyResource> Create([FromBody] ProxyResource resource)
        {
            var model = MapToModel(resource);
            var proxy = _proxyService.Add(model);
            return Created(proxy.Id);
        }

        [HttpPut("{id:int}")]
        public ActionResult<ProxyResource> Update(int id, [FromBody] ProxyResource resource)
        {
            var model = MapToModel(resource);
            model.Id = id;

            if (string.IsNullOrWhiteSpace(resource.Password))
            {
                var existingProxy = _proxyService.Get(id);
                model.Password = existingProxy.Password;
            }

            _proxyService.Update(model);
            return Accepted(id);
        }

        [HttpDelete("{id:int}")]
        public void Delete(int id)
        {
            _proxyService.Delete(id);
        }

        [HttpPost("test")]
        public async Task<IActionResult> Test([FromBody] ProxyResource resource)
        {
            EnsureValidProxyType(resource.Type);

            var result = await _proxyTestService.TestProxy(
                resource.Hostname,
                resource.Port,
                resource.Type,
                resource.Username,
                resource.Password);

            if (result.IsValid)
            {
                return Ok(new
                {
                    isValid = true,
                    message = result.Message,
                    responseTime = result.ResponseTime?.TotalMilliseconds
                });
            }
            else
            {
                return Ok(new
                {
                    isValid = false,
                    message = result.Message
                });
            }
        }

        private ProxyResource MapToResource(ProxyDefinition proxy)
        {
            if (proxy == null)
            {
                return null;
            }

            return new ProxyResource
            {
                Id = proxy.Id,
                Name = proxy.Name,
                Type = proxy.ProxyType,
                Hostname = proxy.Hostname,
                Port = proxy.Port,
                Username = proxy.Username,
                Password = string.Empty
            };
        }

        private ProxyDefinition MapToModel(ProxyResource resource)
        {
            EnsureValidProxyType(resource.Type);

            return new ProxyDefinition
            {
                Name = resource.Name,
                ProxyType = resource.Type,
                Hostname = resource.Hostname,
                Port = resource.Port,
                Username = resource.Username,
                Password = resource.Password
            };
        }

        private static void EnsureValidProxyType(ProxyType type)
        {
            if (!Enum.IsDefined(typeof(ProxyType), type))
            {
                throw new BadRequestException("Proxy type must be Http, Socks4, or Socks5");
            }
        }
    }
}
