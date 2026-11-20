using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.TagExtraction;
using NzbDrone.Core.Notifications;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.MediaFiles
{
    public partial class AudioTagMutationFixture
    {
        [TestCaseSource(nameof(FormatCases))]
        public void scrub_removes_modeled_values_missing_from_database_but_normal_write_keeps_them(string extension, bool scrub)
        {
            var file = Add(extension, 3, 14);
            _service.WriteTags(file, false);
            string[] performers;
            string[] composers;
            using (var audio = TagLib.File.Create(file.Path))
            {
                audio.Tag.Performers = new[] { "First author", "Second author" };
                audio.Tag.Composers = new[] { "First narrator", "Second narrator" };
                audio.Tag.Year = 2019;
                audio.Tag.Comment = "Ripped by fixture";
                audio.Save();
            }
            using (var audio = TagLib.File.Create(file.Path))
            {
                performers = audio.Tag.Performers;
                composers = audio.Tag.Composers;
            }
            file.Edition.Title = null;
            file.Edition.Book.Title = null;
            file.Author.Name = null;
            file.Edition.ReleaseDate = null;
            file.Edition.Publisher = null;
            file.Edition.NarratorNames = new List<string>();
            _scrub = scrub;
            _events.Clear();
            var before = File.ReadAllBytes(file.Path);
            _service.WriteTags(file, false);
            using (var audio = TagLib.File.Create(file.Path))
            {
                Assert.Multiple(() =>
                {
                    Assert.That(audio.Tag.Title ?? string.Empty, Is.EqualTo(scrub ? string.Empty : "Fixture title"));
                    Assert.That(audio.Tag.Album ?? string.Empty, Is.EqualTo(scrub ? string.Empty : "Fixture book"));
                    Assert.That(audio.Tag.Performers, Is.EqualTo(scrub ? Array.Empty<string>() : performers));
                    Assert.That(audio.Tag.Composers, Is.EqualTo(scrub ? Array.Empty<string>() : composers));
                    Assert.That(audio.Tag.Year, Is.EqualTo(scrub ? 0 : 2019));
                    Assert.That(audio.Tag.Track, Is.EqualTo(3));
                    Assert.That(audio.Tag.TrackCount, Is.EqualTo(14));
                    Assert.That(string.IsNullOrEmpty(audio.Tag.Comment), Is.EqualTo(scrub));
                });
            }
            Assert.That(_service.ReadAllTags(file.Path).GetValueOrDefault("PUBLISHER") ?? new List<string>(), scrub ? Is.Empty : Does.Contain("Fixture publisher"));
            Assert.That(_events, Has.Count.EqualTo(scrub ? 1 : 0));
            if (!scrub) Assert.That(File.ReadAllBytes(file.Path), Is.EqualTo(before));
            AssertSecondScrubIsNoop(file);
        }

        [TestCase(".mp3")]
        [TestCase(".m4b")]
        [TestCase(".flac")]
        [TestCase(".ogg")]
        [TestCase(".opus")]
        [TestCase(".wv")]
        public void scrub_removes_full_native_date_when_database_has_no_date(string extension)
        {
            var file = Add(extension);
            _service.WriteTags(file, false);
            const string date = "2019-03-07";
            using (var audio = TagLib.File.Create(file.Path))
            {
                if (audio.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3)
                    TagLib.Id3v2.TextInformationFrame.Get(id3, "TDRC", true).Text = new[] { date };
                else if (audio.GetTag(TagLib.TagTypes.Apple) is TagLib.Mpeg4.AppleTag apple)
                    apple.SetText(new TagLib.ByteVector(new byte[] { 0xa9, 100, 97, 121 }), date);
                else if (audio.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph) xiph.SetField("DATE", date);
                else if (audio.GetTag(TagLib.TagTypes.Ape) is TagLib.Ape.Tag ape) ape.SetValue("Year", date);
                audio.Tag.Comment = "Ripped by fixture";
                audio.Save();
            }
            file.Edition.ReleaseDate = null;
            _scrub = true;
            _events.Clear();
            Assert.That(_service.ReadAllTags(file.Path)["DATE"], Does.Contain(date));
            _service.WriteTags(file, false);
            Assert.That(_service.ReadAllTags(file.Path).GetValueOrDefault("DATE") ?? new List<string>(), Is.Empty);
            Assert.That(_events, Has.Count.EqualTo(1));
            AssertSecondScrubIsNoop(file);
        }

        [TestCase(".mp3")]
        [TestCase(".m4b")]
        [TestCase(".flac")]
        public void scrub_removes_native_title_list_when_database_has_no_title(string extension)
        {
            var file = Add(extension);
            _service.WriteTags(file, false);
            var values = new[] { "First title", "Second title" };
            var appleId = new TagLib.ByteVector(new byte[] { 0xa9, 110, 97, 109 });
            using (var audio = TagLib.File.Create(file.Path))
            {
                if (audio.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3)
                    TagLib.Id3v2.TextInformationFrame.Get(id3, "TIT2", true).Text = values;
                else if (audio.GetTag(TagLib.TagTypes.Apple) is TagLib.Mpeg4.AppleTag apple)
                    apple.SetData(appleId, values.Select(value => new TagLib.Mpeg4.AppleDataBox(TagLib.ByteVector.FromString(value, TagLib.StringType.UTF8), 1)).ToArray());
                else ((TagLib.Ogg.XiphComment)audio.GetTag(TagLib.TagTypes.Xiph)).SetField("TITLE", values);
                audio.Tag.Comment = "Ripped by fixture";
                audio.Save();
            }
            string[] ReadTitles(TagLib.File audio) => audio.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3
                ? TagLib.Id3v2.TextInformationFrame.Get(id3, "TIT2", false)?.Text ?? Array.Empty<string>()
                : audio.GetTag(TagLib.TagTypes.Apple) is TagLib.Mpeg4.AppleTag apple
                    ? apple.DataBoxes(appleId).Select(data => data.Text).ToArray()
                    : ((TagLib.Ogg.XiphComment)audio.GetTag(TagLib.TagTypes.Xiph)).GetField("TITLE");
            using (var audio = TagLib.File.Create(file.Path)) Assert.That(ReadTitles(audio), Is.EqualTo(values), "Fixture must contain two native values before scrub");
            file.Edition.Title = null;
            _scrub = true;
            _events.Clear();
            _service.WriteTags(file, false);
            Assert.That(_events, Has.Count.EqualTo(1));
            using (var audio = TagLib.File.Create(file.Path)) Assert.That(ReadTitles(audio), Is.Empty);
            AssertSecondScrubIsNoop(file);
        }

        [Test]
        public void scrub_removes_asf_full_date_descriptors_when_database_has_no_date()
        {
            var file = Add(".wma");
            _service.WriteTags(file, false);
            var dates = new[] { "2019-03-07", "2020-08-09" };
            using (var audio = TagLib.File.Create(file.Path))
            {
                ((TagLib.Asf.Tag)audio.GetTag(TagLib.TagTypes.Asf)).SetDescriptors("WM/Year",
                    dates.Select(date => new TagLib.Asf.ContentDescriptor("WM/Year", date)).ToArray());
                audio.Tag.Comment = "Ripped by fixture";
                audio.Save();
            }
            string[] ReadDates(TagLib.File audio) => ((TagLib.Asf.Tag)audio.GetTag(TagLib.TagTypes.Asf))
                .GetDescriptors("WM/Year").Select(field => field.ToString()).ToArray();
            using (var audio = TagLib.File.Create(file.Path)) Assert.That(ReadDates(audio), Is.EqualTo(dates));
            file.Edition.ReleaseDate = null;
            _scrub = true;
            _events.Clear();
            _service.WriteTags(file, false);
            Assert.That(_events, Has.Count.EqualTo(1));
            using (var audio = TagLib.File.Create(file.Path)) Assert.That(ReadDates(audio), Is.Empty);
            AssertSecondScrubIsNoop(file);
        }

        [Test]
        public void scrub_removes_riff_native_dates_and_lists_missing_from_database()
        {
            var file = AddWave();
            _scrub = true;
            _service.WriteTags(file, false);
            var titles = new[] { "First title", "Second title" };
            var albums = new[] { "First album", "Second album" };
            var dates = new[] { "2019-03-07", "2020-08-09" };
            using (var audio = TagLib.File.Create(file.Path))
            {
                var info = (TagLib.Riff.InfoTag)audio.GetTag(TagLib.TagTypes.RiffInfo, true);
                info.SetValue("INAM", titles);
                info.SetValue("DIRC", albums);
                info.SetValue("ICRD", dates);
                info.SetValue("XTRA", "remove me");
                var movie = (TagLib.Riff.MovieIdTag)audio.GetTag(TagLib.TagTypes.MovieId, true);
                movie.SetValue("TITL", titles);
                movie.SetValue("IART", new[] { "First author", "Second author" });
                audio.Save();
            }
            void AssertNativeValues(bool removed = false)
            {
                using var audio = TagLib.File.Create(file.Path);
                var info = (TagLib.Riff.InfoTag)audio.GetTag(TagLib.TagTypes.RiffInfo);
                Assert.That(info.GetValuesAsStrings("INAM"), Is.EqualTo(removed ? Array.Empty<string>() : titles));
                Assert.That(info.GetValuesAsStrings("DIRC"), Is.EqualTo(removed ? Array.Empty<string>() : albums));
                Assert.That(info.GetValuesAsStrings("ICRD"), Is.EqualTo(removed ? Array.Empty<string>() : dates));
                var movie = (TagLib.Riff.MovieIdTag)audio.GetTag(TagLib.TagTypes.MovieId);
                Assert.That(movie.GetValuesAsStrings("TITL"), Is.EqualTo(removed ? Array.Empty<string>() : titles));
                Assert.That(movie.GetValuesAsStrings("IART"), Is.EqualTo(removed ? Array.Empty<string>() : new[] { "First author", "Second author" }));
            }
            AssertNativeValues();
            file.Edition.Title = null;
            file.Edition.Book.Title = null;
            file.Author.Name = null;
            file.Edition.ReleaseDate = null;
            _events.Clear();
            _service.WriteTags(file, false);
            Assert.That(_events, Has.Count.EqualTo(1));
            AssertNativeValues(true);
            using (var audio = TagLib.File.Create(file.Path))
                Assert.That(((TagLib.Riff.InfoTag)audio.GetTag(TagLib.TagTypes.RiffInfo)).GetValuesAsStrings("XTRA"), Is.Empty);
            AssertSecondScrubIsNoop(file);
        }

        private static void SetIdentity(TagLib.File audio, string key, string value)
        {
            if (audio.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3)
                TagLib.Id3v2.UserTextInformationFrame.Get(id3, key, true).Text = new[] { value };
            else if (audio.GetTag(TagLib.TagTypes.Apple) is TagLib.Mpeg4.AppleTag apple)
                apple.SetDashBox("com.pilabor.tone", key == "ASIN" ? "AUDIBLE_ASIN" : key, value);
            else if (audio.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph) xiph.SetField(key, value);
            else if (audio.GetTag(TagLib.TagTypes.Ape) is TagLib.Ape.Tag ape) ape.SetValue(key, value);
            else if (audio.GetTag(TagLib.TagTypes.Asf) is TagLib.Asf.Tag asf) asf.SetDescriptorString(value, key == "ASIN" || key == "ISBN" ? "WM/" + key : key);
            else Assert.Fail("Unsupported identity fixture");
        }

        private static string GetIdentity(TagLib.File audio, string key)
        {
            if (audio.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3)
                return string.Join("; ", TagLib.Id3v2.UserTextInformationFrame.Get(id3, key, false)?.Text ?? Array.Empty<string>());
            if (audio.GetTag(TagLib.TagTypes.Apple) is TagLib.Mpeg4.AppleTag apple)
                return apple.GetDashBox("com.pilabor.tone", key == "ASIN" ? "AUDIBLE_ASIN" : key);
            if (audio.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph) return string.Join("; ", xiph.GetField(key));
            if (audio.GetTag(TagLib.TagTypes.Ape) is TagLib.Ape.Tag ape) return ape.GetItem(key)?.ToString();
            if (audio.GetTag(TagLib.TagTypes.Asf) is TagLib.Asf.Tag asf) return asf.GetDescriptorString(key == "ASIN" || key == "ISBN" ? "WM/" + key : key);
            throw new AssertionException("Unsupported identity fixture");
        }

        [TestCaseSource(nameof(TagFormats))]
        public void scrub_removes_native_identity_and_custom_field_asin_without_repeated_writes(string extension)
        {
            var file = Add(extension);
            _scrub = true;
            _service.WriteTags(file, false);
            using (var audio = TagLib.File.Create(file.Path))
            {
                SetIdentity(audio, "ASIN", "B012345678");
                SetIdentity(audio, "CustomIdentity", "Edition b123456789");
                if (extension != ".m4b") SetIdentity(audio, "ISBN", "9781234567897");
                if (audio.GetTag(TagLib.TagTypes.Asf) is TagLib.Asf.Tag asf)
                    asf.MetadataLibraryObject.AddRecord(new TagLib.Asf.DescriptionRecord(0, 0, "FixtureIdentity", "B234567890"));
                audio.Save();
            }
            _events.Clear();
            using (var audio = TagLib.File.Create(file.Path))
            {
                Assert.That(GetIdentity(audio, "ASIN"), Is.EqualTo("B012345678"));
                Assert.That(GetIdentity(audio, "CustomIdentity"), Is.EqualTo("Edition b123456789"));
            }
            using (var audio = TagLib.File.Create(file.Path))
            {
                audio.Tag.Comment = extension == ".wma" ? "Ripped by fixture" : "Ripped by RG from Brilliance Audio";
                audio.Save();
            }
            using (var audio = TagLib.File.Create(file.Path)) Assert.That(audio.Tag.Comment, Does.Contain("Ripped by"));
            _service.WriteTags(file, false);
            Assert.That(_events, Has.Count.EqualTo(1), string.Join("\n", _allLogs.Logs));
            using (var audio = TagLib.File.Create(file.Path))
            {
                Assert.That(GetIdentity(audio, "ASIN"), Is.Null.Or.Empty);
                Assert.That(GetIdentity(audio, "CustomIdentity"), Is.Null.Or.Empty);
                if (extension != ".m4b") Assert.That(GetIdentity(audio, "ISBN"), Is.Null.Or.Empty);
                if (audio.GetTag(TagLib.TagTypes.Asf) is TagLib.Asf.Tag asf)
                    Assert.That(asf.MetadataLibraryObject.GetRecords(0, 0, "FixtureIdentity").Select(record => record.ToString()), Is.Empty);
                Assert.That(string.IsNullOrEmpty(audio.Tag.Comment), Is.True, "An excluded comment's ASIN-shaped text is not identity evidence");
            }
            AssertSecondScrubIsNoop(file);
        }

        [Test]
        public void scrub_keeps_asf_stream_records_while_removing_file_metadata()
        {
            var file = Add(".wma");
            using (var audio = TagLib.File.Create(file.Path))
            {
                var tag = (TagLib.Asf.Tag)audio.GetTag(TagLib.TagTypes.Asf);
                tag.MetadataLibraryObject.AddRecord(new TagLib.Asf.DescriptionRecord(0, 1, "WM/ChannelMask", (uint)3));
                tag.MetadataLibraryObject.AddRecord(new TagLib.Asf.DescriptionRecord(0, 0, "FixtureJunk", "remove me"));
                audio.Save();
            }
            _scrub = true;
            _service.WriteTags(file, false);
            Assert.That(_events, Has.Count.EqualTo(1));
            using (var audio = TagLib.File.Create(file.Path))
            {
                var tag = (TagLib.Asf.Tag)audio.GetTag(TagLib.TagTypes.Asf);
                Assert.That(tag.MetadataLibraryObject.GetRecords(0, 1, "WM/ChannelMask").Single().ToDWord(), Is.EqualTo(3));
                Assert.That(tag.MetadataLibraryObject.GetRecords(0, 0, "FixtureJunk"), Is.Empty);
            }
            AssertSecondScrubIsNoop(file);
        }

        [Test]
        public void scrub_removes_native_asf_brilliance_text()
        {
            var file = Add(".wma");
            _scrub = true;
            _service.WriteTags(file, false);
            using (var audio = TagLib.File.Create(file.Path))
            {
                audio.Tag.Comment = "Ripped by RG from Brilliance Audio";
                audio.Save();
            }
            using (var audio = TagLib.File.Create(file.Path))
            {
                var asf = (TagLib.Asf.Tag)audio.GetTag(TagLib.TagTypes.Asf);
                Assert.That(asf.GetDescriptorString("WM/Text"), Is.EqualTo("Ripped by RG from Brilliance Audio"));
            }
            _events.Clear();
            _service.WriteTags(file, false);
            Assert.That(_events, Has.Count.EqualTo(1));
            using (var audio = TagLib.File.Create(file.Path)) Assert.That(audio.Tag.Comment, Is.Null.Or.Empty);
            AssertSecondScrubIsNoop(file);
        }

        private void AssertSecondScrubIsNoop(BookFile file)
        {
            var events = _events.Count;
            var guards = _guards;
            var before = File.ReadAllBytes(file.Path);
            Assert.That(_service.GetRetagPreviewsByBook(2), Is.Empty);
            _service.WriteTags(file, false);
            Assert.That(File.ReadAllBytes(file.Path), Is.EqualTo(before));
            Assert.That(_events, Has.Count.EqualTo(events));
            Assert.That(_guards, Is.EqualTo(guards));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void scrub_handles_id3v1_trailer_and_id3v23_then_converges(bool v23)
        {
            var file = Add();
            file.Edition.Publisher = null;
            if (!v23) file.Edition.NarratorNames = new List<string>();
            using (var audio = TagLib.File.Create(file.Path))
            {
                audio.RemoveTags(TagLib.TagTypes.AllTags);
                var tag = audio.GetTag(v23 ? TagLib.TagTypes.Id3v2 : TagLib.TagTypes.Id3v1, true);
                if (tag is TagLib.Id3v2.Tag id3)
                {
                    id3.Version = 3;
                    id3.Composers = new[] { "First narrator", "Second narrator" };
                }
                tag.Title = file.Edition.Title;
                tag.Album = file.Edition.Book.Title;
                tag.Performers = new[] { file.Author.Name };
                tag.Year = 2024;
                tag.Comment = "Ripped by fixture";
                audio.Save();
            }
            var original = File.ReadAllBytes(file.Path);
            if (v23) Assert.That(original[3], Is.EqualTo(3), "Fixture must be ID3v2.3 on disk");
            else Assert.That(System.Text.Encoding.ASCII.GetString(original, original.Length - 128, 3), Is.EqualTo("TAG"));
            _scrub = true;
            _events.Clear();
            _service.WriteTags(file, false);
            Assert.That(_events, Has.Count.EqualTo(1));
            using (var audio = TagLib.File.Create(file.Path))
            {
                Assert.That(audio.Tag.Title, Is.EqualTo(file.Edition.Title));
                Assert.That(string.IsNullOrEmpty(audio.Tag.Comment), Is.True);
                if (v23)
                {
                    Assert.That(((TagLib.Id3v2.Tag)audio.GetTag(TagLib.TagTypes.Id3v2)).Version, Is.EqualTo(3));
                    Assert.That(audio.Tag.Composers, Is.EqualTo(new[] { "First narrator", "Second narrator" }));
                }
            }
            AssertSecondScrubIsNoop(file);
        }

        [TestCase("Rebuilds tags from the matched edition")]
        [TestCase("一致した版からタグを再構築")]
        public void scrub_diff_key_is_stable_in_preview_history_and_custom_script(string description)
        {
            var file = Add();
            file.Edition.ForeignEditionId = "hc:edition:123";
            file.Quality = new NzbDrone.Core.Qualities.QualityModel();
            _service.WriteTags(file, false);
            using (var audio = TagLib.File.Create(file.Path))
            {
                audio.Tag.Comment = "Ripped by RG from Brilliance Audio";
                audio.Save();
            }
            _scrub = true;
            _scrubDescription = description;
            _events.Clear();
            var preview = _service.GetRetagPreviewsByBook(2).Single();
            Assert.That(preview.Changes.Keys, Is.EqualTo(new[] { "SCRUB" }));
            Assert.That(preview.Changes["SCRUB"].Item2, Is.EqualTo(description));
            _service.WriteTags(file, false);
            NzbDrone.Core.History.EntityHistory history = null;
            new NzbDrone.Core.History.HistoryService(Proxy<NzbDrone.Core.History.IHistoryRepository>((_, args) => history = (NzbDrone.Core.History.EntityHistory)args[0]), _logs.GetLogger("history"))
                .Handle(_events.Single());
            using var historyJson = System.Text.Json.JsonDocument.Parse(history.Data["Diff"]);
            Assert.That(historyJson.RootElement[0].GetProperty("field").GetString(), Is.EqualTo("SCRUB"));
            Assert.That(historyJson.RootElement[0].GetProperty("newValue").GetString(), Is.EqualTo(description));
            System.Collections.Specialized.StringDictionary environment = null;
            var script = new NzbDrone.Core.Notifications.CustomScript.CustomScript(null,
                Proxy<NzbDrone.Common.Processes.IProcessProvider>((_, args) =>
                {
                    environment = (System.Collections.Specialized.StringDictionary)args[2];
                    return new NzbDrone.Common.Processes.ProcessOutput();
                }), null, _logs.GetLogger("script"))
            {
                Definition = new NotificationDefinition { Settings = new NzbDrone.Core.Notifications.CustomScript.CustomScriptSettings { Path = "fixture-only" } }
            };
            script.OnBookRetag(new BookRetagMessage { Author = file.Author, Book = file.Edition.Book, BookFile = file, Diff = _events.Single().Diff, Scrubbed = true });
            using var scriptJson = System.Text.Json.JsonDocument.Parse(environment["Chaptarr_Tags_Diff"]);
            Assert.That(scriptJson.RootElement.EnumerateObject().Single().Name, Is.EqualTo("scrub"));
            Assert.That(scriptJson.RootElement.GetProperty("scrub").GetProperty("item2").GetString(), Is.EqualTo(description));
            Assert.That(_service.ReadAllTags(file.Path).Keys.Any(key => key.Contains("SCRUB", StringComparison.OrdinalIgnoreCase)), Is.False);
            AssertSecondScrubIsNoop(file);
        }

        [Test]
        public void scrub_only_event_reuses_localized_preview_explanation_in_notification()
        {
            var file = Add();
            _service.WriteTags(file, false);
            using (var audio = TagLib.File.Create(file.Path))
            {
                audio.Tag.Comment = "Ripped by fixture";
                audio.Save();
            }
            _scrub = true;
            _events.Clear();
            var preview = _service.GetRetagPreviewsByBook(2).Single();
            _service.WriteTags(file, false);
            BookRetagMessage delivered = null;
            var notification = Proxy<INotification>((method, args) => method.Name switch
            {
                "get_Definition" => new NotificationDefinition { Id = 1, Tags = new HashSet<int>() },
                "get_HasPendingQueue" => false,
                "OnBookRetag" => delivered = (BookRetagMessage)args[0],
                _ => throw new NotImplementedException(method.Name)
            });
            var factory = Proxy<INotificationFactory>((method, _) => method.Name switch
            {
                "OnBookRetagEnabled" => new List<INotification> { notification },
                "GetAvailableProviders" => new List<INotification>(),
                _ => throw new NotImplementedException(method.Name)
            });
            var statuses = Proxy<INotificationStatusService>((method, _) => method.Name switch
            {
                "GetBlockedProviders" => new List<NotificationStatus>(),
                "RecordSuccess" => null,
                _ => throw new NotImplementedException(method.Name)
            });
            new NotificationService(factory, statuses, null, _logs.GetLogger("notification")).Handle(_events.Single());
            Assert.That(delivered, Is.Not.Null);
            Assert.That(delivered.Diff, Is.EqualTo(preview.Changes));
            Assert.That(delivered.Message, Does.Contain("SCRUB"));
            Assert.That(delivered.Message, Does.Contain(_localization.GetLocalizedString("AudioTagsScrubChanges")));
            Assert.That(_warnings.Logs, Is.Empty);
            Assert.That(_allLogs.Logs, Has.Some.Contains("Audio tag comparisons (Preview): 1 files in"));
        }
    }
}
