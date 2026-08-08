using Microsoft.Data.Sqlite;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Chaptarr.Http.Exceptions;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using NLog;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Exceptions;

namespace Chaptarr.Http.ErrorManagement
{
    public class ChaptarrErrorPipeline
    {
        private readonly Logger _logger;

        public ChaptarrErrorPipeline(Logger logger)
        {
            _logger = logger;
        }

        public async Task HandleException(HttpContext context)
        {
            _logger.Trace("Handling Exception");

            var response = context.Response;
            var exceptionHandlerPathFeature = context.Features.Get<IExceptionHandlerPathFeature>();
            var exception = exceptionHandlerPathFeature?.Error;

            var statusCode = HttpStatusCode.InternalServerError;
            var errorModel = new ErrorModel
            {
                Message = exception?.Message,
                Description = null
            };

            if (exception is ApiException apiException)
            {
                _logger.Warn(apiException, "API Error:\n{0}", apiException.Message);

                errorModel = new ErrorModel(apiException);
                statusCode = apiException.StatusCode;
            }
            else if (exception is ValidationException validationException)
            {
                _logger.Warn("Invalid request {0}", validationException.Message);

                response.StatusCode = (int)HttpStatusCode.BadRequest;
                response.ContentType = "application/json";
                var errors = validationException.Errors
                    .Select(e => new
                    {
                        propertyName = e.PropertyName,
                        errorMessage = e.ErrorMessage
                    })
                    .ToList();

                await response.WriteAsync(STJson.ToJson(errors));
                return;
            }
            else if (exception is NzbDroneClientException clientException)
            {
                _logger.Debug(clientException, "Client error during request {0} {1}: {2}", context.Request.Method, context.Request.Path, clientException.Message);
                statusCode = clientException.StatusCode;
            }
            else if (exception is ModelNotFoundException)
            {
                _logger.Debug(exception, "Model not found during request {0} {1}", context.Request.Method, context.Request.Path);
                statusCode = HttpStatusCode.NotFound;
            }
            else if (exception is ModelConflictException)
            {
                _logger.Warn(exception, "Model conflict during request {0} {1}", context.Request.Method, context.Request.Path);
                statusCode = HttpStatusCode.Conflict;
            }
            else if (exception is SqliteException sqLiteException)
            {
                if (context.Request.Method == "PUT" || context.Request.Method == "POST")
                {
                    // Unique/constraint violation → conflict
                    if (sqLiteException.SqliteErrorCode == 19 /*SQLITE_CONSTRAINT*/ || sqLiteException.SqliteExtendedErrorCode == 2067 /*SQLITE_CONSTRAINT_UNIQUE*/)
                    {
                        statusCode = HttpStatusCode.Conflict;
                    }
                }

                _logger.Error(sqLiteException, "[{0} {1}]", context.Request.Method, context.Request.Path);
            }
            else
            {
                _logger.Error(exception, "Unhandled exception occurred during request {0} {1}", context.Request.Method, context.Request.Path);
                _logger.Fatal(exception, "Request Failed. {0} {1}", context.Request.Method, context.Request.Path);
            }

            await errorModel.WriteToResponse(response, statusCode);
        }
    }
}
