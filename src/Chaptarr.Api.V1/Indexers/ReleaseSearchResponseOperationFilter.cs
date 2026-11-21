using System.Collections.Generic;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Chaptarr.Api.V1.Indexers
{
    public sealed class ReleaseSearchResponseOperationFilter : IOperationFilter
    {
        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            operation.Responses["200"].Content["application/json"] = new OpenApiMediaType
            {
                Schema = new OpenApiSchema
                {
                    OneOf = new List<OpenApiSchema>
                    {
                        context.SchemaGenerator.GenerateSchema(typeof(List<ReleaseResource>), context.SchemaRepository),
                        context.SchemaGenerator.GenerateSchema(typeof(ReleaseSearchResponse), context.SchemaRepository),
                        context.SchemaGenerator.GenerateSchema(typeof(AuthorReleaseSearchResponse), context.SchemaRepository)
                    }
                }
            };
        }
    }
}
