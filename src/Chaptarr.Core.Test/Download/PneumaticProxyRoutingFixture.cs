using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Http;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients.Pneumatic;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class PneumaticProxyRoutingFixture
    {
        private class HttpClientProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new AssertionException($"General HTTP client should not be used: {targetMethod?.Name}");
            }
        }

        private class IndexerProxy : DispatchProxy
        {
            public bool Executed { get; private set; }
            public string RateLimitKey { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IIndexer.GetDownloadRequest))
                {
                    return new HttpRequest((string)args[0]);
                }

                if (targetMethod?.Name == nameof(IIndexer.ExecuteDownloadRequestAsync))
                {
                    Executed = true;
                    var request = (HttpRequest)args[0];
                    RateLimitKey = request.RateLimitKey;
                    return Task.FromResult(new HttpResponse(request, new HttpHeader(), new byte[] { 1, 2, 3 }));
                }

                throw new NotImplementedException(targetMethod?.Name);
            }
        }

        private class DiskProviderProxy : DispatchProxy
        {
            public byte[] SavedBytes { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case nameof(IDiskProvider.SaveStream):
                        using (var memory = new MemoryStream())
                        {
                            ((Stream)args[0]).CopyTo(memory);
                            SavedBytes = memory.ToArray();
                        }

                        return null;
                    case nameof(IDiskProvider.WriteAllText):
                        return null;
                    case nameof(IDiskProvider.FileGetLastWrite):
                        return DateTime.UtcNow;
                    default:
                        throw new NotImplementedException(targetMethod?.Name);
                }
            }
        }

        [Test]
        public async Task nzb_download_should_use_the_originating_indexer_transport()
        {
            var disk = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var indexer = DispatchProxy.Create<IIndexer, IndexerProxy>();
            var subject = new Pneumatic(
                DispatchProxy.Create<IHttpClient, HttpClientProxy>(),
                null,
                disk,
                null,
                LogManager.GetCurrentClassLogger())
            {
                Definition = new DownloadClientDefinition
                {
                    Name = "Pneumatic",
                    Settings = new PneumaticSettings
                    {
                        NzbFolder = "/nzb",
                        StrmFolder = "/strm"
                    }
                }
            };
            var remoteBook = new RemoteBook
            {
                Release = new ReleaseInfo
                {
                    Title = "Book",
                    DownloadUrl = "https://indexer.example/download/1",
                    IndexerId = 44
                },
                ParsedBookInfo = new ParsedBookInfo()
            };

            await subject.Download(remoteBook, indexer);

            Assert.That(((IndexerProxy)(object)indexer).Executed, Is.True);
            Assert.That(((IndexerProxy)(object)indexer).RateLimitKey, Is.EqualTo("44"));
            Assert.That(((DiskProviderProxy)(object)disk).SavedBytes, Is.EqualTo(new byte[] { 1, 2, 3 }));
        }
    }
}
