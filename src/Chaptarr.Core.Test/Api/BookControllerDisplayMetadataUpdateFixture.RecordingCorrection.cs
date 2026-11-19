using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1.BookFiles;
using Chaptarr.Api.V1.Books;
using Chaptarr.Http.Middleware;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Api
{
    public partial class BookControllerDisplayMetadataUpdateFixture
    {
        [Test]
        public void explicit_edition_correction_updates_recording_class_for_all_parts(
            [Values(false, true)] bool postgres,
            [Values(false, true)] bool pinCurrent,
            [Values("standard", "dramatized", "fullcast", "brand", "multiple")] string production)
        {
            var expected = production != "standard";
            VerifyEditionRecordingCorrection(postgres, pinCurrent, selected =>
            {
                selected.IsGraphicAudio = false;
                selected.AudioProductionType = production switch { "dramatized" => "Dramatized", "fullcast" => "Full Cast", _ => null };
                selected.Publisher = production == "brand" ? "GraphicAudio" : "Publisher";
                selected.NarratorNames = production == "multiple" ? new() { "Reader One", "Reader Two", "Reader Three" } : new();
            }, expected, production switch
            {
                "dramatized" => "Dramatized", "fullcast" or "multiple" => "Full Cast", "brand" => "GraphicAudio", _ => null
            });
        }

        private void VerifyEditionRecordingCorrection(bool postgres, bool pinCurrent, Action<Edition> configure,
            bool expected, string expectedLabel, bool facade = false)
        {
            var writes = new List<string>();
            WithEndpoint(postgres, facade, true, (_, _, editions, book, _) =>
            {
                var selected = book.Editions.Single(edition => edition.Monitored == pinCurrent);
                configure(selected);
                editions.Update(selected);
            }, fileTest: (controller, _, files, book) =>
            {
                var selected = book.Editions.Single(edition => edition.Monitored == pinCurrent);
                var original = book.Editions.Single(edition => edition.Monitored);
                var parts = Enumerable.Range(1, 3).Select(part => files.Insert(new BookFile
                {
                    EditionId = original.Id, Path = $"/fixture/part-{part}.m4b", MediaType = "audiobook",
                    Quality = new QualityModel(Quality.M4B), MediaInfo = new MediaInfoModel(),
                    DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow,
                    AudioProductionOverride = part == 1 ? null : !expected, IsGraphicAudio = !expected,
                    AudioProductionType = expected ? null : "Dramatized",
                    AllTags = new() { ["COMMENT"] = new() { expected ? "Standard narration" : "GraphicAudio" } }
                })).ToList();
                var request = controller.GetBookById(book.Id).Value;
                request.AnyEditionOk = false;
                request.Editions = book.Editions.ToResource(controller.HttpContext.GetReadarrFacadeContext());
                request.Editions.ForEach(edition => edition.Monitored = edition.Id == selected.Id);
                controller.UpdateBook(request);

                var format = new CustomFormat { Id = 1, Name = "Production", Specifications = new()
                {
                    new AudioProductionSpecification { Name = "Production" }
                } };
                var formats = new CustomFormatCalculationService(Proxy<ICustomFormatService>((method, _) =>
                    method.Name == nameof(ICustomFormatService.All) ? new List<CustomFormat> { format } :
                    throw new NotImplementedException(method.Name)), LogManager.GetCurrentClassLogger());
                foreach (var saved in files.Get(parts.Select(part => part.Id)))
                {
                    Assert.Multiple(() =>
                    {
                        Assert.That(saved.EditionId, Is.EqualTo(selected.Id));
                        Assert.That(saved.AudioProductionOverride, Is.EqualTo(expected));
                        Assert.That(saved.ToResource().IsGraphicAudio, Is.EqualTo(expected), "The file API shows the corrected class");
                        Assert.That(AudioProductionDetector.IsDramatizedRecording(saved), Is.EqualTo(expected), "Replacement uses the correction");
                        Assert.That(formats.ParseCustomFormat(saved).Select(item => item.Id),
                            Is.EquivalentTo(expected ? new[] { format.Id } : Array.Empty<int>()));
                        Assert.That(AudioProductionDetector.GetProductionLabel(saved, saved.Edition), Is.EqualTo(expectedLabel),
                            "Naming uses the corrected class without inventing a publisher");
                        Assert.That(saved.IsGraphicAudio, Is.EqualTo(!expected), "Observed evidence is retained separately from a correction");
                        Assert.That(saved.Path, Is.EqualTo(parts.Single(part => part.Id == saved.Id).Path));
                    });
                }

                writes.Clear();
                controller.UpdateBook(controller.GetBookById(book.Id).Value);
                Assert.That(writes.Where(sql => sql.StartsWith("UPDATE \"BookFiles\"", StringComparison.Ordinal)), Is.Empty,
                    "An unchanged save does not rewrite the files");
                Assert.That(files.Get(parts.Select(part => part.Id)).Select(file => file.AudioProductionOverride), Is.All.EqualTo(expected));

                var automatic = controller.GetBookById(book.Id).Value;
                automatic.AnyEditionOk = true;
                writes.Clear();
                controller.UpdateBook(automatic);
                Assert.That(files.Get(parts.Select(part => part.Id)).Select(file => file.AudioProductionOverride), Is.All.Null,
                    "Enabling automatic selection clears this row's recording corrections");
                Assert.That(files.Get(parts.Select(part => part.Id)).Select(file => file.EditionId), Is.All.EqualTo(selected.Id));
                var changed = files.Get(parts[0].Id);
                changed.AudioProductionOverride = !expected;
                files.SetFields(changed, file => file.AudioProductionOverride);
                var pinned = controller.GetBookById(book.Id).Value;
                pinned.AnyEditionOk = false;
                controller.UpdateBook(pinned);
                Assert.That(files.Get(parts.Select(part => part.Id)).Select(file => file.AudioProductionOverride), Is.All.EqualTo(expected),
                    "Pinning again replaces an intervening explicit file correction");
            }, observeCommand: writes.Add);
        }

        // Captured catalog fields; file tags below deliberately contradict the chosen edition.
        public static IEnumerable<TestCaseData> CapturedRecordingEditions()
        {
            var editions = new (Edition Edition, string Label)[]
            {
                (new Edition
                {
                    ForeignEditionId = "az:B002V5GQ1E-audiobook",
                    Title = "The Raven",
                    Subtitle = "Dramatic Reading of the Gothic Classic plus Special Commentary",
                    Publisher = "Renaissance E Books Inc.",
                    Narrator = "Bill Mills",
                    AudioProductionType = null,
                    IsGraphicAudio = false, NarratorNames = new() { "Bill Mills" }
                }, "Dramatized"),
                (new Edition
                {
                    ForeignEditionId = "hc:edition:31651179-audiobook",
                    Title = "Good Omens: The BBC Radio 4 Dramatisation",
                    Subtitle = "The Nice and Accurate Prophecies of Agnes Nutter, Witch",
                    Publisher = "BBC Physical Audio",
                    Narrator = "",
                    AudioProductionType = null,
                    IsGraphicAudio = false, NarratorNames = new() {  }
                }, "Dramatized"),
                (new Edition
                {
                    ForeignEditionId = "gr:20362706-audiobook",
                    Title = "Neverwhere",
                    Subtitle = "BBC Dramatisation",
                    Publisher = "BBC Radio 4",
                    Narrator = "",
                    AudioProductionType = null,
                    IsGraphicAudio = false, NarratorNames = new() {  }
                }, "Dramatized"),
                (new Edition
                {
                    ForeignEditionId = "gr:33407704-audiobook",
                    Title = "Stardust: BBC Radio 4 Dramatisation",
                    Subtitle = "BBC Radio 4 Dramatisation",
                    Publisher = "BBC Radio 4",
                    Narrator = "",
                    AudioProductionType = null,
                    IsGraphicAudio = false, NarratorNames = new() {  }
                }, "Dramatized"),
                (new Edition
                {
                    ForeignEditionId = "hc:edition:30785244-audiobook",
                    Title = "Stardust - BBC Dramatisation",
                    Subtitle = null,
                    Publisher = "BBC Radio 4",
                    Narrator = "",
                    AudioProductionType = null,
                    IsGraphicAudio = false, NarratorNames = new() {  }
                }, "Dramatized"),
                (new Edition
                {
                    ForeignEditionId = "hc:edition:32278570-audiobook",
                    Title = "Stardust: BBC Radio 4 Dramatisation",
                    Subtitle = "BBC Radio 4 Dramatisation",
                    Publisher = "BBC Radio 4",
                    Narrator = "",
                    AudioProductionType = null,
                    IsGraphicAudio = false, NarratorNames = new() {  }
                }, "Dramatized"),
                (new Edition
                {
                    ForeignEditionId = "hc:edition:32337007-audiobook",
                    Title = "The Chronicles of Narnia: The Lion, the Witch, and the Wardrobe",
                    Subtitle = null,
                    Publisher = "Listening Library",
                    Narrator = "Dramatization",
                    AudioProductionType = null,
                    IsGraphicAudio = false, NarratorNames = new() { "Dramatization" }
                }, "Dramatized"),
                (new Edition
                {
                    ForeignEditionId = "gr:268720-audiobook",
                    Title = "The Lion, the Witch, and the Wardrobe",
                    Subtitle = "The Lion, the Witch, and the Wardrobe",
                    Publisher = "Listening Library",
                    Narrator = "Dramatization",
                    AudioProductionType = null,
                    IsGraphicAudio = false, NarratorNames = new() { "Dramatization" }
                }, "Dramatized"),
                (new Edition
                {
                    ForeignEditionId = "gr:56390645-audiobook",
                    Title = "Jack and Jill: Dramatic reading",
                    Subtitle = "Dramatic reading",
                    Publisher = "LibriVox",
                    Narrator = "",
                    AudioProductionType = null,
                    IsGraphicAudio = false, NarratorNames = new() {  }
                }, "Dramatized"),
                (new Edition
                {
                    ForeignEditionId = "gr:136233255-audiobook",
                    Title = "Little Women",
                    Subtitle = null,
                    Publisher = "Dramatization, BBC Radio Collection",
                    Narrator = "",
                    AudioProductionType = null,
                    IsGraphicAudio = false, NarratorNames = new() {  }
                }, "Dramatized"),
                (new Edition
                {
                    ForeignEditionId = "gr:60387317-audiobook",
                    Title = "Little Men (Dramatic Reading)",
                    Subtitle = null,
                    Publisher = "Librivox",
                    Narrator = "Jasmin Salma",
                    AudioProductionType = null,
                    IsGraphicAudio = false, NarratorNames = new() { "Jasmin Salma", "Jeanne Viray", "MichelleHannah" }
                }, "Full Cast"),
                (new Edition
                {
                    ForeignEditionId = "az:B092NZ1K9Y-audiobook", Title = "The Christmas Pig", Subtitle = null,
                    Publisher = "Audible Studios", Narrator = "Amaka Okafor", IsGraphicAudio = false, AudioProductionType = null,
                    NarratorNames = new() { "Amaka Okafor", "Annette Badland", "Clare Corbett", "Gerard McDermott", "Gerran Howell",
                        "Jemima Penny", "Karen Bartke", "Nicholas Boulton", "Rachel Atkins", "Rocco Padden", "Saffron Coomber",
                        "Sandra Gayer", "Summer Rose", "Tom Alexander", "Yasmin Mwanza" }
                }, "Full Cast"),
                (new Edition
                {
                    ForeignEditionId = "gr:229457338-audiobook",
                    Title = "Edgar Allan Poe's Stories and Tales I", Subtitle = null,
                    Publisher = "Monterey SoundWorks", Narrator = "full cast",
                    NarratorNames = new() { "full cast" }, IsGraphicAudio = false, AudioProductionType = null
                }, "Full Cast"),
                (new Edition
                {
                    ForeignEditionId = "gr:242244580-audiobook",
                    Title = "How to Sell a Haunted House [Dramatized Adaptation]",
                    Subtitle = null,
                    Publisher = "Blackstone Publishing, Inc.",
                    Narrator = "",
                    AudioProductionType = null,
                    IsGraphicAudio = false, NarratorNames = new() {  }
                }, "Dramatized"),
                (new Edition
                {
                    ForeignEditionId = "az:1624053629-audiobook",
                    Title = "The Magician's Nephew (Radio Theatre)",
                    Subtitle = null,
                    Publisher = "Tyndale Entertainment",
                    Narrator = "",
                    AudioProductionType = null,
                    IsGraphicAudio = false, NarratorNames = new() {  }
                }, "Dramatized"),
                (new Edition
                {
                    ForeignEditionId = "az:B002V1OOE4-audiobook",
                    Title = "The Weird Circle",
                    Subtitle = "The Cask of Amontillado (Dramatized)",
                    Publisher = "Radio Spirits, Inc.",
                    Narrator = "uncredited",
                    AudioProductionType = null,
                    IsGraphicAudio = false, NarratorNames = new() { "uncredited" }
                }, "Dramatized"),
                (new Edition
                {
                    ForeignEditionId = "az:B004F3EOLE-audiobook",
                    Title = "The Chronicles Of Narnia: The Magician's Nephew",
                    Subtitle = "A BBC Radio 4 Full-Cast Dramatisation",
                    Publisher = "BBC Digital Audio",
                    Narrator = "Full Cast",
                    AudioProductionType = null,
                    IsGraphicAudio = false, NarratorNames = new() { "Full Cast", "Maurice Denham", "Robert Eddison", "Rosemary Martin", "Rowena Snaith", "Simeon Pearl", "Stephen Thorne" }
                }, "Full Cast"),
                (new Edition
                {
                    ForeignEditionId = "gr:239425557-audiobook",
                    Title = "How to Sell a Haunted House",
                    Subtitle = null,
                    Publisher = "GraphicAudio",
                    Narrator = "Bradley Foster Smith",
                    AudioProductionType = null,
                    IsGraphicAudio = false, NarratorNames = new() { "Bradley Foster Smith", "Dawn Ursula", "Holly Adams", "Jonathan David Bullock", "Julie-Ann Elliott", "Kelly Baskin", "Laura C. Harris", "Megan Poppy", "Rose Elizabeth Supan", "Stephanie Németh-Parker", "Su Ling Chan", "full cast" }
                }, "GraphicAudio"),
                (new Edition
                {
                    ForeignEditionId = "gr:57139731-audiobook",
                    Title = "Good to Great: Why Some Companies Make the Leap...And Others Don't",
                    Subtitle = "Why Some Companies Make the Leap...And Others Don't",
                    Publisher = "HarperAudio",
                    Narrator = "Jim Collins",
                    AudioProductionType = null,
                    IsGraphicAudio = false, NarratorNames = new() { "Jim Collins" }
                }, null)
            };
            foreach (var sample in editions)
            foreach (var postgres in new[] { false, true })
            foreach (var pinCurrent in new[] { false, true })
                yield return new TestCaseData(postgres, pinCurrent, sample.Edition, sample.Label)
                    .SetName($"captured_edition_recording_correction({sample.Edition.ForeignEditionId},{postgres},{pinCurrent})");
        }

        [TestCaseSource(nameof(CapturedRecordingEditions))]
        public void captured_edition_recording_correction(bool postgres, bool pinCurrent, Edition captured, string label)
        {
            VerifyEditionRecordingCorrection(postgres, pinCurrent, selected =>
            {
                selected.Title = captured.Title;
                selected.Subtitle = captured.Subtitle;
                selected.Publisher = captured.Publisher;
                selected.Narrator = captured.Narrator;
                selected.NarratorNames = captured.NarratorNames;
                selected.IsGraphicAudio = captured.IsGraphicAudio;
                selected.AudioProductionType = captured.AudioProductionType;
            }, label != null, label);
        }

        [Test]
        public void facade_id_put_applies_the_same_explicit_recording_correction([Values(false, true)] bool postgres)
        {
            VerifyEditionRecordingCorrection(postgres, true, selected =>
            {
                selected.Title = "How to Sell a Haunted House [Dramatized Adaptation]";
                selected.Publisher = "Blackstone Publishing, Inc.";
            }, true, "Dramatized", facade: true);
        }

        [Test]
        public void edition_correction_uses_stored_metadata_when_the_client_omits_or_changes_it(
            [Values(false, true)] bool postgres, [Values(false, true)] bool facade,
            [Values(false, true)] bool nonStandard)
        {
            WithEndpoint(postgres, facade, false, (_, _, editions, book, _) =>
            {
                var selected = book.Editions.Single(edition => edition.Monitored);
                selected.Title = nonStandard ? "Title [Dramatized Adaptation]" : "Title";
                selected.Publisher = "Publisher";
                editions.Update(selected);
            }, fileTest: (controller, _, files, book) =>
            {
                var request = controller.GetBookById(book.Id).Value;
                request.AnyEditionOk = false;
                request.Editions = book.Editions.ToResource(controller.HttpContext.GetReadarrFacadeContext());
                foreach (var edition in request.Editions)
                {
                    edition.ReadingFormatId = null;
                    edition.Title = nonStandard ? "Title" : "Title [Dramatized Adaptation]";
                    edition.Subtitle = null;
                    edition.Publisher = nonStandard ? null : "GraphicAudio";
                    edition.NarratorNames = null;
                    edition.Narrator = null;
                }

                controller.UpdateBook(request);
                Assert.That(files.GetFilesByBook(book.Id).Single().AudioProductionOverride, Is.EqualTo(nonStandard));
            });
        }

        [Test]
        public void compatibility_body_put_can_reset_recording_corrections_to_automatic(
            [Values(false, true)] bool postgres, [Values(false, true)] bool facade)
        {
            WithEndpoint(postgres, facade, false, null, fileTest: (controller, _, files, book) =>
            {
                var file = files.GetFilesByBook(book.Id).Single();
                var request = controller.GetBookById(book.Id).Value;
                request.AnyEditionOk = false;
                controller.UpdateBook(request);
                Assert.That(files.Get(file.Id).AudioProductionOverride, Is.False);

                request = controller.GetBookById(book.Id).Value;
                request.AnyEditionOk = true;
                controller.UpdateBookByBody(request);
                Assert.That(files.Get(file.Id).AudioProductionOverride, Is.Null);
                Assert.That(files.Get(file.Id).EditionId, Is.EqualTo(file.EditionId));
            });
        }

        [Test]
        public void edition_correction_uses_the_book_media_type_for_legacy_untyped_files(
            [Values(false, true)] bool postgres)
        {
            WithEndpoint(postgres, false, true, (_, _, editions, book, _) =>
            {
                var target = book.Editions.Single(edition => !edition.Monitored);
                target.Title = "Title [Dramatized Adaptation]";
                editions.Update(target);
            }, fileTest: (controller, _, files, book) =>
            {
                var originalId = book.Editions.Single(edition => edition.Monitored).Id;
                var targetId = book.Editions.Single(edition => !edition.Monitored).Id;
                var file = files.Insert(new BookFile
                {
                    EditionId = originalId, Path = "/fixture/legacy.m4b", MediaType = "",
                    Quality = new QualityModel(Quality.M4B), MediaInfo = new MediaInfoModel(),
                    DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow, AudioProductionOverride = false
                });
                var request = controller.GetBookById(book.Id).Value;
                request.AnyEditionOk = false;
                request.Editions = book.Editions.ToResource();
                request.Editions.ForEach(edition => edition.Monitored = edition.Id == targetId);
                controller.UpdateBook(request);
                Assert.That(files.Get(file.Id).EditionId, Is.EqualTo(targetId));
                Assert.That(files.Get(file.Id).AudioProductionOverride, Is.True);
            });
        }

        [Test]
        public void compatibility_body_put_does_not_apply_the_explicit_edition_correction(
            [Values(false, true)] bool postgres, [Values(false, true)] bool facade)
        {
            var originalId = 0;
            WithEndpoint(postgres, facade, false, (controller, _, _, book, _) =>
            {
                originalId = book.Editions.Single(edition => edition.Monitored).Id;
                var request = controller.GetBookById(book.Id).Value;
                request.AnyEditionOk = false;
                request.Editions = book.Editions.ToResource();
                request.Editions.ForEach(edition => edition.Monitored = edition.Id != originalId);
                controller.UpdateBookByBody(request);
            }, fileTest: (_, _, files, book) =>
            {
                var saved = files.GetFilesByBook(book.Id).Single();
                Assert.That(saved.EditionId, Is.EqualTo(originalId));
                Assert.That(saved.AudioProductionOverride, Is.Null);
            });
        }

        [Test]
        public void rejected_recording_reset_rolls_back_enabling_automatic_edition_selection([Values(false, true)] bool postgres)
        {
            WithEndpoint(postgres, false, false, (controller, books, _, book, _) =>
            {
                var request = controller.GetBookById(book.Id).Value;
                request.AnyEditionOk = true;
                var error = Assert.Catch<Exception>(() => controller.UpdateBook(request));
                Assert.That(error.ToString(), Does.Contain(postgres ? "test_reset_rejected" : "reset rejected"));
                Assert.That(books.Get(book.Id).AnyEditionOk, Is.False);
            }, fileTest: (_, _, files, book) =>
            {
                Assert.That(files.GetFilesByBook(book.Id).Single().AudioProductionOverride, Is.True);
            }, beforeTest: (database, book) =>
            {
                using var connection = database.OpenConnection();
                Dapper.SqlMapper.Execute(connection, "UPDATE \"Books\" SET \"AnyEditionOk\" = @value WHERE \"Id\" = @id", new { value = false, id = book.Id });
                Dapper.SqlMapper.Execute(connection, "UPDATE \"BookFiles\" SET \"AudioProductionOverride\" = @value", new { value = true });
                if (postgres)
                    Dapper.SqlMapper.Execute(connection, "ALTER TABLE \"BookFiles\" ADD CONSTRAINT test_reset_rejected CHECK (\"AudioProductionOverride\" IS NOT NULL)");
                else
                    Dapper.SqlMapper.Execute(connection, "CREATE TRIGGER test_reset_rejected BEFORE UPDATE OF AudioProductionOverride ON BookFiles WHEN NEW.AudioProductionOverride IS NULL BEGIN SELECT RAISE(ABORT, 'reset rejected'); END");
            });
        }

        [Test]
        public void picking_a_non_audio_edition_leaves_recording_corrections_unchanged(
            [Values(false, true)] bool postgres, [Values(EditionReadingFormat.Print, EditionReadingFormat.Ebook)] int format)
        {
            WithEndpoint(postgres, false, false, (controller, _, editions, book, _) =>
            {
                var target = book.Editions.Single(edition => !edition.Monitored);
                target.ReadingFormatId = format;
                target.Title = "Title [Dramatized Adaptation]";
                editions.Update(target);
                var request = controller.GetBookById(book.Id).Value;
                request.AnyEditionOk = false;
                request.Editions = editions.FindByBook(new[] { book.Id }).ToResource();
                request.Editions.ForEach(edition => edition.Monitored = edition.Id == target.Id);
                controller.UpdateBook(request);
            }, fileTest: (_, _, files, book) =>
            {
                var saved = files.GetFilesByBook(book.Id).Single();
                Assert.That(saved.EditionId, Is.EqualTo(book.Editions.Single(edition => edition.Monitored).Id));
                Assert.That(saved.AudioProductionOverride, Is.Null);
            }, beforeTest: (database, _) =>
            {
                using var connection = database.OpenConnection();
                Dapper.SqlMapper.Execute(connection, "UPDATE \"BookFiles\" SET \"AudioProductionOverride\" = @value", new { value = (bool?)null });
            });
        }

        [Test]
        public void legacy_blank_media_does_not_inherit_a_conflicting_correction_parameter(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType mediaType)
        {
            Func<System.Data.IDbConnection> open = null;
            WithEndpoint(postgres, false, true, null, mediaType, fileTest: (_, _, files, book) =>
            {
                var original = book.Editions.Single(edition => edition.Monitored);
                var target = book.Editions.Single(edition => !edition.Monitored);
                var file = files.Insert(new BookFile
                {
                    EditionId = original.Id, Path = "/fixture/legacy-file", MediaType = "",
                    Quality = new QualityModel(mediaType == BookMediaType.Audiobook ? Quality.M4B : Quality.EPUB),
                    MediaInfo = new MediaInfoModel(), DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow,
                    AudioProductionOverride = false
                });
                using var connection = open();
                using var transaction = connection.BeginTransaction();
                var conflictingMedia = mediaType == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook;
                Assert.That(files.RelinkAndClassifyFilesByBook(book.Id, target.Id, conflictingMedia,
                    AudioProductionCorrection.NonStandard, connection, transaction), Is.EqualTo((0, 0)));
                transaction.Commit();
                Assert.That(files.Get(file.Id).EditionId, Is.EqualTo(original.Id));
                Assert.That(files.Get(file.Id).AudioProductionOverride, Is.False);
            }, beforeTest: (database, _) => open = database.OpenConnection);
        }

        [Test]
        public void repeated_repository_edition_correction_writes_only_actual_changes([Values(false, true)] bool postgres)
        {
            Func<System.Data.IDbConnection> open = null;
            WithEndpoint(postgres, false, false, null, fileTest: (_, _, files, book) =>
            {
                var editionId = book.Editions.Single(edition => !edition.Monitored).Id;
                using var connection = open();
                using var transaction = connection.BeginTransaction();
                Assert.That(files.RelinkAndClassifyFilesByBook(book.Id, editionId, BookMediaType.Audiobook, AudioProductionCorrection.NonStandard,
                    connection, transaction), Is.EqualTo((1, 1)));
                Assert.That(files.RelinkAndClassifyFilesByBook(book.Id, editionId, BookMediaType.Audiobook, AudioProductionCorrection.NonStandard,
                    connection, transaction), Is.EqualTo((0, 0)));
                transaction.Commit();
            }, beforeTest: (database, _) => open = database.OpenConnection);
        }

        [Test]
        public void book_file_api_can_explicitly_restore_automatic_recording_detection([Values(false, true)] bool postgres)
        {
            WithEndpoint(postgres, false, false, null, fileTest: (_, _, files, book) =>
            {
                var file = files.GetFilesByBook(book.Id).Single();
                file.AudioProductionOverride = true;
                files.SetFields(file, item => item.AudioProductionOverride);
                var media = Proxy<IMediaFileService>((method, args) =>
                {
                    if (method.Name == nameof(IMediaFileService.Get)) return files.Get((int)args[0]);
                    if (method.Name == nameof(IMediaFileService.Update)) { files.Update((BookFile)args[0]); return null; }
                    throw new NotImplementedException(method.Name);
                });
                var controller = new BookFileController(null, media, null, null, null, null, null, null, LogManager.GetCurrentClassLogger(), null);
                var request = System.Text.Json.JsonSerializer.Deserialize<BookFileResource>(
                    "{\"id\":" + file.Id + ",\"audioProductionOverride\":null}", new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                Assert.That(request.AudioProductionOverrideSpecified, Is.True);
                controller.SetQuality(request);
                Assert.That(files.Get(file.Id).AudioProductionOverride, Is.Null);
            });
        }

        [Test]
        public void edition_correction_leaves_wrong_media_files_on_the_same_row_untouched([Values(false, true)] bool postgres)
        {
            WithEndpoint(postgres, false, true, (_, _, editions, book, _) =>
            {
                var target = book.Editions.Single(edition => !edition.Monitored);
                target.Title = "How to Sell a Haunted House [Dramatized Adaptation]";
                target.Publisher = "Blackstone Publishing, Inc.";
                editions.Update(target);
            }, fileTest: (controller, _, files, book) =>
            {
                var originalId = book.Editions.Single(edition => edition.Monitored).Id;
                var otherMedia = files.Insert(new BookFile
                {
                    EditionId = originalId, Path = "/fixture/associated.epub", MediaType = "ebook",
                    Quality = new QualityModel(Quality.EPUB), MediaInfo = new MediaInfoModel(),
                    DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow, AudioProductionOverride = false
                });
                var request = controller.GetBookById(book.Id).Value;
                request.AnyEditionOk = false;
                request.Editions = book.Editions.ToResource();
                request.Editions.ForEach(edition => edition.Monitored = edition.Id != originalId);
                controller.UpdateBook(request);
                var saved = files.Get(otherMedia.Id);
                Assert.That(saved.EditionId, Is.EqualTo(originalId));
                Assert.That(saved.AudioProductionOverride, Is.False);
                Assert.That(saved.Path, Is.EqualTo(otherMedia.Path));
            });
        }

        [Test]
        public void edition_recording_correction_is_limited_to_the_audiobook_row(
            [Values(false, true)] bool postgres,
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType mediaType)
        {
            var otherEditionIds = new List<int>();
            WithEndpoint(postgres, false, true, (_, books, editions, book, _) =>
            {
                var target = book.Editions.Single(edition => !edition.Monitored);
                target.AudioProductionType = "Dramatized";
                editions.Update(target);
                foreach (var side in new[] { BookMediaType.Audiobook, BookMediaType.Ebook })
                {
                    var other = books.Insert(new Book { AuthorId = book.AuthorId, Title = book.Title, CleanTitle = book.CleanTitle,
                        TitleSlug = "other-" + side, MediaType = side, Added = DateTime.UtcNow });
                    otherEditionIds.Add(editions.Insert(new Edition { BookId = other.Id, Title = book.Title,
                        ForeignEditionId = "hc:edition:other-" + side, Monitored = true }).Id);
                }
            }, mediaType, fileTest: (controller, _, files, book) =>
            {
                BookFile Insert(int editionId, bool initialClass) => files.Insert(new BookFile
                {
                    EditionId = editionId, Path = $"/fixture/{editionId}.dat", AudioProductionOverride = initialClass,
                    MediaType = mediaType.ToString().ToLowerInvariant(),
                    Quality = new QualityModel(mediaType == BookMediaType.Audiobook ? Quality.M4B : Quality.EPUB),
                    MediaInfo = new MediaInfoModel(), DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow
                });
                var own = Insert(book.Editions.Single(edition => edition.Monitored).Id, false);
                var others = otherEditionIds.Select(id => Insert(id, false)).ToList();
                var request = controller.GetBookById(book.Id).Value;
                request.AnyEditionOk = false;
                request.Editions = book.Editions.ToResource();
                request.Editions.ForEach(edition => edition.Monitored = !edition.Monitored);
                controller.UpdateBook(request);
                Assert.That(files.Get(own.Id).AudioProductionOverride, Is.EqualTo(mediaType == BookMediaType.Audiobook));
                var automatic = controller.GetBookById(book.Id).Value;
                automatic.AnyEditionOk = true;
                controller.UpdateBook(automatic);
                Assert.That(files.Get(own.Id).AudioProductionOverride, Is.EqualTo(mediaType == BookMediaType.Audiobook ? (bool?)null : false));
                foreach (var other in others)
                {
                    var saved = files.Get(other.Id);
                    Assert.That(saved.EditionId, Is.EqualTo(other.EditionId));
                    Assert.That(saved.AudioProductionOverride, Is.False);
                }
            });
        }
    }
}
