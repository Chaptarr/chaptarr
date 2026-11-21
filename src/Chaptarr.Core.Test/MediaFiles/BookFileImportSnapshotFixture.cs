using System;
using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class BookFileImportSnapshotFixture
    {
        private sealed class UnavailableRelationship<T> : LazyLoaded<T>
        {
            public override void LazyLoad() => throw new InvalidOperationException("Snapshot must not load relationships");
        }

        [Test]
        public void import_snapshot_must_not_traverse_or_load_book_relationships()
        {
            var edition = new Edition { Id = 10 };
            var book = new Book { Id = 1, Editions = new() { edition } };
            edition.Book = book;
            var original = new BookFile { Id = 20, EditionId = edition.Id, Edition = edition };
            edition.BookFiles = new() { original };
            book.BookFiles = new() { original };

            var snapshot = original.CloneForImport();
            Assert.That(snapshot, Is.Not.SameAs(original));
            Assert.That(snapshot.Edition, Is.SameAs(edition));
            Assert.That(snapshot.Id, Is.EqualTo(original.Id));

            original.LazyAuthor = new UnavailableRelationship<Author>();
            original.LazyEdition = new UnavailableRelationship<Edition>();
            Assert.DoesNotThrow(() => snapshot = original.CloneForImport());
            Assert.That(snapshot.LazyAuthor, Is.SameAs(original.LazyAuthor));
            Assert.That(snapshot.LazyEdition, Is.SameAs(original.LazyEdition));
        }

        [Test]
        public void import_preparation_must_not_mutate_stored_embedded_values()
        {
            var quality = new QualityModel(Quality.MP3);
            var tags = new Dictionary<string, List<string>> { ["title"] = new() { "Stored title" } };
            var original = new BookFile { Id = 20, Path = "/library/stored.mp3", EditionId = 10,
                MediaInfo = new MediaInfoModel { AudioFormat = "MP3", Duration = TimeSpan.Zero },
                MatchProvenance = new MatchProvenance { ImportSourcePaths = new() { "/download/stored.mp3" } },
                ReplicaPaths = new() { "/replica/stored.mp3" }, Quality = quality, AllTags = tags };
            var snapshot = original.CloneForImport();
            snapshot.Path = "/library/replacement.m4b";
            snapshot.EditionId = 11;
            snapshot.Quality = new QualityModel(Quality.M4B);
            MediaDuration.ApplyToMediaInfo(snapshot.MediaInfo, 120);
            snapshot.MatchProvenance.ImportSourcePaths.Add("/download/new.mp3");
            snapshot.ReplicaPaths.Add("/replica/new.mp3");

            Assert.That(original.Path, Is.EqualTo("/library/stored.mp3"));
            Assert.That(original.EditionId, Is.EqualTo(10));
            Assert.That(original.Quality, Is.SameAs(quality));
            Assert.That(original.MediaInfo.Duration, Is.EqualTo(TimeSpan.Zero));
            Assert.That(snapshot.MediaInfo.Duration, Is.EqualTo(TimeSpan.FromSeconds(120)));
            Assert.That(original.MatchProvenance.ImportSourcePaths, Has.Count.EqualTo(1));
            Assert.That(original.ReplicaPaths, Has.Count.EqualTo(1));
            Assert.That(snapshot.AllTags, Is.SameAs(tags), "Import preparation replaces or reads tag data; tags are written only after commit");
        }
    }
}
