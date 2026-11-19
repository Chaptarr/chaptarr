using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Chaptarr.Core.Test.Common;
using Chaptarr.Core.Test.MetadataSource.BookInfo;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.MetadataSource;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Books
{
    public partial class PersistedAliasPendingLifecycleFixture
    {
        [Test]
        public void actual_work_http_refusal_or_transient_keeps_healthy_selection_and_exact_search(
            [Values(false, true)] bool postgres,
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media,
            [Values("direct400", "plain400", "plain409", "typed409", "oversized409", "202", "429", "503", "invalid-json", "local-invalid")] string failure)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var owner = ErrorContractOwner(h);
                owner.GoodreadsAuthorId = "gr:58";
                h.Authors.Update(owner);
                var healthy = h.AddBook(media, "hc:312460", "hc:edition:12250068");
                healthy.AuthorId = failure == "503" ? h.Author.Id : owner.Id;
                h.Books.SetAuthorId(healthy);
                var failedWork = failure == "local-invalid" ? "unprefixed" : "gb:unsupported";
                var pending = ErrorContractRequest(h, media, new() { failedWork, "hc:312460" });
                var attempts = new List<string>();
                var library = Proxy<IAuthorLibraryService>((_, args) =>
                {
                    attempts.Add((string)args[0]);
                    return Task.FromResult((string)args[0] == owner.GoodreadsAuthorId ? owner : h.Author);
                });
                var urls = new List<string>();
                var client = Proxy<IHttpClient>((method, args) =>
                {
                    if (method.Name != nameof(IHttpClient.Get)) throw new AssertionException(method.Name);
                    var request = (HttpRequest)args[0];
                    urls.Add(request.Url.Path);
                    var healthyLookup = request.Url.Path.EndsWith("hc:312460", StringComparison.Ordinal);
                    if (healthyLookup)
                        return new HttpResponse(request, new HttpHeader { ContentType = "application/json" },
                            BookInfoProxyCanonicalLookupFixture.SecondaryEditionAliasesPayload, HttpStatusCode.OK);
                    var status = failure switch
                    {
                        "202" => HttpStatusCode.Accepted,
                        "429" => HttpStatusCode.TooManyRequests,
                        "503" => HttpStatusCode.ServiceUnavailable,
                        "invalid-json" => HttpStatusCode.OK,
                        "plain409" or "typed409" or "oversized409" => HttpStatusCode.Conflict,
                        _ => HttpStatusCode.BadRequest
                    };
                    var payload = failure switch
                    {
                        "direct400" => BookInfoProxyLookupFailureFixture.TypedLookupPayload("invalid_provider_id", failedWork, "Unsupported {identifier}"),
                        "typed409" => BookInfoProxyLookupFailureFixture.TypedLookupPayload("work_rescue_terminal", failedWork, "Declared work rescue stop"),
                        "oversized409" => BookInfoProxyLookupFailureFixture.TypedLookupPayload("work_rescue_terminal", failedWork, "Declared stop " + new string('x', 8192)),
                        "invalid-json" => "not JSON",
                        "plain400" or "plain409" => "Untrusted proxy body " + new string('x', 8192),
                        _ => "Unsupported {identifier}\n"
                    };
                    return new HttpResponse(request, new HttpHeader { ContentType = "application/json", ["Retry-After"] = "0" }, payload, status);
                });
                var metadata = BookInfoProxyCanonicalLookupFixture.CreateProxy(client);
                void Run() => new ProcessPendingImportsCommandHandler(h.Pending, library, h.BookService, metadata,
                    h.Queue, h.EventAggregator, LogManager.GetCurrentClassLogger(), h.Aliases).Execute(new ProcessPendingImportsCommand());
                Run();
                if (failure != "503")
                {
                    var receiving = h.Pending.GetByProviderId(owner.GoodreadsAuthorId);
                    Assert.That(receiving, Is.Not.Null);
                    Run();
                    Assert.That(h.Requests.Find(receiving.Id), Is.Null);
                }
                var saved = h.Requests.Get(pending.Id);
                var terminal = failure == "direct400" || failure == "typed409" || failure == "oversized409" || failure == "local-invalid";
                Assert.Multiple(() =>
                {
                    Assert.That(saved.OverallStatus, Is.EqualTo(terminal ? PendingImportStatus.PartialSuccess : PendingImportStatus.Retrying));
                    Assert.That(saved.IsActive(), Is.EqualTo(!terminal));
                    Assert.That(attempts, Is.EqualTo(failure == "503"
                        ? new[] { h.Author.HardcoverAuthorId } : new[] { h.Author.HardcoverAuthorId, owner.GoodreadsAuthorId }));
                    Assert.That(h.Commands.OfType<BookSearchCommand>().SelectMany(command => command.BookIds), Is.EqualTo(new[] { healthy.Id }));
                    Assert.That(h.BookService.GetBook(healthy.Id).IsMonitored(), Is.True);
                    Assert.That(h.Books.All().Select(book => book.Id), Is.EqualTo(new[] { healthy.Id }));
                    Assert.That(urls.Count(url => url.EndsWith("hc:312460", StringComparison.Ordinal)), Is.EqualTo(failure == "503" ? 0 : 1));
                    Assert.That(urls.Count(url => !url.EndsWith("hc:312460", StringComparison.Ordinal)),
                        Is.EqualTo(failure == "local-invalid" ? 0 : failure == "429" ? 3 : 1));
                    if (failure == "direct400") Assert.That(saved.LastError, Does.Contain("Unsupported {identifier}"));
                    if (failure == "typed409" || failure == "oversized409") Assert.That(saved.LastError, Does.Contain("Declared"));
                    if (failure == "oversized409") Assert.That(System.Text.Encoding.UTF8.GetByteCount(saved.LastError), Is.LessThan(1024));
                    Assert.That(saved.LastError, Does.Not.Contain("Untrusted proxy body"));
                });
                if (!terminal)
                {
                    h.Pending.RetryNow(pending.Id);
                    Run();
                    Assert.That(h.Requests.Get(pending.Id).IsActive(), Is.True);
                    Assert.That(h.Commands.OfType<BookSearchCommand>().SelectMany(command => command.BookIds), Is.EqualTo(new[] { healthy.Id }));
                }
            });
        }
    }
}
