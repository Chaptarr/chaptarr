using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentValidation.Results;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.FileList;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Validation;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture]
    public class HttpIndexerEmptyFeedTestFixture
    {
        private sealed class EmptyFeedIndexer : HttpIndexerBase<FileListSettings>
        {
            public EmptyFeedIndexer()
                : base(null, null, null, null, LogManager.GetCurrentClassLogger())
            {
                Definition = new IndexerDefinition
                {
                    Settings = new FileListSettings { Username = "user", Passkey = "pass" }
                };
            }

            public override string Name => "EmptyFeed";
            public override DownloadProtocol Protocol => DownloadProtocol.Torrent;

            public override IIndexerRequestGenerator GetRequestGenerator() =>
                new FileListRequestGenerator { Settings = (FileListSettings)Definition.Settings };

            public override IParseIndexerResponse GetParser() => null;

            protected override Task<IList<ReleaseInfo>> FetchPage(IndexerRequest request, IParseIndexerResponse parser) =>
                Task.FromResult<IList<ReleaseInfo>>(new List<ReleaseInfo>());

            public async Task<List<ValidationFailure>> RunTest()
            {
                var failures = new List<ValidationFailure>();
                await Test(failures);
                return failures;
            }
        }

        [Test]
        public async Task should_report_an_empty_feed_as_a_warning_that_does_not_block_saving()
        {
            var failures = await new EmptyFeedIndexer().RunTest();

            Assert.That(failures, Has.Count.EqualTo(1));
            Assert.That(failures[0], Is.TypeOf<NzbDroneValidationFailure>());
            Assert.That(((NzbDroneValidationFailure)failures[0]).IsWarning, Is.True);

            var result = new NzbDroneValidationResult(failures);
            Assert.That(result.IsValid, Is.True);
            Assert.That(result.HasWarnings, Is.True);
        }
    }
}
