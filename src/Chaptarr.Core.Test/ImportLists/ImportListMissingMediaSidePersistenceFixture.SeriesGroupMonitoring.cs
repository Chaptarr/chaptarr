using System.Linq;
using Chaptarr.Core.Test.Common;
using NUnit.Framework;
using NzbDrone.Core.Books;

namespace Chaptarr.Core.Test.ImportLists
{
    public partial class ImportListMissingMediaSidePersistenceFixture
    {
        [Test]
        public void monitoring_a_book_from_either_series_group_should_share_one_same_media_row(
            [Values(false, true)] bool postgres,
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType mediaType)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var stored = new StoredListLibrary(database);
                var links = new SeriesBookLinkRepository(database, stored.Events);
                var series = new SeriesRepository(database, stored.Events, links, stored.Logger);
                foreach (var side in new[] { BookMediaType.Audiobook, BookMediaType.Ebook })
                {
                    var book = CatalogBook(side, "Shared Work", "123");
                    book.SetMonitored(true);
                    stored.Save(book);
                    foreach (var (title, id, position) in new[] { ("First Group", "gr:49075", "1"), ("Second Group", "gr:135117", "5") })
                    {
                        var group = series.Insert(new Series { Title = title, GoodreadsSeriesId = id, MediaType = side });
                        links.Insert(new SeriesBookLink { SeriesId = group.Id, BookId = book.Id, Position = position });
                    }
                }
                var initial = series.GetByAuthorId(stored.Author.Id);
                var selectedGroups = initial.Where(group => group.MediaType == mediaType).OrderBy(group => group.Title).ToList();
                Assert.Multiple(() =>
                {
                    Assert.That(initial, Has.Count.EqualTo(4));
                    Assert.That(initial.Select(group => group.LinkItems.Count), Is.All.EqualTo(1));
                    Assert.That(selectedGroups, Has.Count.EqualTo(2));
                    Assert.That(selectedGroups.SelectMany(group => group.LinkItems).Select(link => link.BookId).Distinct().ToList(), Has.Count.EqualTo(1));
                    Assert.That(initial.Where(group => group.MediaType != mediaType).SelectMany(group => group.LinkItems)
                        .Select(link => link.BookId).Distinct().ToList(), Has.Count.EqualTo(1));
                });
                var selectedBookId = selectedGroups[0].LinkItems.Single().BookId;
                var memberships = initial.SelectMany(group => group.LinkItems.Select(link => (group.Id, link.BookId, link.Position))).ToList();
                Assert.That(initial.Where(group => group.MediaType != mediaType).SelectMany(group => group.LinkItems)
                    .Select(link => link.BookId), Does.Not.Contain(selectedBookId));

                stored.Books.SetMonitored(new[] { selectedGroups[0].LinkItems.Single().BookId }, false);

                var unmonitored = series.GetByAuthorId(stored.Author.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(unmonitored.SelectMany(group => group.LinkItems.Select(link => (group.Id, link.BookId, link.Position))), Is.EquivalentTo(memberships));
                    Assert.That(unmonitored.Where(group => group.MediaType == mediaType).SelectMany(group => group.LinkItems)
                        .Select(link => link.Book.Value.IsMonitored()), Is.All.False);
                    Assert.That(unmonitored.Where(group => group.MediaType != mediaType).SelectMany(group => group.LinkItems)
                        .Select(link => link.Book.Value.IsMonitored()), Is.All.True);
                    Assert.That(stored.BookRows.All(), Has.Count.EqualTo(2));
                });
                var secondGroup = unmonitored.Single(group => group.MediaType == mediaType && group.Title == "Second Group");
                stored.Books.SetMonitored(new[] { secondGroup.LinkItems.Single().BookId }, true);

                var monitored = series.GetByAuthorId(stored.Author.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(monitored.SelectMany(group => group.LinkItems.Select(link => (group.Id, link.BookId, link.Position))), Is.EquivalentTo(memberships));
                    Assert.That(monitored.SelectMany(group => group.LinkItems).Select(link => link.Book.Value.IsMonitored()), Is.All.True);
                    Assert.That(monitored.Where(group => group.MediaType == mediaType).SelectMany(group => group.LinkItems)
                        .Select(link => link.BookId).Distinct(), Is.EqualTo(new[] { selectedBookId }));
                    Assert.That(stored.BookRows.All(), Has.Count.EqualTo(2));
                    Assert.That(stored.Commands, Is.Empty);
                });
            });
        }
    }
}
