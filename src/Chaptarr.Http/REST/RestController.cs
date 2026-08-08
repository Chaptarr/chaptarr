using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Chaptarr.Http.Validation;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using NzbDrone.Core.Datastore;
using NzbDrone.Http.REST.Attributes;

namespace Chaptarr.Http.REST
{
    public abstract class RestController<TResource> : Controller
        where TResource : RestResource, new()
    {
        private static readonly List<Type> VALIDATE_ID_ATTRIBUTES = new List<Type> { typeof(RestPutByIdAttribute), typeof(RestDeleteByIdAttribute) };

        protected ResourceValidator<TResource> PostValidator { get; private set; }
        protected ResourceValidator<TResource> PutValidator { get; private set; }
        protected ResourceValidator<TResource> SharedValidator { get; private set; }

        protected void ValidateId(int id)
        {
            if (id <= 0)
            {
                throw new BadRequestException(id + " is not a valid ID");
            }
        }

        protected RestController()
        {
            PostValidator = new ResourceValidator<TResource>();
            PutValidator = new ResourceValidator<TResource>();
            SharedValidator = new ResourceValidator<TResource>();

            PutValidator.RuleFor(r => r.Id).ValidId();
        }

        [RestGetById]
        [Produces("application/json")]
        public virtual ActionResult<TResource> GetResourceByIdWithErrorHandler(int id)
        {
            try
            {
                return GetResourceById(id);
            }
            catch (ModelNotFoundException)
            {
                return NotFound();
            }
        }

        protected abstract TResource GetResourceById(int id);

        public override void OnActionExecuting(ActionExecutingContext context)
        {
            var descriptor = context.ActionDescriptor as ControllerActionDescriptor;

            var skipAttribute = (SkipValidationAttribute)Attribute.GetCustomAttribute(descriptor.MethodInfo, typeof(SkipValidationAttribute), true);
            var skipValidate = skipAttribute?.Skip ?? false;
            var skipShared = skipAttribute?.SkipShared ?? false;

            var attributes = descriptor.MethodInfo.CustomAttributes as IReadOnlyCollection<CustomAttributeData> ??
                             descriptor.MethodInfo.CustomAttributes.ToArray();
            var validateId = attributes.Any(x => VALIDATE_ID_ATTRIBUTES.Contains(x.AttributeType));

            if (Request.Method == "POST" || Request.Method == "PUT")
            {
                var resourceArgs = context.ActionArguments.Values
                    .SelectMany(x => x switch
                    {
                        TResource single => new[] { single },
                        IEnumerable<TResource> multiple => multiple,
                        _ => Enumerable.Empty<TResource>()
                    });

                foreach (var resource in resourceArgs)
                {
                    if (Request.Method == "PUT" && resource != null && context.RouteData.Values.TryGetValue("id", out var routeIdObj) && routeIdObj != null)
                    {
                        var routeIdString = routeIdObj.ToString();
                        if (int.TryParse(routeIdString, out var routeId) && routeId > 0)
                        {
                            // Map route Id to body resource if not set in request
                            if (resource.Id == 0)
                            {
                                resource.Id = routeId;
                            }
                            else if (resource.Id != routeId)
                            {
                                throw new BadRequestException("Route id does not match resource id");
                            }
                        }
                    }

                    ValidateResource(resource, skipValidate, skipShared);
                }
            }

            if (validateId && !skipValidate)
            {
                if (context.ActionArguments.TryGetValue("id", out var idObj))
                {
                    ValidateId((int)idObj);
                }
            }

            base.OnActionExecuting(context);
        }

        protected void ValidateResource(TResource resource, bool skipValidate = false, bool skipSharedValidate = false)
        {
            if (resource == null)
            {
                throw new BadRequestException("Request body can't be empty");
            }

            var errors = new List<ValidationFailure>();

            if (!skipSharedValidate)
            {
                errors.AddRange(SharedValidator.Validate(resource).Errors);
            }

            if (Request.Method.Equals("POST", StringComparison.InvariantCultureIgnoreCase) && !skipValidate && !Request.Path.ToString().EndsWith("/test", StringComparison.InvariantCultureIgnoreCase))
            {
                errors.AddRange(PostValidator.Validate(resource).Errors);
            }
            else if (Request.Method.Equals("PUT", StringComparison.InvariantCultureIgnoreCase))
            {
                errors.AddRange(PutValidator.Validate(resource).Errors);
            }

            if (errors.Any())
            {
                throw new ValidationException(errors);
            }
        }

        protected ActionResult<TResource> Accepted(int id)
        {
            var result = GetResourceById(id);
            return AcceptedAtAction(nameof(GetResourceByIdWithErrorHandler), new { id = id }, result);
        }

        protected ActionResult<TResource> Created(int id)
        {
            var result = GetResourceById(id);
            return CreatedAtAction(nameof(GetResourceByIdWithErrorHandler), new { id = id }, result);
        }
    }
}
