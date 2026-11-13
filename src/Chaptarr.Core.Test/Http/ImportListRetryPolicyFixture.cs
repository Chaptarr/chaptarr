using System;
using System.Collections.Generic;
using System.Net;
using FluentValidation.Results;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Goodreads;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Http
{
    public class ImportListRetryPolicyFixture
    {
        [TestCase("0", 3600)]
        [TestCase("-1", 3600)]
        [TestCase("60", 60)]
        public void list_fetch_keeps_its_existing_retry_default(string value, int expectedSeconds)
        {
            var response = new HttpResponse(new HttpRequest("http://list.test/"), new HttpHeader { ["Retry-After"] = value }, "", HttpStatusCode.TooManyRequests);
            TimeSpan? delay = null;
            var status = Proxy<IImportListStatusService>((method, args) =>
            {
                if (method.Name == nameof(IImportListStatusService.RecordFailure)) delay = (TimeSpan)args[1];
                return null;
            });
            new Probe(status, new TooManyRequestsException(response.Request, response)).Fetch();
            Assert.That(delay, Is.EqualTo(TimeSpan.FromSeconds(expectedSeconds)));
        }

        private sealed class Probe : HttpImportListBase<GoodreadsBookshelfImportListSettings>
        {
            private readonly Exception _error;
            public Probe(IImportListStatusService status, Exception error)
                : base(null, status, null, null, null, null, null, null, null, LogManager.GetCurrentClassLogger())
            {
                _error = error;
                Definition = new ImportListDefinition { Id = 1, Name = "Fixture", Settings = new GoodreadsBookshelfImportListSettings() };
            }
            public override string Name => "Fixture";
            public override ImportListType ListType => ImportListType.Other;
            public override TimeSpan MinRefreshInterval => TimeSpan.FromHours(1);
            public override IImportListRequestGenerator GetRequestGenerator() => throw _error;
            public override IParseImportListResponse GetParser() => null;
            protected override void Test(List<ValidationFailure> failures) { }
        }
    }
}
