using System;
using System.Collections.Generic;
using System.Net;
using Chaptarr.Api.V1.Author;
using Chaptarr.Core.Test.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MediaFiles;
using Swashbuckle.AspNetCore.Swagger;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class AuthorDeletePreviewFixture
    {
        [TestCase(null, false)]
        [TestCase(null, true)]
        [TestCase("audiobook", false)]
        [TestCase("ebook", false)]
        public void preview_requires_the_same_explicit_scope_as_delete_and_never_mutates(string mediaType, bool both)
        {
            var author = new Author { Id = 1, Name = "Frank Herbert", AudiobookPath = "/library/audio/Herbert", EbookPath = "/library/ebooks/Herbert" };
            var reads = 0;
            var authors = InterfaceProxy.Proxy<IAuthorRepository>((method, args) =>
            {
                Assert.That(method.Name, Is.EqualTo(nameof(IAuthorRepository.Get)));
                Assert.That(args[0], Is.EquivalentTo(new[] { 1 }));
                return new List<Author> { author };
            });
            var books = InterfaceProxy.Proxy<IBookRepository>((method, _) =>
            {
                Assert.That(method.Name, Is.EqualTo(nameof(IBookRepository.GetBooksByAuthorId)));
                return new List<Book>();
            });
            var expected = new AuthorDeletionPreview();
            var deletion = InterfaceProxy.Proxy<IDeleteMediaFiles>((method, args) =>
            {
                Assert.That(method.Name, Is.EqualTo(nameof(IDeleteMediaFiles.PreviewAuthorFiles)));
                Assert.That(args[0], Is.EquivalentTo(new[] { author }));
                Assert.That(args[1], Is.EqualTo(both ? null : Enum.Parse<BookMediaType>(mediaType, true)));
                reads++;
                return expected;
            });
            var controller = new AuthorDeletePreviewController(authors, books, deletion)
            { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
            var request = new AuthorEditorResource { AuthorIds = new List<int> { 1, 1 }, MediaType = mediaType, ApplyToBothFormats = both };
            if (mediaType == null && !both)
            {
                var error = Assert.Throws<NzbDroneClientException>(() => controller.Preview(request));
                Assert.That(error.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
                Assert.That(reads, Is.Zero);
            }
            else
            {
                Assert.That(controller.Preview(request), Is.SameAs(expected));
                Assert.That(reads, Is.EqualTo(1));
            }
        }

        [TestCase(null)]
        [TestCase(new int[0])]
        [TestCase(new[] { 0 })]
        public void invalid_preview_selection_never_reaches_disk(int[] ids)
        {
            var controller = new AuthorDeletePreviewController(null, null, null);
            Assert.Throws<NzbDrone.Core.Exceptions.BadRequestException>(() => controller.Preview(ids == null ? null :
                new AuthorEditorResource { AuthorIds = new List<int>(ids) }));
        }

        [Test]
        public void preview_contract_exposes_the_inventory_and_retention_reasons()
        {
            using var services = OpenApiNullableReferenceFixture.CreateServices();
            var api = services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");
            Assert.That(api.Paths.ContainsKey("/api/v1/author/deletepreview"), Is.True);
            Assert.That(api.Components.Schemas[nameof(AuthorDeletionPreview)].Properties.Keys,
                Does.Contain("files").And.Contain("folders").And.Contain("otherAuthors").And.Contain("warnings").And.Contain("recycleBin"));
            Assert.That(api.Components.Schemas[nameof(AuthorDeletionFile)].Properties.Keys, Does.Contain("reason"));
        }
    }
}
