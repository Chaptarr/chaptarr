using System.Collections.Generic;
using Chaptarr.Api.V1.Books;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class BookFilePreviewResourceMapperFixture
    {
        [Test]
        public void should_expose_book_file_id_as_organize_preview_row_id()
        {
            var preview = new RenameBookFilePreview
            {
                BookFileId = 42
            };

            var resource = preview.ToResource();

            Assert.That(resource.Id, Is.EqualTo(preview.BookFileId));
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
