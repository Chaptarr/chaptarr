using System;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.Routing;

namespace Chaptarr.Http
{
    public class VersionedApiControllerAttribute : Attribute, IRouteTemplateProvider, IApiBehaviorMetadata
    {
        public const string API_CORS_POLICY = "ApiCorsPolicy";
        public const string CONTROLLER_RESOURCE = "[controller]";

        public VersionedApiControllerAttribute(int version, string resource = CONTROLLER_RESOURCE)
        {
            Resource = resource;
            Template = $"api/v{version}/{resource}";
        }

        public string Resource { get; }
        public string Template { get; }
        public int? Order => 2;
        public string Name { get; set; }
    }

    public class V1ApiControllerAttribute : VersionedApiControllerAttribute
    {
        public V1ApiControllerAttribute(string resource = "[controller]")
            : base(1, resource)
        {
        }
    }

    public class V5ApiControllerAttribute : VersionedApiControllerAttribute
    {
        public V5ApiControllerAttribute(string resource = "[controller]")
            : base(5, resource)
        {
        }
    }
}
