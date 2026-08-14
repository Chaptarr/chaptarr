using System.Collections.Generic;
using Chaptarr.Api.V1;
using Chaptarr.Api.V1.Books;
using Microsoft.AspNetCore.Mvc;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class BookFilePreviewResourceMapperFixture
    {
        private sealed class StubRenameBookFileService : IRenameBookFileService
        {
            public List<RenameBookFilePreview> GetRenamePreviews(int authorId, string mediaType = null, bool moveToCanonicalAuthorFolder = false) => new() { new RenameBookFilePreview { BookFileId = 42 } };
            public List<RenameBookFilePreview> GetRenamePreviews(int authorId, int bookId) => new();
        }

        [Test]
        public void should_expose_book_file_id_as_organize_preview_row_id()
        {
            var preview = new RenameBookFilePreview
            {
                BookId = 21,
                EditionId = 22,
                BookFileId = 42,
                CanOrganize = false,
                Reason = "Author folder boundary unavailable"
            };

            var resource = preview.ToResource();

            Assert.That(resource.Id, Is.EqualTo(preview.BookFileId));
            Assert.That(resource.BookId, Is.EqualTo(preview.BookId));
            Assert.That(resource.EditionId, Is.EqualTo(preview.EditionId));
            Assert.That(resource.CanOrganize, Is.False);
            Assert.That(resource.Reason, Is.EqualTo(preview.Reason));
        }

        [Test]
        public void should_return_preview_rows_for_author_scoped_preview()
        {
            var controller = new RenameBookController(new StubRenameBookFileService());

            var result = controller.GetBookFiles(1, null);

            Assert.That(result.Result, Is.Null);
            Assert.That(result.Value, Is.Not.Null);
            Assert.That(result.Value, Has.Count.EqualTo(1));
            Assert.That(result.Value[0].Id, Is.EqualTo(42));
        }

        [Test]
        public void should_reject_canonical_author_folder_for_book_scoped_preview()
        {
            var controller = new RenameBookController(new StubRenameBookFileService());

            var result = controller.GetBookFiles(1, 21, moveToCanonicalAuthorFolder: true);

            var badRequest = result.Result as BadRequestObjectResult;
            Assert.That(badRequest, Is.Not.Null);
            Assert.That(badRequest.StatusCode, Is.EqualTo(400));

            var error = badRequest.Value as ApiErrorResource;
            Assert.That(error, Is.Not.Null);
            Assert.That(error.Message, Does.Contain("only supported for author-level previews"));
        }

        [Test]
        public void should_expose_book_file_id_as_retag_preview_row_id()
        {
            var preview = new RetagBookFilePreview
            {
                BookFileId = 42,
                Changes = new Dictionary<string, System.Tuple<string, string>>()
            };

            var resource = preview.ToResource();

            Assert.That(resource.Id, Is.EqualTo(preview.BookFileId));
        }
    }
}
