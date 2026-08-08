using System;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.DecisionEngine.Specifications.RssSync;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Delay;

namespace Chaptarr.Core.Test.DecisionEngine
{
    [TestFixture]
    public class DelaySpecificationFixture
    {
        private class ThrowingProxy<T> : DispatchProxy
            where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"User-invoked search should not call {typeof(T).Name}.{targetMethod?.Name}");
            }
        }

        [Test]
        public void should_ignore_delay_for_user_invoked_search()
        {
            var subject = new DelaySpecification(
                DispatchProxy.Create<IPendingReleaseService, ThrowingProxy<IPendingReleaseService>>(),
                DispatchProxy.Create<IUpgradableSpecification, ThrowingProxy<IUpgradableSpecification>>(),
                DispatchProxy.Create<IDelayProfileService, ThrowingProxy<IDelayProfileService>>(),
                DispatchProxy.Create<IMediaFileService, ThrowingProxy<IMediaFileService>>(),
                LogManager.GetCurrentClassLogger());

            var decision = subject.IsSatisfiedBy(
                new RemoteBook(),
                new BookSearchCriteria { UserInvokedSearch = true });

            Assert.That(decision.Accepted, Is.True);
        }
    }
}
