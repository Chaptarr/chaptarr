using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Chaptarr.Http.ErrorManagement;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using NLog;
using NUnit.Framework;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class ValidationErrorCodeFixture
    {
        [Test]
        public async Task validation_response_preserves_the_code_independently_of_the_message_language()
        {
            var context = new DefaultHttpContext();
            using var body = new MemoryStream();
            context.Response.Body = body;
            context.Features.Set<IExceptionHandlerPathFeature>(new ExceptionHandlerFeature
            {
                Error = new ValidationException(new[]
                {
                    new ValidationFailure("Path", "Zugriff verweigert") { ErrorCode = "RootFolderNotWritable" }
                })
            });
            await new ChaptarrErrorPipeline(LogManager.GetCurrentClassLogger()).HandleException(context);
            Assert.That(context.Response.StatusCode, Is.EqualTo(400));
            using var response = JsonDocument.Parse(body.ToArray());
            Assert.That(response.RootElement[0].GetProperty("errorCode").GetString(), Is.EqualTo("RootFolderNotWritable"));
            Assert.That(response.RootElement[0].GetProperty("errorMessage").GetString(), Is.EqualTo("Zugriff verweigert"));
        }
    }
}
