using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Chaptarr.Api.V1.Books;
using Chaptarr.Http.ErrorManagement;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Exceptions;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class BookEditorControllerDeleteFixture
    {
        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task bulk_delete_returns_json_only_after_success(bool deleteFiles, bool failSecondBook)
        {
            var deleted = new List<int>();
            var books = Proxy<IBookService>((method, args) =>
            {
                Assert.That(method.Name, Is.EqualTo(nameof(IBookService.DeleteBook)));
                Assert.That(args[1], Is.EqualTo(deleteFiles));
                Assert.That(args[2], Is.True);
                var bookId = (int)args[0];
                if (failSecondBook && bookId == 18)
                {
                    throw new NzbDroneClientException(HttpStatusCode.Conflict, "The root folder is unavailable.");
                }

                deleted.Add(bookId);
                return null;
            });
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            builder.Services.AddCors(options => options.AddPolicy("ApiCorsPolicy", policy => policy.AllowAnyOrigin()));
            builder.Services.AddControllers().AddApplicationPart(typeof(BookEditorController).Assembly).AddControllersAsServices();
            builder.Services.AddTransient(_ => new BookEditorController(books, null, null));
            await using var app = builder.Build();
            app.UseExceptionHandler(handler => handler.Run(context => new ChaptarrErrorPipeline(LogManager.GetCurrentClassLogger()).HandleException(context)));
            app.UseCors();
            app.MapControllers();
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses.Single();
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false })
            {
                BaseAddress = new Uri(address),
                Timeout = TimeSpan.FromSeconds(10)
            };
            using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/book/editor")
            {
                Content = JsonContent.Create(new BookEditorResource
                {
                    BookIds = new List<int> { 17, 18 },
                    DeleteFiles = deleteFiles,
                    AddImportListExclusion = true
                })
            };
            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/json"));
            Assert.That(response.StatusCode, Is.EqualTo(failSecondBook ? HttpStatusCode.Conflict : HttpStatusCode.OK));
            Assert.That(deleted, Is.EqualTo(failSecondBook ? new[] { 17 } : new[] { 17, 18 }));
            Assert.That(body, failSecondBook ? Does.Contain("The root folder is unavailable.") : Is.EqualTo("{}"));
        }
    }
}
