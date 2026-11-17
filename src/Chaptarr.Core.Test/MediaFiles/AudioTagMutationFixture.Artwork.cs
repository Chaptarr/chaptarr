using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaCover;
using static Chaptarr.Core.Test.Common.InterfaceProxy;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Commands;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Chaptarr.Core.Test.MediaFiles
{
    public partial class AudioTagMutationFixture
    {
        private string CreateCover(string name, int seed)
        {
            var path = Path.Combine(_directory, name + ".png");
            var random = new Random(seed);
            using var image = new Image<Rgb24>(256, 256);
            for (var y = 0; y < image.Height; y++)
                for (var x = 0; x < image.Width; x++)
                    image[x, y] = new Rgb24((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
            image.SaveAsPng(path);
            Assert.That(new FileInfo(path).Length, Is.GreaterThanOrEqualTo(100_000), "Use real large image bytes");
            return path;
        }


        [Test]
        public void scrub_removes_duplicate_art_from_a_secondary_tag_container_once()
        {
            var file = Add();
            _scrub = true;
            _coverPath = CreateCover("edition", 29);
            _service.WriteTags(file, false);
            using (var audio = TagLib.File.Create(file.Path))
            {
                audio.GetTag(TagLib.TagTypes.Ape, true).Pictures = new[] { new TagLib.Picture(_coverPath) { Type = TagLib.PictureType.FrontCover, Description = string.Empty } };
                audio.Save();
            }
            _events.Clear();
            var previews = _service.GetRetagPreviewsByBook(2);
            _service.WriteTags(file, false);
            Assert.That(_events, Has.Count.EqualTo(1));
            using (var audio = TagLib.File.Create(file.Path)) Assert.That(audio.GetTag(TagLib.TagTypes.Ape).Pictures, Is.Empty);
            AssertSecondScrubIsNoop(file);
            Assert.That(previews.Single().Changes.Keys, Is.EquivalentTo(new[] { "COVER", "SCRUB" }));
        }

        [Test]
        public void preview_discloses_incidental_identity_and_cover_changes_on_a_real_write()
        {
            var file = Add();
            _service.WriteTags(file, false);
            file.Edition.AudibleASIN = "B002V5ECMK";
            file.Edition.Isbn13 = "9780593135204";
            _coverPath = CreateCover("edition", 29);
            Assert.That(_service.GetRetagPreviewsByBook(2), Is.Empty);
            file.Edition.Title = "Corrected title";
            var preview = _service.GetRetagPreviewsByBook(2).Single();
            Assert.That(preview.Changes.Keys, Is.EquivalentTo(new[] { "TITLE", "ASIN", "ISBN", "COVER" }));
            _events.Clear();
            _service.WriteTags(file, false);
            Assert.That(_events.Single().Diff.Keys, Is.EquivalentTo(preview.Changes.Keys));
        }

        public static IEnumerable<TestCaseData> ArtworkCases()
        {
            foreach (var extension in TagFormats)
                foreach (var local in new[] { false, true })
                    yield return new TestCaseData(extension, local);
        }

        [TestCaseSource(nameof(ArtworkCases))]
        public void scrub_replaces_large_junk_art_once_then_preview_and_writer_are_noops(string extension, bool localCover)
        {
            var file = Add(extension, 3, 14);
            _scrub = true;
            _service.WriteTags(file, false);
            var junk = CreateCover("release", 17);
            using (var audio = TagLib.File.Create(file.Path))
            {
                audio.Tag.Pictures = new[] { new TagLib.Picture(junk) { Type = TagLib.PictureType.FrontCover } };
                audio.Save();
            }
            using (var audio = TagLib.File.Create(file.Path))
            {
                Assert.That(audio.Tag.Pictures.Single().Data.Data, Is.EqualTo(File.ReadAllBytes(junk)));
                if (extension == ".opus")
                {
                    var stored = ((TagLib.Ogg.XiphComment)audio.GetTag(TagLib.TagTypes.Xiph)).GetField("METADATA_BLOCK_PICTURE").Single();
                    Assert.That(System.Text.RegularExpressions.Regex.IsMatch(stored, @"\bB[0-9A-Z]{9}\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase), Is.True,
                        "The real cover's native base64 reproduces the old preservation predicate");
                }
            }
            if (localCover) _coverPath = CreateCover("edition", 29);
            _events.Clear();
            var before = File.ReadAllBytes(file.Path);
            var guards = _guards;
            if (MediaFileExtensions.CanWriteAudioTags(extension))
                Assert.That(_service.GetRetagPreviewsByBook(2).Single().Changes.Keys, Is.EquivalentTo(new[] { "SCRUB", "COVER" }));
            Assert.That(File.ReadAllBytes(file.Path), Is.EqualTo(before), "Preview must never save");
            _service.WriteTags(file, false);
            Assert.That(_events, Has.Count.EqualTo(1), string.Join("\n", _allLogs.Logs));
            Assert.That(_guards, Is.EqualTo(guards + 1));
            using (var audio = TagLib.File.Create(file.Path))
            {
                Assert.That(audio.Tag.Pictures, Has.Length.EqualTo(localCover ? 1 : 0));
                if (localCover)
                {
                    Assert.That(audio.Tag.Pictures.Single().Data.Data, Is.EqualTo(File.ReadAllBytes(_coverPath)));
                    if (extension == ".m4b") Assert.That(audio.Tag.Pictures.Single().Description, Is.EqualTo("cover.png"), "MP4 synthesizes its description from the image bytes");
                }
                Assert.That(audio.Tag.Track, Is.EqualTo(3));
                Assert.That(audio.Tag.TrackCount, Is.EqualTo(14));
                Assert.That(_service.ReadAllTags(file.Path).Keys.Any(key => key.Contains("SCRUB", StringComparison.OrdinalIgnoreCase)), Is.False);
            }
            AssertSecondScrubIsNoop(file);
            Assert.That(_warnings.Logs, Is.Empty);
        }

        [TestCaseSource(nameof(ArtworkCases))]
        public void previously_written_file_with_own_cover_or_no_art_has_no_repeat_write(string extension, bool embedded)
        {
            var file = Add(extension);
            _scrub = true;
            _coverPath = embedded ? CreateCover("edition", 29) : null;
            _service.WriteTags(file, false);
            // A local image becoming available does not itself authorize a write.
            _coverPath ??= CreateCover("edition", 29);
            _events.Clear();
            AssertSecondScrubIsNoop(file);
            Assert.That(_events, Is.Empty);
            Assert.That(_warnings.Logs, Is.Empty);
        }

        [TestCase(".mp3", true)]
        [TestCase(".mp3", false)]
        [TestCase(".m4b", true)]
        [TestCase(".m4b", false)]
        public void already_clean_file_is_not_written_on_its_first_retag(string extension, bool embedded)
        {
            var file = Add(extension);
            _coverPath = CreateCover("edition", 29);
            using (var audio = TagLib.File.Create(file.Path))
            {
                audio.Tag.Clear();
                audio.Tag.Title = file.Edition.Title;
                audio.Tag.Album = file.Edition.Book.Title;
                audio.Tag.Performers = new[] { file.Author.Name };
                audio.Tag.Composers = new[] { "First narrator", "Second narrator" };
                audio.Tag.Year = 2024;
                if (audio.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3)
                    TagLib.Id3v2.TextInformationFrame.Get(id3, new TagLib.ByteVector("TPUB"), true).Text = new[] { file.Edition.Publisher };
                else ((TagLib.Mpeg4.AppleTag)audio.GetTag(TagLib.TagTypes.Apple)).SetDashBox("com.apple.iTunes", "publisher", file.Edition.Publisher);
                if (embedded) audio.Tag.Pictures = new[] { new TagLib.Picture(_coverPath) { Type = TagLib.PictureType.FrontCover, Description = string.Empty } };
                audio.Save();
            }
            _scrub = true;
            var before = File.ReadAllBytes(file.Path);
            Assert.That(_service.GetRetagPreviewsByBook(2), Is.Empty);
            _service.WriteTags(file, false);
            Assert.That(_events, Is.Empty);
            Assert.That(_guards, Is.Zero);
            Assert.That(File.ReadAllBytes(file.Path), Is.EqualTo(before));
        }

        [TestCase(".mp3")]
        [TestCase(".flac")]
        [TestCase(".opus")]
        public void scrub_clears_junk_picture_metadata_even_when_cover_bytes_match(string extension)
        {
            var file = Add(extension);
            _scrub = true;
            _coverPath = CreateCover("edition", 29);
            _service.WriteTags(file, false);
            using (var audio = TagLib.File.Create(file.Path))
            {
                audio.Tag.Pictures = new[] { new TagLib.Picture(_coverPath) { Type = TagLib.PictureType.BackCover, Description = "Ripped by fixture" } };
                audio.Save();
            }
            using (var audio = TagLib.File.Create(file.Path))
            {
                Assert.That(audio.Tag.Pictures.Single().Type, Is.EqualTo(TagLib.PictureType.BackCover));
                Assert.That(audio.Tag.Pictures.Single().Description, Is.EqualTo("Ripped by fixture"));
            }
            _events.Clear();
            Assert.That(_service.GetRetagPreviewsByBook(2), Has.Count.EqualTo(1));
            _service.WriteTags(file, false);
            Assert.That(_events, Has.Count.EqualTo(1));
            using (var audio = TagLib.File.Create(file.Path))
            {
                Assert.That(audio.Tag.Pictures.Single().Type, Is.EqualTo(TagLib.PictureType.FrontCover));
                Assert.That(audio.Tag.Pictures.Single().Description, Is.Null.Or.Empty);
            }
            AssertSecondScrubIsNoop(file);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void non_scrub_cover_difference_is_incidental_to_an_actual_tag_write(bool localCover)
        {
            var file = Add(".m4b");
            _service.WriteTags(file, false);
            var junk = CreateCover("release", 17);
            using (var audio = TagLib.File.Create(file.Path))
            {
                audio.Tag.Pictures = new[] { new TagLib.Picture(junk) { Type = TagLib.PictureType.FrontCover } };
                audio.Save();
            }
            if (localCover) _coverPath = CreateCover("edition", 29);
            _events.Clear();
            AssertSecondScrubIsNoop(file);
            Assert.That(_events, Is.Empty);
            file.Edition.Title = "Changed title";
            _service.WriteTags(file, false);
            Assert.That(_events, Has.Count.EqualTo(1));
            using (var audio = TagLib.File.Create(file.Path))
                Assert.That(audio.Tag.Pictures.Single().Data.Data, Is.EqualTo(File.ReadAllBytes(localCover ? _coverPath : junk)));
            AssertSecondScrubIsNoop(file);
        }

        [Test]
        public void disabled_import_does_not_load_cover_but_managed_tag_change_on_manual_retag_embeds_it()
        {
            var file = Add();
            _mode = NzbDrone.Core.Configuration.WriteAudioTagsType.No;
            _coverPath = CreateCover("edition", 29);
            var before = File.ReadAllBytes(file.Path);
            _service.WriteTags(file, true);
            Assert.That(_coverLookups, Is.Zero);
            Assert.That(File.ReadAllBytes(file.Path), Is.EqualTo(before));
            var batch = RetagFiles(new RetagFilesCommand(1, new List<int> { file.Id }));
            Assert.That(batch.Written, Is.EqualTo(1));
            using var audio = TagLib.File.Create(file.Path);
            Assert.That(audio.Tag.Pictures.Single().Data.Data, Is.EqualTo(File.ReadAllBytes(_coverPath)));
        }

        [TestCase(".mp3", true)]
        [TestCase(".mp3", false)]
        [TestCase(".m4b", true)]
        [TestCase(".m4b", false)]
        [TestCase(".flac", true)]
        [TestCase(".opus", true)]
        [TestCase(".ogg", true)]
        [TestCase(".wma", true)]
        public void unavailable_art_is_preserved_while_other_tags_can_be_updated(string extension, bool scrub)
        {
            var file = Add(extension);
            _scrub = scrub;
            _service.WriteTags(file, false);
            var art = CreateCover("old", 17);
            using (var audio = TagLib.File.Create(file.Path))
            {
                audio.Tag.Pictures = new[] { new TagLib.Picture(art) };
                audio.Save();
            }
            _events.Clear();
            _coverReady = false;
            AssertSecondScrubIsNoop(file);
            file.Edition.Title = "Updated title";
            var preview = _service.GetRetagPreviewsByBook(2).Single();
            Assert.That(preview.Changes.Keys, Does.Contain("TITLE"));
            Assert.That(preview.Changes.ContainsKey("SCRUB"), Is.EqualTo(scrub));
            _service.WriteTags(file, false);
            Assert.That(_events, Has.Count.EqualTo(1));
            using (var audio = TagLib.File.Create(file.Path))
            {
                Assert.That(audio.Tag.Title, Is.EqualTo("Updated title"));
                Assert.That(audio.Tag.Pictures.Single().Data.Data, Is.EqualTo(File.ReadAllBytes(art)));
            }
            _coverReady = true;
            _coverPath = CreateCover("new", 29);
            // The separate art-only test pins no write outside Scrub; this step changes a managed field.
            if (!scrub) file.Edition.Title = "Another title";
            _service.WriteTags(file, false);
            using (var audio = TagLib.File.Create(file.Path))
                Assert.That(audio.Tag.Pictures.Single().Data.Data, Is.EqualTo(File.ReadAllBytes(_coverPath)));
            AssertSecondScrubIsNoop(file);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void unreadable_cover_does_not_fail_a_useful_tag_write(bool decoderFailure)
        {
            var file = Add();
            _scrub = true;
            _coverRead = _ => throw (decoderFailure ? new ImageProcessingException("Fixture decoder processing failed") : new IOException("Fixture cover is busy"));
            _service.WriteTags(file, false);
            Assert.That(_events, Has.Count.EqualTo(1));
            Assert.That(_warnings.Logs, Is.Empty);
            using var audio = TagLib.File.Create(file.Path);
            Assert.That(audio.Tag.Title, Is.EqualTo(file.Edition.Title));
        }

        [Test]
        public void refreshed_cover_url_reaches_sync_and_real_reader_without_erasing_art()
        {
            var file = Add(".m4b");
            file.Edition.Monitored = true;
            file.Edition.ForeignEditionId = "hc:edition:123";
            file.Edition.Images = new() { new(MediaCoverTypes.Cover, "https://images.example/old.png") };
            var logger = _logs.GetLogger("RefreshArtwork");
            var covers = new MediaCoverService(null, null, null, null, _disk,
                Proxy<IAppFolderInfo>((_, _) => _directory), null, null, null, null, null, logger, imageHostHealth: new NzbDrone.Core.Http.HttpServiceHealth(NLog.LogManager.GetCurrentClassLogger()));
            var original = CreateCover("stored", 47);
            var path = covers.GetCoverPath(file.Edition.BookId, MediaCoverEntity.Book, MediaCoverTypes.Cover, ".png");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.Copy(original, path);
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(path), "cover-metadata.json"), new
            {
                SelectedEdition = new { EditionProviderId = file.Edition.ForeignEditionId, CoverUrl = file.Edition.Images[0].Url, DownloadedAt = DateTime.UtcNow }
            }.ToJson());
            _coverSnapshot = covers.ReadLocalBookCover;
            _service.WriteTags(file, false);
            Assert.That(covers.ReadLocalBookCover(file.Edition).Ready, Is.True);
            var remote = RefreshEntityCopy.CloneEdition(file.Edition);
            remote.Images = new() { new(MediaCoverTypes.Cover, "https://images.example/new.png") };
            remote.Title = "Corrected title";
            var saved = false;
            var editions = Proxy<IEditionService>((method, _) =>
            {
                if (method.Name != nameof(IEditionService.UpdateMany)) throw new AssertionException(method.Name);
                saved = true;
                return null;
            });
            var metadata = new MetadataTagService(_service, Proxy<IEBookTagService>((_, _) => null), logger,
                null, Proxy<NzbDrone.Core.Messaging.Commands.ICommandResultReporter>((_, _) => null), _media, null);
            _events.Clear();
            _scrub = true;
            var refresh = new RefreshEditionService(editions, metadata, null, null, logger);
            refresh.RefreshEditionInfo(new(), new() { file.Edition }, new(), new(), new(), new() { remote }, false,
                new Dictionary<int, Edition> { [file.Edition.Id] = remote });
            Assert.That(saved, Is.True);
            Assert.That(file.Edition.Images.Single().Url, Is.EqualTo(remote.Images.Single().Url));
            Assert.That(covers.ReadLocalBookCover(file.Edition).Ready, Is.False, "The old receipt cannot stand in for the new URL");
            Assert.That(_events, Has.Count.EqualTo(1), string.Join("\n", _allLogs.Logs));
            using (var audio = TagLib.File.Create(file.Path))
            {
                Assert.That(audio.Tag.Title, Is.EqualTo(remote.Title));
                Assert.That(audio.Tag.Pictures.Single().Data.Data, Is.EqualTo(File.ReadAllBytes(original)));
            }
            AssertSecondScrubIsNoop(file);
        }

        [Test]
        public void invalid_webp_does_not_block_metadata_or_erase_embedded_art()
        {
            var file = Add();
            var original = CreateCover("original", 31);
            using (var audio = TagLib.File.Create(file.Path))
            {
                audio.Tag.Pictures = new[] { new TagLib.Picture(original) };
                audio.Save();
            }
            _coverPath = Path.Combine(_directory, "broken.webp");
            using (var image = new Image<Rgb24>(32, 48, new Rgb24(20, 100, 200))) image.SaveAsWebp(_coverPath);
            File.WriteAllBytes(_coverPath, File.ReadAllBytes(_coverPath).Take(16).ToArray());
            Assert.That(Image.DetectFormat(File.ReadAllBytes(_coverPath)).DefaultMimeType, Is.EqualTo("image/webp"));
            _scrub = true;
            _service.WriteTags(file, false);
            Assert.That(_events, Has.Count.EqualTo(1), string.Join("\n", _allLogs.Logs));
            using (var audio = TagLib.File.Create(file.Path))
            {
                Assert.That(audio.Tag.Title, Is.EqualTo(file.Edition.Title));
                Assert.That(audio.Tag.Pictures.Single().Data.Data, Is.EqualTo(File.ReadAllBytes(original)));
            }
            AssertSecondScrubIsNoop(file);
        }

        [Test]
        public void changed_mp3_art_is_written_only_to_id3_even_when_ape_also_exists()
        {
            var file = Add();
            var original = CreateCover("original", 31);
            using (var audio = TagLib.File.Create(file.Path))
            {
                audio.GetTag(TagLib.TagTypes.Ape, true).Pictures = new[] { new TagLib.Picture(original) };
                audio.GetTag(TagLib.TagTypes.Id3v2, true).Pictures = new[] { new TagLib.Picture(original) };
                audio.Save();
            }
            _coverPath = CreateCover("edition", 32);
            _service.WriteTags(file, false);
            using (var audio = TagLib.File.Create(file.Path))
            {
                Assert.That(audio.GetTag(TagLib.TagTypes.Id3v2).Pictures.Single().Data.Data, Is.EqualTo(File.ReadAllBytes(_coverPath)));
                Assert.That(audio.GetTag(TagLib.TagTypes.Ape).Pictures, Is.Empty);
            }
            AssertSecondScrubIsNoop(file);
        }

        [TestCase(".mp3", "jpeg", false)]
        [TestCase(".mp3", "jpeg", true)]
        [TestCase(".mp3", "webp", false)]
        [TestCase(".mp3", "webp", true)]
        [TestCase(".m4b", "jpeg", false)]
        [TestCase(".m4b", "jpeg", true)]
        [TestCase(".m4b", "webp", false)]
        [TestCase(".m4b", "webp", true)]
        public void artwork_is_embedded_as_a_player_compatible_image(string extension, string format, bool scrub)
        {
            var file = Add(extension);
            _scrub = scrub;
            _coverPath = Path.Combine(_directory, "edition." + format);
            using (var image = new Image<Rgb24>(32, 48, new Rgb24(20, 100, 200)))
            {
                if (format == "jpeg") image.SaveAsJpeg(_coverPath);
                else image.SaveAsWebp(_coverPath);
            }
            _service.WriteTags(file, false);
            Assert.That(_events, Has.Count.EqualTo(1));
            AssertSecondScrubIsNoop(file);
            using var audio = TagLib.File.Create(file.Path);
            var picture = audio.Tag.Pictures.Single();
            Assert.That(picture.MimeType, Is.EqualTo("image/jpeg"));
            if (format == "jpeg") Assert.That(picture.Data.Data, Is.EqualTo(File.ReadAllBytes(_coverPath)));
            using var embedded = Image.Load(picture.Data.Data);
            Assert.That((embedded.Width, embedded.Height), Is.EqualTo((32, 48)));
        }

        [TestCase(".mp3")]
        [TestCase(".m4b")]
        public void unsupported_avif_cover_keeps_existing_art_during_scrub(string extension)
        {
            var file = Add(extension);
            var existing = CreateCover("existing", 17);
            using (var audio = TagLib.File.Create(file.Path))
            {
                audio.Tag.Pictures = new[] { new TagLib.Picture(existing) };
                audio.Save();
            }
            _coverPath = Path.Combine(_directory, "edition.avif");
            File.WriteAllBytes(_coverPath, Convert.FromBase64String("AAAAIGZ0eXBhdmlmAAAAAGF2aWZtaWYxbWlhZk1BMUIAAAD5bWV0YQAAAAAAAAAvaGRscgAAAAAAAAAAcGljdAAAAAAAAAAAAAAAAFBpY3R1cmVIYW5kbGVyAAAAAA5waXRtAAAAAAABAAAAHmlsb2MAAAAARAAAAQABAAAAAQAAASEAAAAbAAAAKGlpbmYAAAAAAAEAAAAaaW5mZQIAAAAAAQAAYXYwMUNvbG9yAAAAAGppcHJwAAAAS2lwY28AAAAUaXNwZQAAAAAAAAAIAAAACAAAABBwaXhpAAAAAAMICAgAAAAMYXYxQ4EADAAAAAATY29scm5jbHgAAgACAAIAAAAAF2lwbWEAAAAAAAAAAQABBAECgwQAAAAjbWRhdAoFGAi/bAIyEhgAAABQAAAAALATUT8UTny0YA=="));
            _scrub = true;
            _service.WriteTags(file, false);
            Assert.That(_events, Has.Count.EqualTo(1));
            using (var audio = TagLib.File.Create(file.Path))
            {
                Assert.That(audio.Tag.Title, Is.EqualTo(file.Edition.Title));
                Assert.That(audio.Tag.Pictures.Single().Data.Data, Is.EqualTo(File.ReadAllBytes(existing)));
            }
            AssertSecondScrubIsNoop(file);
        }

        [Test]
        public void batch_evicts_previous_book_cover_and_never_reuses_another_books_art()
        {
            var first = Add();
            var other = Add();
            other.EditionId = other.Edition.Id = 4;
            var returned = Add();
            var pathA = CreateCover("edition-a", 29);
            var pathB = CreateCover("edition-b", 41);
            var replacement = File.ReadAllBytes(CreateCover("edition-a-updated", 55));
            var expected = new[] { File.ReadAllBytes(pathA), File.ReadAllBytes(pathB), replacement };
            _coverRead = edition => edition.Id == 3 ? pathA : pathB;
            _beforeUpdate = () => { if (_updates == 1) File.WriteAllBytes(pathA, replacement); };
            var batch = RetagFiles(new RetagFilesCommand(1, new List<int> { first.Id, other.Id, returned.Id }));
            Assert.That(batch.Written, Is.EqualTo(3), string.Join("\n", _allLogs.Logs));
            for (var i = 0; i < _files.Count; i++)
            {
                using var audio = TagLib.File.Create(_files[i].Path);
                Assert.That(audio.Tag.Pictures.Single().Data.Data, Is.EqualTo(expected[i]));
            }
        }

        [Test]
        public void batch_reuses_cover_bytes_without_reopening_the_image_for_each_file()
        {
            var first = Add();
            var second = Add();
            _coverPath = CreateCover("edition", 29);
            var expected = File.ReadAllBytes(_coverPath);
            _beforeUpdate = () => File.Delete(_coverPath);
            var batch = RetagFiles(new RetagFilesCommand(1, new List<int> { first.Id, second.Id }));
            Assert.That(batch.Written, Is.EqualTo(2), string.Join("\n", _allLogs.Logs));
            Assert.That(_coverLookups, Is.EqualTo(1), "One local snapshot serves all parts of the current edition");
            foreach (var file in _files)
            {
                using var audio = TagLib.File.Create(file.Path);
                Assert.That(audio.Tag.Pictures.Single().Data.Data, Is.EqualTo(expected));
            }
        }
    }
}
