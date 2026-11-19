using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class MediaFileReplacementReplicaOwnershipFixture
    {
        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        public void replacement_should_detach_old_replica_cleanup_only_after_same_book_successor_commits(bool fail, bool otherBook)
        {
            const string replica = "/library/Author/Audio/book.epub";
            var old = new BookFile { Id = 10, EditionId = 3, Edition = new Edition { Id = 3, BookId = 2 },
                Path = "/library/Author/Ebook/book.epub", MediaType = "ebook", ReplicaPaths = new List<string> { replica } };
            var successor = new BookFile { EditionId = 4, Edition = new Edition { Id = 4, BookId = otherBook ? 5 : 2 },
                Path = "/library/Author/Ebook/book-new.epub", MediaType = "ebook", ReplicaPaths = new List<string> { replica } };
            var events = new List<BookFileDeletedEvent>();
            var repository = Proxy<IMediaFileRepository>((method, args) =>
            {
                if (method.Name == nameof(IMediaFileRepository.Get)) return new[] { old }.AsEnumerable();
                if (method.Name == nameof(IMediaFileRepository.ReplaceMany))
                {
                    Assert.That(old.ReplicaPaths, Is.EqualTo(new[] { replica }), "precommit evidence must survive rollback");
                    if (fail) throw new InvalidOperationException("database unavailable");
                    successor.Id = 11;
                    return null;
                }
                throw new NotImplementedException(method.Name);
            });
            var publisher = Proxy<IEventAggregator>((method, args) =>
            {
                if (args[0] is BookFileDeletedEvent deleted) events.Add(deleted);
                return null;
            });
            var service = new MediaFileService(repository, publisher, null, null, LogManager.GetCurrentClassLogger());
            void Replace() => service.ReplaceMany(new List<BookFile> { successor }, new List<BookFile> { old }, DeleteMediaFileReason.Upgrade);
            if (fail)
            {
                Assert.That(Replace, Throws.InvalidOperationException);
                Assert.That(old.ReplicaPaths, Is.EqualTo(new[] { replica }));
                Assert.That(events, Is.Empty);
            }
            else
            {
                Replace();
                Assert.That(events, Has.Count.EqualTo(1));
                Assert.That(events[0].BookFile.ReplicaPaths, Has.Count.EqualTo(otherBook ? 1 : 0));
                Assert.That(successor.ReplicaPaths, Is.EqualTo(new[] { replica }));
                Assert.That(events[0].Context.FindReplacement(successor.Edition.BookId, old.Path), Is.SameAs(successor));
            }
        }
    }
}
