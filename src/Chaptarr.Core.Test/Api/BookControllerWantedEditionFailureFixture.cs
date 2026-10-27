using System;
using Chaptarr.Api.V1.Books;
using Chaptarr.Core.Test.Common;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Mvc;
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
        public void wanted_edition_refusal_preserves_bad_request_status_and_original_message(string errorKind)
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
                Assert.That(method.Name, Is.EqualTo(nameof(IBookService.AddWantedEdition)));
                Assert.That(args, Is.EqualTo(new object[] { 7, 11 }));
                throw failure;
            });
            var controller = new BookController(null, books, null, null, null, null, null, null, null, null,
                null, null, null, null, new QualityProfileExistsValidator(new TestQualityProfileService()),
                new MetadataProfileExistsValidator(new TestMetadataProfileService()), LogManager.GetCurrentClassLogger());
            var result = controller.AddWantedEdition(7, new AddWantedEditionRequest { EditionId = 11, SearchForNewBook = true });
            Assert.That(result.Result, Is.InstanceOf<BadRequestObjectResult>());
            var rejected = (BadRequestObjectResult)result.Result;
            Assert.That(rejected.StatusCode, Is.EqualTo(400));
            Assert.That(rejected.Value, Is.TypeOf<string>().And.EqualTo(reason));
        }
    }
}
