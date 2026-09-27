using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;

namespace Chaptarr.Core.Test.Books
{
    // RefreshAuthor commands for different authors run concurrently on the singleton refresh services, so their
    // per-refresh state must be isolated per async flow (or safe to share).
    [TestFixture]
    public class RefreshSingletonPerRefreshStateFixture
    {
        private const BindingFlags NonPublicInstance = BindingFlags.NonPublic | BindingFlags.Instance;

        private sealed class TestableRefreshBookService : RefreshBookService
        {
            public TestableRefreshBookService()
                : base(null, null, null, null, null, null, null, null, null, null, null, null, null, null, LogManager.GetCurrentClassLogger())
            {
            }

            public Dictionary<string, Author> CacheOfCurrentRefresh => _bookMetadataCache;
        }

        private static T Uninitialized<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

        // Deterministic interleaving: flow A writes, then flow B reads, then flow A reads again.
        private static (T SeenByB, T SeenByAAfter) Interleave<T>(Action writeInA, Func<T> read)
        {
            using var aWrote = new ManualResetEventSlim();
            using var bRead = new ManualResetEventSlim();
            T seenByB = default;
            T seenByAAfter = default;

            var a = Task.Run(() =>
            {
                writeInA();
                aWrote.Set();
                bRead.Wait();
                seenByAAfter = read();
            });
            var b = Task.Run(() =>
            {
                aWrote.Wait();
                seenByB = read();
                bRead.Set();
            });

            Task.WaitAll(a, b);
            return (seenByB, seenByAAfter);
        }

        [Test]
        public void book_metadata_cache_should_not_be_shared_between_concurrent_refreshes()
        {
            var service = new TestableRefreshBookService();

            var (seenByB, seenByAAfter) = Interleave(
                () => service.CacheOfCurrentRefresh["gr:1:audiobook"] = new Author { Id = 1 },
                () => service.CacheOfCurrentRefresh.ContainsKey("gr:1:audiobook"));

            Assert.That(seenByB, Is.False, "refresh B must not see refresh A's cached metadata");
            Assert.That(seenByAAfter, Is.True, "refresh A keeps its own cache");
        }

        [Test]
        public void rehome_blueprint_should_not_be_shared_between_concurrent_author_refreshes()
        {
            var service = Uninitialized<RefreshAuthorService>();
            var property = typeof(RefreshAuthorService).GetProperty("_authorRefreshRehomeBlueprint", NonPublicInstance);
            Assert.That(property, Is.Not.Null);
            var blueprintOfA = new List<Book> { new Book { Id = 1 } };

            var (seenByB, seenByAAfter) = Interleave(
                () => property.SetValue(service, blueprintOfA),
                () => (List<Book>)property.GetValue(service));

            Assert.That(seenByB, Is.Null, "refresh B must not see refresh A's snapshot");
            Assert.That(seenByAAfter, Is.SameAs(blueprintOfA), "refresh A keeps its own snapshot");
        }

        [Test]
        public void refresh_matching_indexes_should_always_belong_to_the_list_they_were_asked_for_under_contention()
        {
            // Stress regression guard for the one-entry memo: the check and the return used to read the shared field
            // twice, so a concurrent refresh could swap it in between and the caller got another list's index.
            var book = Uninitialized<RefreshBookService>();
            var author = Uninitialized<RefreshAuthorService>();
            var logger = LogManager.GetCurrentClassLogger();
            typeof(RefreshBookService).GetField("_logger", NonPublicInstance).SetValue(book, logger);
            typeof(RefreshAuthorService).GetField("_logger", NonPublicInstance).SetValue(author, logger);

            var editionMethod = typeof(RefreshBookService).GetMethod("GetEditionRefreshMatchingIndex", NonPublicInstance);
            var bookMethod = typeof(RefreshAuthorService).GetMethod("GetBookRefreshMatchingIndex", NonPublicInstance);

            var wrongEditions = 0;
            var wrongBooks = 0;
            var tasks = new List<Task>();

            for (var t = 0; t < 4; t++)
            {
                tasks.Add(Task.Run(() =>
                {
                    var mine = new List<Edition>();
                    for (var i = 0; i < 20000; i++)
                    {
                        var index = editionMethod.Invoke(book, new object[] { mine });
                        var source = index.GetType().GetProperty("Source", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).GetValue(index);
                        if (!ReferenceEquals(source, mine))
                        {
                            Interlocked.Increment(ref wrongEditions);
                        }
                    }
                }));
                tasks.Add(Task.Run(() =>
                {
                    var mine = new List<Book>();
                    for (var i = 0; i < 20000; i++)
                    {
                        var index = bookMethod.Invoke(author, new object[] { mine });
                        var source = index.GetType().GetProperty("Source", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).GetValue(index);
                        if (!ReferenceEquals(source, mine))
                        {
                            Interlocked.Increment(ref wrongBooks);
                        }
                    }
                }));
            }

            Task.WaitAll(tasks.ToArray());

            Assert.That(wrongEditions, Is.Zero);
            Assert.That(wrongBooks, Is.Zero);
        }
    }
}
