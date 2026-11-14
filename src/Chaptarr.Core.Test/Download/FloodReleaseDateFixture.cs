using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients.Flood;
using NzbDrone.Core.Download.Clients.Flood.Models;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class FloodReleaseDateFixture
    {
        private sealed class TestFlood : Flood
        {
            public TestFlood(IFloodProxy proxy)
                : base(proxy, null, null, null, null, null, null, null, LogManager.GetCurrentClassLogger())
            {
            }

            public string Add(RemoteBook book, bool magnet) => magnet
                ? AddFromMagnetLink(book, "hash", "magnet:?xt=urn:btih:hash")
                : AddFromTorrentFile(book, "hash", "book.torrent", new byte[] { 1 });
        }

        [TestCase(false, BookMediaType.Audiobook, false)]
        [TestCase(true, BookMediaType.Audiobook, false)]
        [TestCase(false, BookMediaType.Ebook, false)]
        [TestCase(true, BookMediaType.Ebook, false)]
        [TestCase(false, BookMediaType.Audiobook, true)]
        [TestCase(true, BookMediaType.Ebook, true)]
        public void year_tag_should_skip_unknown_dates_and_preserve_known_years(bool magnet, BookMediaType side, bool includeDated)
        {
            var tags = new List<string>();
            var adds = 0;
            var proxy = InterfaceProxy.Proxy<IFloodProxy>((method, args) =>
            {
                Assert.That(method.Name, Is.EqualTo(magnet ? nameof(IFloodProxy.AddTorrentByUrl) : nameof(IFloodProxy.AddTorrentByFile)));
                tags.AddRange((IEnumerable<string>)args[1]);
                adds++;
                return null;
            });
            var client = new TestFlood(proxy)
            {
                Definition = new DownloadClientDefinition
                {
                    Settings = new FloodSettings
                    {
                        AudiobookTags = new[] { "audiobooks" }, EbookTags = new[] { "ebooks" },
                        AdditionalTags = new[] { (int)AdditionalTags.Year }
                    }
                }
            };
            var remote = new RemoteBook
            {
                Books = new List<Book> { new() { Title = "Undated Work", MediaType = side, ReleaseDate = null } }
            };
            if (includeDated)
            {
                remote.Books.Add(new Book { Title = "Dated Work", MediaType = side, ReleaseDate = new DateTime(2020, 5, 6) });
                remote.Books.Add(new Book { Title = "Later Work", MediaType = side, ReleaseDate = new DateTime(2021, 7, 8) });
            }

            Assert.That(client.Add(remote, magnet), Is.EqualTo("hash"));
            Assert.That(adds, Is.EqualTo(1));
            var expected = new List<string> { side == BookMediaType.Audiobook ? "audiobooks" : "ebooks" };
            if (includeDated) expected.AddRange(new[] { "2020", "2021" });
            Assert.That(tags, Is.EquivalentTo(expected));
            Assert.That(remote.Books.First().ReleaseDate, Is.Null);
        }
    }
}
