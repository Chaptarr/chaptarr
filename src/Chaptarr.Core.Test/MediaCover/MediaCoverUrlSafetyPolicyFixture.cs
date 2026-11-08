using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using NzbDrone.Core.MediaCover;

namespace Chaptarr.Core.Test.MediaCover
{
    [TestFixture]
    public class MediaCoverUrlSafetyPolicyFixture
    {
        [TestCase("1.1.1.1", true)]
        [TestCase("127.0.0.1", false)]
        [TestCase("192.168.1.1", false)]
        [TestCase("::1", false)]
        public void resolved_addresses_keep_the_existing_private_address_boundary(string address, bool allowed)
        {
            var calls = 0;
            var result = MediaCoverUrlSafetyPolicy.TryGetSafeImageUrl("https://cover.example/image", null, out var safe, out var reason,
                resolve: (host, token) =>
                {
                    calls++;
                    Assert.That(host, Is.EqualTo("cover.example"));
                    Assert.That(token.CanBeCanceled, Is.True);
                    return Task.FromResult(new[] { IPAddress.Parse(address) });
                });
            Assert.That(result, Is.EqualTo(allowed));
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(safe, allowed ? Is.EqualTo("https://cover.example/image") : Is.Null);
            Assert.That(reason, allowed ? Is.Null : Is.EqualTo("private_or_local_address"));
        }

        [Test]
        public void unresolved_host_fails_closed_without_waiting_forever()
        {
            var neverCompletes = new TaskCompletionSource<IPAddress[]>();
            CancellationToken resolverToken = default;
            var watch = Stopwatch.StartNew();
            var safe = MediaCoverUrlSafetyPolicy.TryGetSafeImageUrl("https://cover.example/image", null, out var url, out var reason,
                resolve: (_, token) => { resolverToken = token; return neverCompletes.Task; });
            Assert.That(safe, Is.False);
            Assert.That(url, Is.Null);
            Assert.That(reason, Is.EqualTo("dns_lookup_timed_out"));
            Assert.That(resolverToken.IsCancellationRequested, Is.True);
            Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(10)));
        }

        [Test]
        public void caller_cancellation_is_propagated_instead_of_becoming_an_unsafe_url_verdict()
        {
            using var cancelled = new CancellationTokenSource();
            Assert.Catch<OperationCanceledException>(() => MediaCoverUrlSafetyPolicy.TryGetSafeImageUrl(
                "https://cover.example/image", null, out _, out _, cancelled.Token,
                (_, token) => { cancelled.Cancel(); return Task.FromCanceled<IPAddress[]>(token); }));
        }

        [Test]
        public void dns_failure_and_no_records_are_distinct()
        {
            Assert.That(MediaCoverUrlSafetyPolicy.TryGetSafeImageUrl("https://cover.example/image", null, out _, out var failure,
                resolve: (_, _) => Task.FromException<IPAddress[]>(new SocketException())), Is.False);
            Assert.That(failure, Is.EqualTo("dns_lookup_failed"));
            Assert.That(MediaCoverUrlSafetyPolicy.TryGetSafeImageUrl("https://cover.example/image", null, out _, out var missing,
                resolve: (_, _) => Task.FromResult(Array.Empty<IPAddress>())), Is.False);
            Assert.That(missing, Is.EqualTo("dns_no_records"));
        }
    }
}
