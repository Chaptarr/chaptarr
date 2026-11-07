using System;
using System.Collections;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using NzbDrone.Common.Cache;

namespace Chaptarr.Core.Test.Common
{
    [TestFixture]
    public class CachedFixture
    {
        private static int PendingExpiryCount(Cached<string> cache)
        {
            var field = typeof(Cached<string>).GetField("_pendingExpiry", BindingFlags.NonPublic | BindingFlags.Instance);
            var dictionary = (IDictionary)field.GetValue(cache);
            return dictionary.Count;
        }

        [Test]
        public void repeated_set_on_same_key_should_not_accumulate_pending_expiry_timers()
        {
            var cache = new Cached<string>();

            for (var i = 0; i < 50; i++)
            {
                cache.Set("key", "value" + i, TimeSpan.FromMinutes(5));
            }

            Assert.That(PendingExpiryCount(cache), Is.EqualTo(1));
            Assert.That(cache.Find("key"), Is.EqualTo("value49"));
        }

        [Test]
        public async Task item_should_still_expire_after_being_repeatedly_refreshed()
        {
            var cache = new Cached<string>();

            for (var i = 0; i < 10; i++)
            {
                cache.Set("key", "value", TimeSpan.FromMilliseconds(50));
            }

            await Task.Delay(TimeSpan.FromMilliseconds(300));

            Assert.That(cache.Find("key"), Is.Null);
            Assert.That(PendingExpiryCount(cache), Is.EqualTo(0));
        }

        [Test]
        public void parallel_refreshes_should_keep_one_expiry_and_clear_should_release_it()
        {
            var cache = new Cached<string>(TimeSpan.FromMinutes(5), rollingExpiry: true);
            Parallel.For(0, 2000, i =>
            {
                cache.Set("key", "value");
                cache.Find("key");
            });

            Assert.That(PendingExpiryCount(cache), Is.EqualTo(1));
            cache.Clear();
            Assert.That(PendingExpiryCount(cache), Is.Zero);
            Assert.That(cache.Count, Is.Zero);
        }

        [Test]
        public void removing_or_replacing_with_a_permanent_value_should_cancel_expiry()
        {
            var cache = new Cached<string>();
            cache.Set("key", "temporary", TimeSpan.FromMinutes(5));
            cache.Set("key", "permanent");
            Assert.That(PendingExpiryCount(cache), Is.Zero);
            Assert.That(cache.Find("key"), Is.EqualTo("permanent"));

            cache.Set("key", "temporary", TimeSpan.FromMinutes(5));
            cache.Remove("key");
            Assert.That(PendingExpiryCount(cache), Is.Zero);
        }
    }
}
