using System;
using Chaptarr.Api.V1.Books;
using Chaptarr.Core.Test.Common;
using FluentValidation;
using FluentValidation.Results;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Chaptarr.Http.ErrorManagement;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Validation;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class BookControllerWantedEditionFailureFixture
    {
        [TestCase("validation")]
        [TestCase("argument")]
        [TestCase("operation")]
        public async Task wanted_edition_failures_use_the_standard_error_pipeline(string errorKind)
        {
            const string reason = "The requested edition cannot be selected without changing a protected selection.";
            Exception failure = errorKind switch
            {
                "validation" => new ValidationException(new[] { new ValidationFailure("Edition", reason) }),
                "argument" => new ArgumentException(reason),
                _ => new InvalidOperationException(reason)
            };
            var books = InterfaceProxy.Proxy<IBookService>((method, args) =>
            {
                Assert.That(method.Name, Is.EqualTo(nameof(IBookService.AddWantedEditionCopy)));
                Assert.That(args, Is.EqualTo(new object[] { 7, 11 }));
                throw failure;
            });
            var controller = new BookController(null, books, null, null, null, null, null, null, null, null,
                null, null, null, null, new QualityProfileExistsValidator(new TestQualityProfileService()),
                new MetadataProfileExistsValidator(new TestMetadataProfileService()), LogManager.GetCurrentClassLogger());
            var thrown = Assert.Throws(failure.GetType(), () => controller.AddWantedEdition(7,
                new AddWantedEditionRequest { EditionId = 11, SearchForNewBook = true }));
            Assert.That(thrown, Is.SameAs(failure));
            var context = new DefaultHttpContext();
            context.Response.Body = new MemoryStream();
            context.Features.Set<IExceptionHandlerPathFeature>(new ExceptionHandlerFeature { Error = thrown });
            await new ChaptarrErrorPipeline(LogManager.GetCurrentClassLogger()).HandleException(context);
            context.Response.Body.Position = 0;
            using var json = await JsonDocument.ParseAsync(context.Response.Body);
            Assert.That(context.Response.StatusCode, Is.EqualTo(errorKind == "validation" ? 400 : 500));
            var body = json.RootElement;
            Assert.That(errorKind == "validation" ? body[0].GetProperty("errorMessage").GetString() : body.GetProperty("message").GetString(), Is.EqualTo(reason));
        }

        [TestCase(0)]
        [TestCase(-1)]
        public void missing_edition_is_a_validation_failure(int editionId)
        {
            var controller = new BookController(null, null, null, null, null, null, null, null, null, null,
                null, null, null, null, new QualityProfileExistsValidator(new TestQualityProfileService()),
                new MetadataProfileExistsValidator(new TestMetadataProfileService()), LogManager.GetCurrentClassLogger());
            var error = Assert.Throws<ValidationException>(() => controller.AddWantedEdition(7,
                new AddWantedEditionRequest { EditionId = editionId }));
            Assert.That(error.Errors, Has.Exactly(1).Matches<ValidationFailure>(failure => failure.PropertyName == "EditionId" && failure.ErrorMessage == "EditionId is required"));
        }
    }
}
