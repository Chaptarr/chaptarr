using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.ThingiProvider;

namespace Chaptarr.Core.Test.ThingiProvider
{
    [TestFixture]
    public class PendingProviderSecretServiceFixture
    {
        [Test]
        public void should_return_opaque_handle_and_resolve_secret()
        {
            var subject = new PendingProviderSecretService(new CacheManager());

            var handle = subject.Create("real-secret");

            Assert.That(handle, Is.Not.EqualTo("real-secret"));
            Assert.That(subject.IsPendingSecret(handle), Is.True);
            Assert.That(subject.Resolve(handle, false), Is.EqualTo("real-secret"));
            Assert.That(subject.Resolve(handle, false), Is.EqualTo("real-secret"));
        }

        [Test]
        public void should_consume_handle_when_requested()
        {
            var subject = new PendingProviderSecretService(new CacheManager());

            var handle = subject.Create("real-secret");

            Assert.That(subject.Resolve(handle, true), Is.EqualTo("real-secret"));
            Assert.Throws<BadRequestException>(() => subject.Resolve(handle, true));
        }

        [Test]
        public void should_leave_plain_values_alone()
        {
            var subject = new PendingProviderSecretService(new CacheManager());

            Assert.That(subject.Resolve("plain-secret", true), Is.EqualTo("plain-secret"));
        }
    }
}
