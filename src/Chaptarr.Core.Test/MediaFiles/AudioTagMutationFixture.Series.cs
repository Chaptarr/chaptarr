using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Chaptarr.Core.Test.Common;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.RootFolders;
using static Chaptarr.Core.Test.Common.InterfaceProxy;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.TagExtraction;
using NzbDrone.Core.MediaFiles.BookImport.Services;
using TagLib;
using File = System.IO.File;

namespace Chaptarr.Core.Test.MediaFiles
{
    public partial class AudioTagMutationFixture
    {
        [TestCase(false)]
        [TestCase(true)]
        public void header_refresh_resolves_ambiguous_series_through_sync_to_the_file(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var file = Add(".m4b");
                var logger = _logs.GetLogger("HeaderRefresh");
                var events = Proxy<IEventAggregator>((_, _) => null);
                var authorRows = new AuthorRepository(database, events, logger);
                var author = authorRows.Insert(new Author { Name = "Fixture author", CleanName = "fixtureauthor", TitleSlug = "fixture-author",
                    HardcoverAuthorId = "hc:9001", Path = _directory, AudiobookRootFolderPath = _directory, Added = DateTime.UtcNow });
                var authors = Proxy<IAuthorService>((method, args) => method.Name switch
                {
                    nameof(IAuthorService.GetAuthor) => authorRows.Get((int)args[0]),
                    nameof(IAuthorService.FindByProviderId) => authorRows.Get(author.Id),
                    nameof(IAuthorService.GetAuthors) or nameof(IAuthorService.GetExistingAuthors) => authorRows.Get((IEnumerable<int>)args[0]).ToList(),
                    _ => throw new AssertionException(method.Name)
                });
                var roots = Proxy<IRootFolderService>((method, _) => method.Name == nameof(IRootFolderService.All)
                    ? new List<RootFolder> { new() { Path = _directory, FolderType = FolderType.Audiobook } } : throw new AssertionException(method.Name));
                var bookRows = new BookRepository(database, events);
                var book = bookRows.Insert(new Book { AuthorId = author.Id, Title = "A Clash of Kings", CleanTitle = "aclashofkings", TitleSlug = "a-clash-of-kings",
                    HardcoverBookId = "hc:1001", MediaType = BookMediaType.Audiobook, AnyEditionOk = true, Added = DateTime.UtcNow });
                var editionRows = new EditionRepository(database, events);
                var edition = editionRows.Insert(new Edition { BookId = book.Id, Title = book.Title, TitleSlug = "clash-audio", ForeignEditionId = "hc:edition:2001-audiobook",
                    HardcoverEditionId = "2001", ReadingFormatId = EditionReadingFormat.Audiobook, Monitored = true,
                    Publisher = "Fixture publisher", NarratorNames = new List<string> { "Fixture narrator" }, ReleaseDate = new DateTime(2024, 1, 1) });
                var linkRows = new SeriesBookLinkRepository(database, events);
                var seriesRows = new SeriesRepository(database, events, linkRows, logger);
                foreach (var (id, title) in new[] { (43790, "A Song of Ice and Fire"), (9000, "A second original series") })
                {
                    var series = seriesRows.Insert(new Series { GoodreadsSeriesId = "gr:" + id, Title = title, MediaType = BookMediaType.Audiobook });
                    linkRows.Insert(new SeriesBookLink { BookId = book.Id, SeriesId = series.Id, Position = "2", SeriesPosition = 2 });
                }
                var editions = new EditionService(editionRows, events, logger);
                var books = new BookService(bookRows, editions, events, authors, _media, roots, linkRows, null, logger);
                file.Author = author;
                file.EditionId = edition.Id;
                file.Edition = edition;
                edition.Book = books.GetBook(book.Id);
                _seriesRead = linkRows.GetLinksByBook;
                using (var audio = TagLib.File.Create(file.Path))
                {
                    var apple = (TagLib.Mpeg4.AppleTag)audio.GetTag(TagTypes.Apple);
                    apple.SetDashBox("com.pilabor.tone", "SERIES", "Stored series");
                    apple.SetDashBox("com.pilabor.tone", "PART", "7");
                    audio.Save();
                }
                _service.WriteTags(file, false);
                Assert.That(BookSeries.AudioTagPair(edition.Book).Title, Is.Null, "Both originals are ambiguous before the header arrives");
                Assert.That(new TagLibSharpExtractor().ExtractTags(file.Path)["MP4:----:com.pilabor.tone:SERIES"], Is.EqualTo(new[] { "Stored series" }));
                var metadata = new MetadataTagService(_service, Proxy<IEBookTagService>((method, args) =>
                {
                    Assert.That(method.Name, Is.EqualTo(nameof(IEBookTagService.SyncTags)));
                    Assert.That(((List<Edition>)args[0]).All(item => item.Book.MediaType == BookMediaType.Audiobook), Is.True);
                    return null;
                }), logger, null, Proxy<ICommandResultReporter>((_, _) => null), _media, authors);
                var refresh = new RefreshBookService(books, authors, roots, editions, null, null,
                    new RefreshEditionService(editions, metadata, null, database, logger), _media, null, events, null,
                    new EditionSelector(logger), null, null, logger, database);
                var remote = RefreshEntityCopy.CloneBook(books.GetBook(book.Id), includeEditions: true);
                refresh.RefreshBookInfo(book, new List<Book> { remote }, author, false);
                _events.Clear();
                remote = RefreshEntityCopy.CloneBook(books.GetBook(book.Id), includeEditions: true);
                refresh.RefreshBookInfo(book, new List<Book> { remote }, author, false);
                Assert.That(_events, Is.Empty, "Unchanged editions must not mask a missing series-only trigger");
                remote.SeriesId = 43790;
                refresh.RefreshBookInfo(book, new List<Book> { remote }, author, false);
                Assert.That(bookRows.Get(book.Id).SeriesId, Is.EqualTo(43790));
                var tags = new TagLibSharpExtractor().ExtractTags(file.Path);
                Assert.That(tags["MP4:----:com.pilabor.tone:SERIES"], Is.EqualTo(new[] { "A Song of Ice and Fire" }));
                Assert.That(tags["MP4:----:com.pilabor.tone:PART"], Is.EqualTo(new[] { "2" }));
                Assert.That(_events, Has.Count.EqualTo(1));
                var bytes = File.ReadAllBytes(file.Path);
                refresh.RefreshBookInfo(book, new List<Book> { remote }, author, false);
                Assert.That(_events, Has.Count.EqualTo(1));
                Assert.That(File.ReadAllBytes(file.Path), Is.EqualTo(bytes));
                Assert.That(_warnings.Logs, Is.Empty);
            });
        }

        [Test]
        public void sync_loads_series_for_all_books_once_before_any_file_write()
        {
            var files = new[] { Add(".mp3"), Add(".m4b") };
            for (var i = 0; i < files.Length; i++)
            {
                files[i].Edition.Book.Id = i + 10;
                files[i].Edition.Id = i + 20;
                files[i].EditionId = i + 20;
                files[i].Edition.BookId = i + 10;
                files[i].Edition.Book.LazySeriesLinks = new NzbDrone.Core.Datastore.LazyLoaded<List<SeriesBookLink>>();
            }
            var calls = 0;
            _seriesRead = ids =>
            {
                calls++;
                Assert.That(ids, Is.EquivalentTo(new[] { 10, 11 }), "Sync must preload the whole batch, not one file at a time");
                return ids.Select(id => new SeriesBookLink { BookId = id, Position = "2", Series = new Series { Title = "Series", GoodreadsSeriesId = "gr:1" } }).ToList();
            };
            _service.SyncTags(files.Select(file => file.Edition).ToList());
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(_events, Has.Count.EqualTo(2));
            Assert.That(_warnings.Logs, Is.Empty);
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void tone_publisher_stays_in_its_namespace_and_second_write_is_a_noop(bool scrub, bool change)
        {
            var file = Add(".m4b");
            using (var audio = TagLib.File.Create(file.Path))
            {
                var apple = (TagLib.Mpeg4.AppleTag)audio.GetTag(TagTypes.Apple);
                while (AppleTagFields.Read(apple).Any(field => field.Mean == "com.apple.iTunes" && field.Name == "publisher"))
                    apple.SetDashBox("com.apple.iTunes", "publisher", null);
                apple.SetDashBox("com.pilabor.tone", "PUBLISHER", file.Edition.Publisher);
                audio.Save();
            }
            var preview = _service.GetRetagPreviewsByAuthor(file.Author.Id).Single();
            Assert.That(preview.Changes.ContainsKey("PUBLISHER"), Is.False, "Existing tone publisher is already correct");
            if (change) file.Edition.Publisher = "Changed publisher";
            _scrub = scrub;
            _service.WriteTags(file, false);
            using (var audio = TagLib.File.Create(file.Path))
            {
                var apple = (TagLib.Mpeg4.AppleTag)audio.GetTag(TagTypes.Apple);
                Assert.That(AppleTagFields.Read(apple).Where(field => string.Equals(field.Name, "publisher", StringComparison.OrdinalIgnoreCase)),
                    Is.EquivalentTo(new[] { ("com.pilabor.tone", "PUBLISHER", file.Edition.Publisher) }));
            }
            var bytes = File.ReadAllBytes(file.Path);
            _service.WriteTags(file, false);
            Assert.That(File.ReadAllBytes(file.Path), Is.EqualTo(bytes));
            Assert.That(_events, Has.Count.EqualTo(1));
            Assert.That(_warnings.Logs, Is.Empty);
        }

        [Test]
        public void changing_tone_publisher_removes_the_competing_itunes_box()
        {
            var file = Add(".m4b");
            _scrub = false;
            using (var audio = TagLib.File.Create(file.Path))
            {
                var apple = (TagLib.Mpeg4.AppleTag)audio.GetTag(TagTypes.Apple);
                apple.SetDashBox("com.pilabor.tone", "PUBLISHER", "Old tone publisher");
                apple.Publisher = "Competing iTunes publisher";
                audio.Save();
            }
            _service.WriteTags(file, false);
            using (var audio = TagLib.File.Create(file.Path))
            {
                var apple = (TagLib.Mpeg4.AppleTag)audio.GetTag(TagTypes.Apple);
                Assert.That(AppleTagFields.Read(apple).Where(field => string.Equals(field.Name, "publisher", StringComparison.OrdinalIgnoreCase)),
                    Is.EquivalentTo(new[] { ("com.pilabor.tone", "PUBLISHER", file.Edition.Publisher) }));
            }
            var bytes = File.ReadAllBytes(file.Path);
            _service.WriteTags(file, false);
            Assert.That(File.ReadAllBytes(file.Path), Is.EqualTo(bytes));
            Assert.That(_events, Has.Count.EqualTo(1));
            Assert.That(_warnings.Logs, Is.Empty);
        }

        [TestCaseSource(nameof(FormatCases))]
        public void written_series_round_trips_as_metadata_without_becoming_title_evidence(string extension, bool scrub)
        {
            var file = Add(extension);
            _scrub = scrub;
            file.Edition.Book.SeriesLinks = new List<SeriesBookLink> { SeriesLink("The Bound and The Broken Novella", "0.5 - story Bones") };
            _service.WriteTags(file, false);
            var tags = new TagLibSharpExtractor().ExtractTags(file.Path);
            TagCanonicalizer.AddCanonicalKeys(tags);
            Assert.That(tags["SERIES"], Is.EqualTo(new[] { "The Bound and The Broken Novella" }));
            Assert.That(tags["SERIES-PART"], Is.EqualTo(new[] { "0.5 - story Bones" }));
            var validator = new ContainmentValidator(new TagNormalizer(), _logs.GetLogger("SeriesMatching"));
            Assert.That(validator.GetEditionTitleEvidence("Bones", tags), Is.Empty);
            Assert.That(validator.GetEditionTitleEvidence("The Bound and The Broken Novella", tags), Is.Empty);
            Assert.That(_events, Has.Count.EqualTo(1));
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void tone_series_namespace_survives_scrub_and_updates_without_a_second_pair(bool scrub, bool authoritative)
        {
            var file = Add(".m4b");
            using (var audio = TagLib.File.Create(file.Path))
            {
                var apple = (TagLib.Mpeg4.AppleTag)audio.GetTag(TagTypes.Apple);
                apple.SetDashBox("com.pilabor.tone", "SERIES", "Divine Apostasy");
                apple.SetDashBox("com.pilabor.tone", "PART", "12");
                audio.Save();
            }
            file.Edition.Book.SeriesLinks = authoritative
                ? new List<SeriesBookLink> { SeriesLink("Confirmed series", "13") }
                : new List<SeriesBookLink> { SeriesLink("First candidate", "1"), SeriesLink("Other candidate", "2", "2") };
            _scrub = scrub;
            _service.WriteTags(file, false);
            using (var audio = TagLib.File.Create(file.Path))
            {
                var fields = AppleTagFields.Read((TagLib.Mpeg4.AppleTag)audio.GetTag(TagTypes.Apple)).Where(f => AppleTagFields.SeriesKey(f.Mean, f.Name) != null).ToList();
                Assert.That(fields, Is.EquivalentTo(new[] {
                    ("com.pilabor.tone", "SERIES", authoritative ? "Confirmed series" : "Divine Apostasy"),
                    ("com.pilabor.tone", "PART", authoritative ? "13" : "12") }));
            }
            var bytes = File.ReadAllBytes(file.Path);
            _service.WriteTags(file, false);
            Assert.That(File.ReadAllBytes(file.Path), Is.EqualTo(bytes));
            Assert.That(_events, Has.Count.EqualTo(1));
            Assert.That(_warnings.Logs, Is.Empty);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void conflicting_tone_and_itunes_series_require_authority_before_scrub(bool authoritative)
        {
            var file = Add(".m4b");
            using (var audio = TagLib.File.Create(file.Path))
            {
                var apple = (TagLib.Mpeg4.AppleTag)audio.GetTag(TagTypes.Apple);
                apple.SetDashBox("com.pilabor.tone", "SERIES", "Tone series");
                apple.SetDashBox("com.pilabor.tone", "PART", "12");
                apple.SetDashBox("com.apple.iTunes", "SERIES", "iTunes series");
                apple.SetDashBox("com.apple.iTunes", "SERIES-PART", "2");
                audio.Save();
            }
            if (authoritative) file.Edition.Book.SeriesLinks = new List<SeriesBookLink> { SeriesLink("Confirmed series", "13") };
            _scrub = true;
            var before = File.ReadAllBytes(file.Path);
            _service.WriteTags(file, false);
            if (!authoritative)
            {
                Assert.That(File.ReadAllBytes(file.Path), Is.EqualTo(before));
                Assert.That(_events, Is.Empty);
                Assert.That(_warnings.Logs.Any(line => line.Contains("Conflicting stored SERIES-PART values")), Is.True);
            }
            else
            {
                using var audio = TagLib.File.Create(file.Path);
                var fields = AppleTagFields.Read((TagLib.Mpeg4.AppleTag)audio.GetTag(TagTypes.Apple)).Where(f => AppleTagFields.SeriesKey(f.Mean, f.Name) != null).ToList();
                Assert.That(fields, Is.EquivalentTo(new[] { ("com.pilabor.tone", "SERIES", "Confirmed series"), ("com.pilabor.tone", "PART", "13") }));
            }
        }

        [Test]
        public void tone_series_is_named_metadata_but_unrelated_custom_fields_remain_title_evidence()
        {
            var file = Add(".m4b");
            using (var audio = TagLib.File.Create(file.Path))
            {
                var apple = (TagLib.Mpeg4.AppleTag)audio.GetTag(TagTypes.Apple);
                apple.SetDashBox("com.pilabor.tone", "SERIES", "Divine Apostasy");
                apple.SetDashBox("com.pilabor.tone", "PART", "12 - story Bones");
                apple.SetDashBox("custom", "PART", "Other title");
                audio.Save();
            }
            var tags = new TagLibSharpExtractor().ExtractTags(file.Path);
            TagCanonicalizer.AddCanonicalKeys(tags);
            Assert.That(tags["SERIES"], Is.EqualTo(new[] { "Divine Apostasy" }));
            Assert.That(tags["SERIES-PART"], Is.EqualTo(new[] { "12 - story Bones" }));
            Assert.That(tags["MP4:----"], Is.EqualTo(new[] { "Other title" }));
            var validator = new ContainmentValidator(new TagNormalizer(), _logs.GetLogger("SeriesMatching"));
            Assert.That(validator.GetEditionTitleEvidence("Bones", tags), Is.Empty);
            Assert.That(validator.GetEditionTitleEvidence("Divine Apostasy", tags), Is.Empty);
            Assert.That(validator.GetEditionTitleEvidence("Other title", tags), Is.Not.Empty);
        }

        private static SeriesBookLink SeriesLink(string name, string position, string id = "1", bool? primary = true, BookMediaType media = BookMediaType.Audiobook)
        {
            return new SeriesBookLink { Position = position, IsPrimary = primary, Series = new Series { Title = name, GoodreadsSeriesId = id, MediaType = media } };
        }

        private static string StoredSeries(string path, string key)
        {
            using var file = TagLib.File.Create(path);
            if (file.GetTag(TagTypes.Apple) is TagLib.Mpeg4.AppleTag apple) return apple.GetDashBox("com.apple.iTunes", key);
            if (file.GetTag(TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph) return xiph.GetFirstField(key);
            if (file.GetTag(TagTypes.Asf) is TagLib.Asf.Tag asf) return asf.GetDescriptorString(key);
            if (file.GetTag(TagTypes.Id3v2) is TagLib.Id3v2.Tag id3) return string.Join("; ", TagLib.Id3v2.UserTextInformationFrame.Get(id3, key, false)?.Text ?? Array.Empty<string>());
            if (file.GetTag(TagTypes.Ape) is TagLib.Ape.Tag ape) return ape.GetItem(key)?.ToString();
            throw new AssertionException("No writable tag container");
        }

        [TestCaseSource(nameof(FormatCases))]
        public void series_only_change_should_match_preview_preserve_download_and_then_be_noop(string extension, bool scrub)
        {
            var file = Add(extension, 23, 45);
            _service.WriteTags(file, false);
            _events.Clear();
            _guards = 0;
            _scrub = scrub;
            file.Edition.Book.SeriesLinks = new List<SeriesBookLink> { SeriesLink("星の物語; 第二部", "2.5–3") };
            if (MediaFileExtensions.CanWriteAudioTags(extension))
            {
                var preview = _service.GetRetagPreviewsByBook(2).Single();
                Assert.That(preview.Changes.Keys, Is.EquivalentTo(new[] { "SERIES", "SERIES-PART" }));
                Assert.That(preview.Changes["SERIES"].Item2, Is.EqualTo("星の物語; 第二部"));
                Assert.That(preview.Changes["SERIES-PART"].Item2, Is.EqualTo("2.5–3"));
            }
            var download = Path.Combine(_directory, "download" + extension);
            if (!_disk.TryCreateHardLink(file.Path, download)) Assert.Ignore("Filesystem does not support hardlinks");
            var original = File.ReadAllBytes(download);
            _service.WriteTags(file, false);
            Assert.That(File.ReadAllBytes(download), Is.EqualTo(original));
            Assert.That(StoredSeries(file.Path, "SERIES"), Is.EqualTo("星の物語; 第二部"));
            Assert.That(StoredSeries(file.Path, "SERIES-PART"), Is.EqualTo("2.5–3"));
            using (var tags = TagLib.File.Create(file.Path))
            {
                Assert.That(tags.Tag.Album, Is.EqualTo("Fixture book"));
                Assert.That(tags.Tag.Track, Is.EqualTo(23));
                Assert.That(tags.Tag.TrackCount, Is.EqualTo(45));
            }

            File.Delete(download);
            Assert.That(_disk.TryCreateHardLink(file.Path, download), Is.True);
            var updated = File.ReadAllBytes(file.Path);
            _service.WriteTags(file, false);
            Assert.That(_service.GetRetagPreviewsByBook(2), Is.Empty);
            Assert.That(File.ReadAllBytes(file.Path), Is.EqualTo(updated));
            Assert.That(_disk.GetFileLinkCount(file.Path), Is.EqualTo(2));
            Assert.That(_guards, Is.EqualTo(1));
            Assert.That(_events, Has.Count.EqualTo(1));
            Assert.That(_warnings.Logs, Is.Empty);
        }

        [TestCase(1, "A Song of Ice and Fire", "2")]
        [TestCase(2, "A Song of Ice and Fire (1-in-2)", "3")]
        public void series_tags_should_use_the_paired_primary_membership_and_header_when_known(int? header, string name, string position)
        {
            var file = Add(".m4b");
            file.Edition.Book.SeriesId = header;
            file.Edition.Book.SeriesLinks = new List<SeriesBookLink>
            {
                SeriesLink("A Song of Ice and Fire", "2", "1"),
                SeriesLink("A Song of Ice and Fire (1-in-2)", "3", "2"),
                SeriesLink("Ebook membership", "0", "3", true, BookMediaType.Ebook)
            };
            _service.WriteTags(file, false);
            Assert.That(StoredSeries(file.Path, "SERIES"), Is.EqualTo(name));
            Assert.That(StoredSeries(file.Path, "SERIES-PART"), Is.EqualTo(position));
        }

        [TestCase("0")]
        [TestCase("4.5")]
        [TestCase("1-2")]
        [TestCase("1, 3, 5")]
        public void series_position_should_be_preserved_verbatim(string position)
        {
            var file = Add();
            file.Edition.Book.SeriesLinks = new List<SeriesBookLink> { SeriesLink("Series", position) };
            _service.WriteTags(file, false);
            Assert.That(StoredSeries(file.Path, "SERIES-PART"), Is.EqualTo(position));
        }

        [TestCase(3, "Aliens / Predator / Prometheus Universe", "2")]
        [TestCase(4, "Aliens / Predator / Prometheus Universe", "2")]
        [TestCase(3, "Doc Savage (publication order; no omnibus)", "2")]
        [TestCase(4, "Doc Savage (publication order; no omnibus)", "2")]
        [TestCase(3, "Series", "1-3; 1.5")]
        [TestCase(4, "Series", "1-3; 1.5")]
        [TestCase(3, "Series", "1/2")]
        [TestCase(4, "Series", "1/2")]
        public void id3_series_separators_should_roundtrip_without_repeated_writes(byte version, string name, string position)
        {
            var file = Add();
            using (var audio = TagLib.File.Create(file.Path))
            {
                ((TagLib.Id3v2.Tag)audio.GetTag(TagTypes.Id3v2, true)).Version = version;
                audio.Save();
            }

            file.Edition.Book.SeriesLinks = new List<SeriesBookLink> { SeriesLink(name, position) };
            _service.WriteTags(file, false);
            Assert.That(StoredSeries(file.Path, "SERIES"), Is.EqualTo(name));
            Assert.That(StoredSeries(file.Path, "SERIES-PART"), Is.EqualTo(position));
            var written = File.ReadAllBytes(file.Path);
            _service.WriteTags(file, false);
            Assert.That(_service.GetRetagPreviewsByBook(2), Is.Empty);
            Assert.That(File.ReadAllBytes(file.Path), Is.EqualTo(written));
            Assert.That(_events, Has.Count.EqualTo(1));
            using var reopened = TagLib.File.Create(file.Path);
            Assert.That(((TagLib.Id3v2.Tag)reopened.GetTag(TagTypes.Id3v2)).Version, Is.EqualTo(version));
        }

        [Test]
        public void header_should_choose_the_attested_series_from_real_multilingual_memberships()
        {
            var file = Add(".m4b");
            file.Edition.Book.SeriesId = 43790;
            file.Edition.Book.SeriesLinks = new List<SeriesBookLink>
            {
                SeriesLink("A Song of Ice and Fire", "2", "gr:43790"),
                SeriesLink("A Song of Ice and Fire (1-in-2)", "3", "gr:105522"),
                SeriesLink("שיר של אש ושל קרח", "2 חלק א'", "gr:418107"),
                SeriesLink("نغمه آتش و یخ جلد دوم", "2", "gr:199405"),
                SeriesLink("Le cronache del ghiaccio e del fuoco", "3", "gr:104861"),
                SeriesLink("Pjesma leda i vatre", "2.1", "gr:151038")
            };
            _service.WriteTags(file, false);
            Assert.That(StoredSeries(file.Path, "SERIES"), Is.EqualTo("A Song of Ice and Fire"));
            Assert.That(StoredSeries(file.Path, "SERIES-PART"), Is.EqualTo("2"));
            file.Edition.Book.SeriesId = null;
            file.Edition.Publisher = "Updated publisher";
            _scrub = true;
            _service.WriteTags(file, false);
            Assert.That(StoredSeries(file.Path, "SERIES"), Is.EqualTo("A Song of Ice and Fire"));
            Assert.That(StoredSeries(file.Path, "SERIES-PART"), Is.EqualTo("2"));
            Assert.That(_events, Has.Count.EqualTo(2));
        }

        private static void SetSeriesTag(BookFile file, string key, string value, bool latin1 = false)
        {
            using var audio = TagLib.File.Create(file.Path);
            if (audio.GetTag(TagTypes.Apple) is TagLib.Mpeg4.AppleTag apple) apple.SetDashBox("com.apple.iTunes", key, value);
            else if (audio.GetTag(TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph) xiph.SetField(key, value);
            else if (audio.GetTag(TagTypes.Asf) is TagLib.Asf.Tag asf) asf.SetDescriptorString(value, key);
            else if (audio.GetTag(TagTypes.Id3v2) is TagLib.Id3v2.Tag id3)
            {
                var frame = TagLib.Id3v2.UserTextInformationFrame.Get(id3, key, true);
                if (latin1) frame.TextEncoding = StringType.Latin1;
                frame.Text = new[] { value };
            }
            else if (audio.GetTag(TagTypes.Ape) is TagLib.Ape.Tag ape) ape.SetValue(key, value);
            audio.Save();
        }

        [TestCaseSource(nameof(FormatCases))]
        public void lowercase_stored_series_should_survive_when_catalog_is_ambiguous(string extension, bool scrub)
        {
            var file = Add(extension);
            SetSeriesTag(file, "series", "Known series");
            SetSeriesTag(file, "series-part", "7");
            file.Edition.Book.SeriesLinks = new List<SeriesBookLink> { SeriesLink("First candidate", "1"), SeriesLink("Other candidate", "2", "2") };
            _scrub = scrub;
            _service.WriteTags(file, false);
            var values = extension is ".mp3" or ".m4b" or ".wma"
                ? new[] { StoredSeries(file.Path, "series"), StoredSeries(file.Path, "SERIES") }
                : new[] { StoredSeries(file.Path, "SERIES") };
            Assert.That(values, Does.Contain("Known series"));
            Assert.That(new[] { StoredSeries(file.Path, "series-part"), StoredSeries(file.Path, "SERIES-PART") }, Does.Contain("7"));
            var bytes = File.ReadAllBytes(file.Path);
            _service.WriteTags(file, false);
            Assert.That(File.ReadAllBytes(file.Path), Is.EqualTo(bytes));
            Assert.That(_events, Has.Count.EqualTo(1));
            Assert.That(_warnings.Logs, Is.Empty);
        }

        [TestCase(".mp3")]
        [TestCase(".m4b")]
        [TestCase(".wma")]
        public void scrub_must_not_merge_conflicting_stored_series_without_authority(string extension)
        {
            var file = Add(extension);
            SetSeriesTag(file, "series", "First series");
            SetSeriesTag(file, "SERIES", "Other series");
            _scrub = true;
            var before = File.ReadAllBytes(file.Path);
            _service.WriteTags(file, false);
            Assert.That(File.ReadAllBytes(file.Path), Is.EqualTo(before));
            Assert.That(_events, Is.Empty);
            Assert.That(_guards, Is.Zero);
            Assert.That(_warnings.Logs.Any(line => line.Contains("Conflicting stored SERIES values")), Is.True);
        }

        [TestCase(".mp3")]
        [TestCase(".m4b")]
        [TestCase(".wma")]
        public void confirmed_series_should_replace_conflicting_case_variants(string extension)
        {
            var file = Add(extension);
            file.Edition.Book.SeriesId = 1;
            file.Edition.Book.SeriesLinks = new List<SeriesBookLink> { SeriesLink("Selected series", "2") };
            _service.WriteTags(file, false);
            SetSeriesTag(file, "series", "Old series");
            SetSeriesTag(file, "series-part", "7");
            _events.Clear();
            _service.WriteTags(file, false);
            Assert.That(StoredSeries(file.Path, "SERIES"), Is.EqualTo("Selected series"));
            Assert.That(StoredSeries(file.Path, "SERIES-PART"), Is.EqualTo("2"));
            Assert.That(StoredSeries(file.Path, "series"), Is.Null.Or.Empty);
            Assert.That(StoredSeries(file.Path, "series-part"), Is.Null.Or.Empty);
            _service.WriteTags(file, false);
            Assert.That(_events, Has.Count.EqualTo(1));
        }

        [TestCase("星の物語")]
        [TestCase("Серия книг")]
        public void existing_latin1_series_frame_must_not_damage_non_latin_updates(string name)
        {
            var file = Add();
            SetSeriesTag(file, "SERIES", "Old ASCII", latin1: true);
            file.Edition.Book.SeriesLinks = new List<SeriesBookLink> { SeriesLink(name, "2") };
            _service.WriteTags(file, false);
            Assert.That(StoredSeries(file.Path, "SERIES"), Is.EqualTo(name));
            _service.WriteTags(file, false);
            Assert.That(_events, Has.Count.EqualTo(1));
        }

        [TestCaseSource(nameof(TagFormats))]
        public void scrub_without_series_should_not_create_empty_fields(string extension)
        {
            var file = Add(extension);
            _scrub = true;
            _service.WriteTags(file, false);
            using var audio = TagLib.File.Create(file.Path);
            if (audio.GetTag(TagTypes.Id3v2) is TagLib.Id3v2.Tag id3)
            {
                Assert.That(id3.GetFrames<TagLib.Id3v2.UserTextInformationFrame>().Any(frame => frame.Description.StartsWith("SERIES", StringComparison.OrdinalIgnoreCase)), Is.False);
            }
            else Assert.That(StoredSeries(file.Path, "SERIES"), Is.Null);
            _service.WriteTags(file, false);
            Assert.That(_events, Has.Count.EqualTo(1));
        }

        [TestCaseSource(nameof(TagFormats))]
        public void missing_catalog_position_should_keep_the_same_series_stored_position(string extension)
        {
            var file = Add(extension);
            SetSeriesTag(file, "series", "Known series");
            SetSeriesTag(file, "series-part", "7");
            file.Edition.Book.SeriesId = 1;
            file.Edition.Book.SeriesLinks = new List<SeriesBookLink> { SeriesLink("Known series", null) };
            _scrub = true;
            _service.WriteTags(file, false);
            Assert.That(StoredSeries(file.Path, "SERIES-PART"), Is.EqualTo("7"));
            _service.WriteTags(file, false);
            Assert.That(_events, Has.Count.EqualTo(1));
        }

        [TestCaseSource(nameof(FormatCases))]
        public void missing_membership_should_keep_stored_series_even_when_other_tags_are_scrubbed(string extension, bool scrub)
        {
            var file = Add(extension);
            file.Edition.Book.SeriesLinks = new List<SeriesBookLink> { SeriesLink("Known series", "4.5") };
            _service.WriteTags(file, false);
            file.Edition.Book.SeriesLinks = new List<SeriesBookLink> { SeriesLink("Other media", "9", media: BookMediaType.Ebook) };
            file.Edition.Book.SeriesName = "Unpaired summary must not become a membership";
            file.Edition.Publisher = "Changed publisher";
            _scrub = scrub;
            _service.WriteTags(file, false);
            Assert.That(StoredSeries(file.Path, "SERIES"), Is.EqualTo("Known series"));
            Assert.That(StoredSeries(file.Path, "SERIES-PART"), Is.EqualTo("4.5"));
            Assert.That(_events, Has.Count.EqualTo(2));
            Assert.That(_warnings.Logs, Is.Empty);
        }

        [TestCaseSource(nameof(TagFormats))]
        public void changed_series_with_no_position_must_not_keep_the_old_series_number(string extension)
        {
            var file = Add(extension);
            file.Edition.Book.SeriesLinks = new List<SeriesBookLink> { SeriesLink("Old series", "8") };
            _service.WriteTags(file, false);
            file.Edition.Book.SeriesId = 1;
            file.Edition.Book.SeriesLinks = new List<SeriesBookLink> { SeriesLink("New series", null) };
            _service.WriteTags(file, false);
            Assert.That(StoredSeries(file.Path, "SERIES"), Is.EqualTo("New series"));
            Assert.That(StoredSeries(file.Path, "SERIES-PART") ?? string.Empty, Is.Empty);
            Assert.That(_service.GetRetagPreviewsByBook(2), Is.Empty);
        }
    }
}
