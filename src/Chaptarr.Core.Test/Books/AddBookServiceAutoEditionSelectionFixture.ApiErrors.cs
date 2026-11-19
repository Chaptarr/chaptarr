using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Chaptarr.Api.V1;
using Chaptarr.Api.V1.Author;
using Chaptarr.Api.V1.Books;
using Chaptarr.Api.V1.ProviderIds;
using Chaptarr.Core.Test.MetadataSource.BookInfo;
using Chaptarr.Http.ErrorManagement;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;

using HttpClient = System.Net.Http.HttpClient;
using HttpRequest = NzbDrone.Common.Http.HttpRequest;
using HttpResponse = NzbDrone.Common.Http.HttpResponse;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Books
{
    public partial class AddBookServiceAutoEditionSelectionFixture
    {
        private static async Task<(HttpStatusCode Status, string Body, string ContentType, AuthorLibraryProxy Imports, List<HttpRequest> Requests)>
            NativeApiAddWithMetadata(BookMediaType media, string outcome, string endpoint = "book")
        {
            var requests = new List<HttpRequest>();
            var metadataClient = Proxy<IHttpClient>((method, args) =>
            {
                if (method.Name != nameof(IHttpClient.Get)) throw new AssertionException(method.Name);
                var request = (HttpRequest)args[0];
                requests.Add(request);
                var author = request.Url.ToString().Contains("/api/v5/author?");
                var status = author ? outcome == "author_identity_ambiguous" ? HttpStatusCode.Conflict : HttpStatusCode.NotFound
                    : outcome == "work" || outcome == "untyped" ? HttpStatusCode.Conflict
                    : outcome == "invalid" ? HttpStatusCode.BadRequest
                    : outcome == "outage" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Accepted;
                var code = author ? outcome : outcome == "work" ? "work_rescue_terminal" : "invalid_provider_id";
                var content = author || outcome is "work" or "invalid"
                    ? JsonSerializer.Serialize(new { code, providerId = author ? "hc:2222" : "hc:7000", message = "Declared provider reason", retryable = false, reopenable = true })
                    : "null";
                return new HttpResponse(request, new HttpHeader { ContentType = "application/json" }, content, status);
            });
            var metadata = BookInfoProxyCanonicalLookupFixture.CreateProxy(metadataClient);
            var library = DispatchProxy.Create<IAuthorLibraryService, AuthorLibraryProxy>();
            var imports = (AuthorLibraryProxy)library;
            imports.AddedAuthor = new NzbDrone.Core.Books.Author { Id = -77 };
            if (outcome.StartsWith("author_")) imports.BeforeAdd = () => metadata.GetAuthorInfo("hc:2222", false);
            if (outcome == "fault") imports.BeforeAdd = () => throw new InvalidOperationException("Unexpected author writer fault");
            var localAuthors = new[]
            {
                new NzbDrone.Core.Books.Author { Id = 201, Name = "First Author", HardcoverAuthorId = "hc:3333" },
                new NzbDrone.Core.Books.Author { Id = 202, Name = "Second Author", HardcoverAuthorId = "hc:4444" }
            };
            var authors = Proxy<IAuthorService>((method, args) => method.Name switch
            {
                nameof(IAuthorService.FindByProviderId) => null,
                nameof(IAuthorService.GetAuthor) => localAuthors.SingleOrDefault(author => author.Id == (int)args[0]),
                _ => throw new AssertionException(method.Name)
            });
            var aliases = outcome == "ambiguity" && endpoint == "author"
                ? Proxy<IProviderAliasService>((method, args) =>
                    method.Name == nameof(IProviderAliasService.FindAuthorIds)
                        ? new List<int> { 201, 202 } : throw new AssertionException(method.Name)) : null;
            var books = new StubBookService();
            if (outcome == "ambiguity" && endpoint == "book")
                books.AuthorBooks.AddRange(new[] { 301, 302 }.Select(id => new Book
                { Id = id, AuthorId = 201, Title = "Requested Work", MediaType = media, HardcoverBookId = "hc:7000" }));
            var editions = new StubEditionService(Array.Empty<Edition>());
            var add = BuildService(authors, books, editions, new StubMetadataProfileService(), library,
                importListExclusionService: DispatchProxy.Create<IImportListExclusionService, ImportListExclusionServiceProxy>(), bookInfo: metadata);
            var root = new RootFolder { Id = 1, Path = @"C:\books".AsOsAgnostic(), FolderType = FolderType.Mixed };
            root.SetAudiobookSettings(new MediaTypeSettings { QualityProfileId = 1, MetadataProfileId = 1 });
            root.SetEbookSettings(new MediaTypeSettings { QualityProfileId = 1, MetadataProfileId = 1 });
            var roots = Proxy<IRootFolderService>((method, args) => method.Name == nameof(IRootFolderService.All)
                ? new List<RootFolder> { root } : throw new AssertionException(method.Name));
            var logger = LogManager.GetCurrentClassLogger();
            var names = Proxy<IBuildFileNames>((method, args) => method.Name == nameof(IBuildFileNames.GetAuthorFolder)
                ? "Requested Author" : throw new AssertionException(method.Name));
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            builder.Services.AddCors(options => options.AddPolicy("ApiCorsPolicy", policy => policy.AllowAnyOrigin()));
            builder.Services.AddControllers().AddApplicationPart(typeof(BookController).Assembly).AddControllersAsServices();
            builder.Services.AddTransient(_ => new BookController(authors, books, add, editions, new EditionSelector(logger),
                null, null, null, null, null, null, null, null, roots,
                new QualityProfileExistsValidator(new TestQualityProfileService()),
                new MetadataProfileExistsValidator(new TestMetadataProfileService()), logger));
            builder.Services.AddTransient(_ => new AuthorController(null, authors, books, null, library, null, null, null, roots, null, null, names,
                logger, new RecycleBinValidator(null), new RootFolderValidator(null), new MappedNetworkDriveValidator(null, null),
                new AuthorPathValidator(authors), new AuthorExistsValidator(authors), new AuthorAncestorValidator(authors),
                new SystemFolderValidator(), new QualityProfileExistsValidator(new TestQualityProfileService()),
                new MetadataProfileExistsValidator(new TestMetadataProfileService()), new AuthorFolderAsRootFolderValidator(names), aliases));
            await using var app = builder.Build();
            app.UseExceptionHandler(handler => handler.Run(context => new ChaptarrErrorPipeline(logger).HandleException(context)));
            app.UseCors();
            app.MapControllers();
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses.Single();
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false })
            { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(20) };
            var authorResource = new AuthorResource
            {
                ForeignAuthorId = outcome == "invalid" && endpoint == "author" ? "invalid:2222" : "hc:2222",
                AuthorName = "Requested Author",
                AudiobookRootFolderPath = media == BookMediaType.Audiobook ? root.Path : null,
                AudiobookQualityProfileId = media == BookMediaType.Audiobook ? 1 : null,
                AudiobookMetadataProfileId = media == BookMediaType.Audiobook ? 1 : null,
                EbookRootFolderPath = media == BookMediaType.Ebook ? root.Path : null,
                EbookQualityProfileId = media == BookMediaType.Ebook ? 1 : null,
                EbookMetadataProfileId = media == BookMediaType.Ebook ? 1 : null
            };
            object resource = endpoint == "author" ? authorResource : new BookResource
            { Title = "Requested Work", HardcoverBookId = "hc:7000", ForeignBookId = "hc:7000", Author = authorResource };
            using var response = await client.PostAsJsonAsync($"/api/v1/{endpoint}?mediaType={(media == BookMediaType.Audiobook ? "audiobook" : "ebook")}", resource);
            var body = await response.Content.ReadAsStringAsync();
            TestContext.WriteLine($"POST /api/v1/{endpoint} ({media}, {outcome}): {(int)response.StatusCode}\n{body}");
            Assert.That(books.AddedBook, Is.Null);
            Assert.That(books.RequestedMonitoringCalls, Is.Empty);
            return (response.StatusCode, body, response.Content.Headers.ContentType?.MediaType, imports, requests);
        }

        private static void AssertDeclaredApiRefusal(HttpStatusCode status, string contentType, string body, string message)
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.UnprocessableEntity));
            Assert.That(contentType, Is.EqualTo("application/json"));
            using var json = JsonDocument.Parse(body);
            Assert.That(json.RootElement.EnumerateObject().Select(property => property.Name), Is.EqualTo(new[] { "message" }));
            Assert.That(STJson.Deserialize<ApiErrorResource>(body).Message, Is.EqualTo(message));
        }

        [Test]
        public async Task native_add_declared_author_refusals_return_unprocessable_with_the_reason(
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media,
            [Values("book", "author")] string endpoint,
            [Values("author_provider_unsupported", "author_identifier_not_author", "author_provider_record_missing", "author_no_primary_works", "author_identity_ambiguous")] string outcome)
        {
            var response = await NativeApiAddWithMetadata(media, outcome, endpoint);
            AssertDeclaredApiRefusal(response.Status, response.ContentType, response.Body, outcome + ": Declared provider reason");
            Assert.That(response.Imports.ProviderId, Is.Null);
            Assert.That(response.Requests, Has.Count.EqualTo(endpoint == "book" ? 2 : 1));
        }

        [Test]
        public async Task native_book_add_declared_work_refusal_returns_unprocessable_with_the_reason(
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media)
        {
            var response = await NativeApiAddWithMetadata(media, "work");
            AssertDeclaredApiRefusal(response.Status, response.ContentType, response.Body, "Declared provider reason");
            Assert.That(response.Imports.ProviderId, Is.Null);
            Assert.That(response.Requests, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task native_add_invalid_ids_and_unexpected_faults_keep_their_status(
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media,
            [Values("book", "author")] string endpoint,
            [Values("invalid", "fault", "author_unknown_code")] string outcome)
        {
            var response = await NativeApiAddWithMetadata(media, outcome, endpoint);
            Assert.That(response.Status, Is.EqualTo(outcome == "invalid" ? HttpStatusCode.BadRequest
                : outcome == "author_unknown_code" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.InternalServerError));
            if (outcome != "invalid" || endpoint == "book")
                Assert.That(STJson.Deserialize<ApiErrorResource>(response.Body).Message, Does.Contain(outcome == "invalid"
                    ? "Declared provider reason" : outcome == "fault" ? "Unexpected author writer fault" : "unknown author terminal code"));
        }

        [Test]
        public async Task native_book_add_temporary_or_untyped_metadata_keeps_the_pending_response(
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media,
            [Values("pending", "outage", "untyped")] string outcome)
        {
            var response = await NativeApiAddWithMetadata(media, outcome);
            Assert.That(response.Status, Is.EqualTo(HttpStatusCode.Accepted));
            Assert.That(STJson.Deserialize<PendingBookRequestResource>(response.Body).PendingId, Is.EqualTo(77));
            Assert.That(response.Imports.ProviderId, Is.EqualTo("hc:2222"));
            Assert.That(response.Imports.Config.QueueIfUnavailable, Is.True);
            Assert.That(response.Requests, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task native_author_add_pending_acknowledgement_keeps_the_accepted_response(
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media)
        {
            var response = await NativeApiAddWithMetadata(media, "pending", "author");
            Assert.That(response.Status, Is.EqualTo(HttpStatusCode.Accepted));
            using var body = JsonDocument.Parse(response.Body);
            Assert.That(body.RootElement.GetProperty("pendingId").GetInt32(), Is.EqualTo(77));
            Assert.That(response.Imports.Config.QueueIfUnavailable, Is.True);
        }

        [Test]
        public async Task native_add_provider_ambiguity_keeps_conflict_with_candidates(
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media,
            [Values("book", "author")] string endpoint)
        {
            var response = await NativeApiAddWithMetadata(media, "ambiguity", endpoint);
            Assert.That(response.Status, Is.EqualTo(HttpStatusCode.Conflict));
            var body = STJson.Deserialize<ProviderAmbiguityResource>(response.Body);
            Assert.That(body.EntityType, Is.EqualTo(endpoint));
            Assert.That(body.Candidates.Select(candidate => candidate.Id), Is.EqualTo(endpoint == "book" ? new[] { 301, 302 } : new[] { 201, 202 }));
            Assert.That(response.Requests, Is.Empty);
            Assert.That(response.Imports.ProviderId, Is.Null);
        }

        [Test]
        public void native_add_annotations_distinguish_refusal_from_provider_ambiguity(
            [Values(typeof(BookController), typeof(AuthorController))] Type controller)
        {
            var method = controller.GetMethod(controller == typeof(BookController) ? nameof(BookController.AddBook) : nameof(AuthorController.AddAuthor));
            var responses = method.GetCustomAttributes<ProducesResponseTypeAttribute>().ToList();
            Assert.That(responses.Single(response => response.StatusCode == StatusCodes.Status422UnprocessableEntity).Type, Is.EqualTo(typeof(ApiErrorResource)));
            Assert.That(responses.Single(response => response.StatusCode == StatusCodes.Status409Conflict).Type, Is.EqualTo(typeof(ProviderAmbiguityResource)));
        }
    }
}
