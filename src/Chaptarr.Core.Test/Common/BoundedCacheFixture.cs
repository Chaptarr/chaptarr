using System;
using System.Threading.Tasks;
using NUnit.Framework;
using NzbDrone.Common.Cache;

namespace Chaptarr.Core.Test.Common
{
    [TestFixture]
    public class BoundedCacheFixture
    {
        [Test]
        public void should_evict_only_the_least_recently_used_entry()
        {
            var cache = new BoundedCache<int>(2);
            cache.Set("a", 1);
            cache.Set("b", 2);
            Assert.That(cache.TryGetValue("a", out _), Is.True);
            cache.Set("c", 3);
            Assert.That(cache.TryGetValue("b", out _), Is.False);
            Assert.That(cache.TryGetValue("a", out var a), Is.True);
            Assert.That(a, Is.EqualTo(1));
            Assert.That(cache.TryGetValue("c", out _), Is.True);
            cache.Set("a", 4);
            Assert.That(cache.Count, Is.EqualTo(2));
        }

        [Test]
        public void reads_should_not_extend_absolute_expiry()
        {
            var now = DateTime.UtcNow;
            var cache = new BoundedCache<int>(2, utcNow: () => now);
            cache.Set("cover", 1, TimeSpan.FromMinutes(10));
            now = now.AddMinutes(9);
            Assert.That(cache.TryGetValue("cover", out _), Is.True);
            now = now.AddMinutes(1);
            Assert.That(cache.TryGetValue("cover", out _), Is.False);
            Assert.That(cache.Count, Is.Zero);
        }

        [Test]
        public void parallel_writes_should_remain_bounded()
        {
            var cache = new BoundedCache<int>(32);
            Parallel.For(0, 10000, i =>
            {
                cache.Set(i.ToString(), i);
                cache.TryGetValue((i - 1).ToString(), out _);
            });
            Assert.That(cache.Count, Is.EqualTo(32));
        }
    }
}
