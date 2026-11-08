using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Chaptarr.Api.V1.Indexers;
using Chaptarr.Core.Test.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class ReleasePushDispatchFixture
    {
        [TestCase(false)] [TestCase(true)]
        public void push_should_dispatch_all_eligible_copies_even_if_the_first_decision_is_rejected(bool rejectFirst)
        {
            var sent = new List<(RemoteBook Book, int? Client)>();
            var maker = InterfaceProxy.Proxy<IMakeDownloadDecision>((method, args) =>
            {
                var release = ((List<ReleaseInfo>)args[0]).Single();
                Assert.That(args[1], Is.True);
                var author = new Author { Id = 1, Name = "Test Writer" };
                RemoteBook View(int id) => new()
                {
                    Author = author, AlternativeCopies = true, Release = release, DownloadAllowed = true,
                    Books = new List<Book> { new() { Id = id, AuthorId = 1, MediaType = BookMediaType.Ebook, HardcoverBookId = "hc:8200" } },
                    TargetEditionIds = new Dictionary<int, int> { [id] = id + 10 },
                    ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(Quality.EPUB), BookTitle = "Paper Lanterns" }
                };
                return new List<DownloadDecision>
                {
                    rejectFirst ? new DownloadDecision(View(1), new Rejection("Cutoff met")) : new DownloadDecision(View(1)),
                    new(View(2))
                };
            });
            var transport = InterfaceProxy.Proxy<IDownloadService>((method, args) =>
            {
                sent.Add(((RemoteBook)args[0], (int?)args[1]));
                return Task.CompletedTask;
            });
            var processor = new ProcessDownloadDecisions(transport,
                InterfaceProxy.Proxy<IPrioritizeDownloadDecision>((method, args) => args[0]),
                InterfaceProxy.Proxy<IPendingReleaseService>((method, args) => null), new Chaptarr.Core.Test.Download.EmptyQueueSpecification(), LogManager.GetCurrentClassLogger());
            var controller = new ReleasePushController(maker, processor,
                InterfaceProxy.Proxy<IIndexerFactory>((method, args) => new List<IndexerDefinition>()),
                InterfaceProxy.Proxy<IDownloadClientFactory>((method, args) => new List<DownloadClientDefinition>()),
                LogManager.GetCurrentClassLogger())
            { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
            controller.HttpContext.Request.Method = HttpMethods.Post;
            var result = controller.Create(new ReleaseResource
            {
                Title = "Test Writer - Paper Lanterns [EPUB]", DownloadUrl = "https://fixture.invalid/release",
                Indexer = "Fixture Indexer", IndexerId = 7, Protocol = DownloadProtocol.Torrent,
                PublishDate = DateTime.UtcNow.AddHours(-1), DownloadClientId = 9
            });
            Assert.That(sent, Has.Count.EqualTo(1));
            Assert.That(sent.Single().Book.Books.Select(book => book.Id), Is.EquivalentTo(rejectFirst ? new[] { 2 } : new[] { 1, 2 }));
            Assert.That(sent.Single().Client, Is.EqualTo(9));
            Assert.That(result.Value, Is.Not.Null);
        }
    }
}
