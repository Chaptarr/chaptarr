using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Chaptarr.Api.V1.Books;
using Microsoft.AspNetCore.Http;
using NLog;
using PendingBookRequestException = NzbDrone.Core.Exceptions.PendingBookRequestException;
using NzbDrone.Core.Validation;
using NzbDrone.Core.RootFolders;
using static Chaptarr.Core.Test.Common.InterfaceProxy;
using Chaptarr.Api.V1.Author;
using Chaptarr.Core.Test.Common;
using Chaptarr.Http.REST;
using Microsoft.AspNetCore.Mvc;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;

namespace Chaptarr.Core.Test.Api
{
    public partial class AuthorControllerProfileRoundTripFixture
    {
        [Test]
        public void classic_add_infers_single_format_and_ignores_empty_lookup_tags(
            [Values] bool postgres, [Values] bool ebook, [Values] bool echo, [Values] bool mixed)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database, ebook, false, initialSync: mixed ? false : null);
                h.Controller.Request.Path = "/api/v1/author";
                var resource = ClassicPayload(h, ebook, echo);
                if (mixed)
                {
                    var error = Assert.ThrowsAsync<BadRequestException>(async () => await h.Controller.AddAuthor(resource));
                    Assert.That(error.Message, Does.Contain("mediaType"));
                    Assert.That(h.Adds, Is.Empty);
                    return;
                }

                var result = h.Controller.AddAuthor(resource).GetAwaiter().GetResult();
                Assert.That(result.Result, Is.TypeOf<AcceptedResult>());
                Assert.That(h.Adds, Has.Count.EqualTo(1));
                Assert.That(h.Adds.Single().CreateAudiobook, Is.EqualTo(!ebook));
                Assert.That(h.Adds.Single().CreateEbook, Is.EqualTo(ebook));
            });
        }

        [Test]
        public void classic_put_infers_the_existing_side_without_a_root_or_nonempty_tags(
            [Values] bool postgres, [Values] bool ebook)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database, ebook, false);
                h.Controller.Request.Path = "/api/v1/author/" + h.Author.Id;
                var resource = ClassicPayload(h, ebook, true);
                resource.RootFolderPath = null;
                h.Validate(resource);
                Assert.That(h.Controller.UpdateAuthor(resource).Result, Is.TypeOf<AcceptedAtActionResult>());
                var saved = h.Authors.Get(h.Author.Id);
                Assert.That(ebook ? saved.AudiobookQualityProfileId : saved.EbookQualityProfileId, Is.Null);
            });
        }

        [Test]
        public void classic_path_with_move_uses_the_single_format_folder(
            [Values] bool postgres, [Values] bool ebook, [Values] bool dual)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database, ebook, false, initialSync: dual ? false : null);
                var resource = h.GetPayload();
                resource.AudiobookFolder = null;
                resource.EbookFolder = null;
                resource.Path = Path.Combine(Path.GetDirectoryName(h.Author.Path), "Moved Author");
                if (dual)
                {
                    var error = Assert.Throws<BadRequestException>(() => h.Controller.UpdateAuthor(resource, moveFiles: true));
                    Assert.That(error.Message, Does.Contain("audiobookFolder").And.Contain("ebookFolder"));
                    Assert.That(h.Commands, Is.Empty);
                    Assert.That(h.Authors.Get(h.Author.Id).Path, Is.EqualTo(h.Author.Path));
                    return;
                }

                h.Controller.UpdateAuthor(resource, moveFiles: true);
                var command = h.Commands.OfType<MoveAuthorCommand>().Single();
                Assert.That(command.SourcePath, Is.EqualTo(h.Author.Path));
                Assert.That(command.DestinationPath, Is.EqualTo(resource.Path));
                Assert.That(command.MediaType, Is.EqualTo(ebook ? "ebook" : "audiobook"));
            });
        }

        [Test]
        public void book_post_infers_root_or_existing_author_without_empty_tags_requesting_both_formats(
            [Values] bool postgres, [Values] bool ebook, [Values] bool dual, [Values] bool rootProvided)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database, ebook, false, initialSync: dual ? false : null);
                foreach (var root in h.Roots.All())
                {
                    root.SetAudiobookSettings(new MediaTypeSettings { QualityProfileId = 1, MetadataProfileId = 1 });
                    root.SetEbookSettings(new MediaTypeSettings { QualityProfileId = 2, MetadataProfileId = 2 });
                }
                var added = new List<Book>();
                var service = Proxy<IBookService>((method, args) => method.Name switch
                {
                    nameof(IBookService.FindAllByProviderId) or nameof(IBookService.FindAllByWorkProviderId) => new List<Book>(),
                    _ => throw new NotImplementedException(method.Name)
                });
                var add = Proxy<IAddBookService>((method, args) =>
                {
                    Assert.That(method.Name, Is.EqualTo(nameof(IAddBookService.AddBook)));
                    added.Add((Book)args[0]);
                    return Task.FromException<Book>(new PendingBookRequestException(77));
                });
                var controller = new BookController(h.AuthorService, service, add, null, null, null, null, null, null, null,
                    null, null, null, h.Roots,
                    new QualityProfileExistsValidator(new TestQualityProfileService()),
                    new MetadataProfileExistsValidator(new TestMetadataProfileService()), LogManager.GetCurrentClassLogger())
                {
                    ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
                };
                controller.Request.Path = "/api/v1/book";
                var author = ClassicPayload(h, ebook, true);
                if (!rootProvided) author.RootFolderPath = null;
                var result = controller.AddBook(null, new BookResource
                {
                    Title = "Exhalation", ForeignBookId = "gr:53048196", Author = author
                }).GetAwaiter().GetResult();
                if (dual)
                {
                    Assert.That(result.Result, Is.TypeOf<BadRequestObjectResult>());
                    Assert.That(((BadRequestObjectResult)result.Result).Value.ToString(), Does.Contain("mediaType"));
                    Assert.That(added, Is.Empty);
                }
                else
                {
                    Assert.That(result.Result, Is.TypeOf<AcceptedResult>());
                    Assert.That(added, Has.Count.EqualTo(1));
                    Assert.That(added.Single().MediaType, Is.EqualTo(ebook ? BookMediaType.Ebook : BookMediaType.Audiobook));
                    Assert.That(added.Single().IsMonitored(), Is.False);
                }
            });
        }

        private static AuthorResource ClassicPayload(Harness h, bool ebook, bool echo) => new AuthorResource
        {
            Id = h.Author.Id,
            AuthorName = h.Author.Name,
            ForeignAuthorId = h.Author.GoodreadsAuthorId,
            Path = h.Author.Path,
            RootFolderPath = ebook ? h.Author.EbookRootFolderPath : h.Author.AudiobookRootFolderPath,
            QualityProfileId = ebook ? h.Author.EbookQualityProfileId : h.Author.AudiobookQualityProfileId,
            MetadataProfileId = h.Author.MetadataProfileId,
            Monitored = true,
            AudiobookTags = echo ? new HashSet<int>() : null,
            EbookTags = echo ? new HashSet<int>() : null
        };
    }
}
