using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Chaptarr.Api.V1.Config;
using Chaptarr.Http.ErrorManagement;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Organizer.NamingPattern;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Organizer
{
    [TestFixture]
    public partial class FileNameBuilderSeriesTokensFixture
    {
        private class StubNamingConfigService : INamingConfigService
        {
            public int SaveCount { get; private set; }
            public NamingConfig GetConfig() => NamingConfig.Default;
            public void Save(NamingConfig namingConfig) => SaveCount++;
        }

        private class StubQualityDefinitionService : IQualityDefinitionService
        {
            public void Update(QualityDefinition qualityDefinition) => throw new NotImplementedException();
            public void UpdateMany(List<QualityDefinition> qualityDefinitions) => throw new NotImplementedException();
            public List<QualityDefinition> All() => throw new NotImplementedException();
            public QualityDefinition GetById(int id) => throw new NotImplementedException();
            public QualityDefinition Get(Quality quality) => new QualityDefinition(quality);
        }

        private class StubCustomFormatCalculationService : ICustomFormatCalculationService
        {
            public List<CustomFormat> ParseCustomFormat(RemoteBook remoteBook, long size) => new();
            public List<CustomFormat> ParseCustomFormat(BookFile bookFile, Author artist) => new();
            public List<CustomFormat> ParseCustomFormat(BookFile bookFile) => new();
            public List<CustomFormat> ParseCustomFormat(Blocklist blocklist, Author artist) => new();
            public List<CustomFormat> ParseCustomFormat(EntityHistory history, Author artist) => new();
            public List<CustomFormat> ParseCustomFormat(LocalBook localBook) => new();
        }

        [TestCase("1", "00", "01")]
        [TestCase("3", "000", "003")]
        [TestCase("10", "00", "10")]
        [TestCase("123", "00", "123")]
        [TestCase("0", "00", "00")]
        [TestCase("2.5", "00", "02.5")]
        [TestCase("0.50", "000", "000.50")]
        [TestCase("-2.5", "00", "-02.5")]
        [TestCase("+2", "00", "+02")]
        [TestCase("003", "00", "003")]
        [TestCase("1234567890123456789012345678901234567890", "00", "1234567890123456789012345678901234567890")]
        [TestCase("2.5", null, "2.5")]
        [TestCase("3", "0", "3")]
        [TestCase("3", "bogus", "3")]
        [TestCase("3", "smart", "3")]
        [TestCase("1-3", "00", "1-3")]
        [TestCase("1,2,3", "00", "1,2,3")]
        [TestCase("3.5,4.5,6.7", "00", "3.5,4.5,6.7")]
        [TestCase("2A", "00", "2A")]
        [TestCase("Part 1", "00", "Part 1")]
        [TestCase("序章", "00", "序章")]
        [TestCase("2,5", "00", "2,5")]
        [TestCase("2.5.1", "00", "2.5.1")]
        [TestCase(null, "00", null)]
        [TestCase("", "00", "")]
        [SetCulture("fr-FR")]
        public void should_zero_pad_numeric_series_positions_without_changing_their_meaning(string position, string format, string expected)
        {
            var builder = CreateBuilder();
            var author = new Author { Name = "Jim Butcher" };
            var book = new Book { Author = author, SeriesLinks = SeriesLinks("Dresden Files", position) };
            var edition = new Edition { Title = "Grave Peril", Book = book };
            var suffix = format == null ? "" : ":" + format;
            var pattern = "{Book Series}/Book {Book SeriesPosition" + suffix + " - }{Book Title}";

            foreach (var mediaType in new[] { "audiobook", "ebook" })
            {
                var config = NamingConfig.Default;
                config.RenameBooks = true;
                config.EbookRenameBooks = true;
                config.StandardBookFormat = pattern;
                config.EbookStandardBookFormat = pattern;
                var file = new BookFile
                {
                    Path = @"C:\books\Grave Peril.m4b".AsOsAgnostic(),
                    MediaType = mediaType,
                    Quality = new QualityModel(mediaType == "ebook" ? Quality.EPUB : Quality.M4B)
                };

                var result = builder.BuildBookFileName(author, edition, file, config, new List<CustomFormat>());

                var positionPart = string.IsNullOrEmpty(expected) ? "" : expected + " - ";
                Assert.That(result, Is.EqualTo(Path.Combine("Dresden Files", "Book " + positionPart + "Grave Peril")), mediaType);
            }
        }

        [TestCase("audiobook", "3", "03, Grave Peril")]
        [TestCase("ebook", "3", "03, Grave Peril")]
        [TestCase("audiobook", "2.5", "02.5, Grave Peril")]
        [TestCase("ebook", "2.5", "02.5, Grave Peril")]
        [TestCase("audiobook", "1,2,3", "1,2,3, Grave Peril")]
        [TestCase("ebook", "1,2,3", "1,2,3, Grave Peril")]
        [TestCase("audiobook", null, "Grave Peril")]
        [TestCase("ebook", null, "Grave Peril")]
        public void series_position_format_preserves_comma_suffix(string mediaType, string position, string expected)
        {
            var author = new Author { Name = "Jim Butcher" };
            var edition = new Edition
            {
                Title = "Grave Peril",
                Book = new Book { Author = author, SeriesLinks = SeriesLinks("Dresden Files", position) }
            };
            var config = NamingConfig.Default;
            config.RenameBooks = config.EbookRenameBooks = true;
            config.StandardBookFormat = config.EbookStandardBookFormat = "{Book SeriesPosition:00, }{Book Title}";
            var file = new BookFile
            {
                Path = "/downloads/Grave Peril.m4b",
                MediaType = mediaType,
                Quality = new QualityModel(mediaType == "ebook" ? Quality.EPUB : Quality.M4B)
            };

            Assert.That(CreateBuilder().BuildBookFileName(author, edition, file, config, new List<CustomFormat>()),
                Is.EqualTo(expected));
        }

        [TestCase("audiobook")]
        [TestCase("ebook")]
        public void should_render_padded_series_position_in_naming_examples_from_series_links(string mediaType)
        {
            var builder = CreateBuilder();
            var controller = new NamingConfigController(new StubNamingConfigService(),
                new FileNameSampleService(builder), new FileNameValidationService(new FileNameSampleService(builder)), builder);
            var pattern = "{Book Series}/{Book SeriesPosition:00 - }{Book Title}";
            var config = new NamingConfigResource
            {
                Id = 1,
                RenameBooks = true,
                EbookRenameBooks = true,
                StandardBookFormat = mediaType == "audiobook" ? pattern : "{Book Title}",
                EbookStandardBookFormat = mediaType == "ebook" ? pattern : "{Book Title}"
            };

            var examples = (NamingExampleResource)controller.GetExamples(config, mediaType);

            Assert.That(examples.SingleBookExample, Is.EqualTo(Path.Combine("The Dresden Files", "03 - Grave Peril")));
        }

        [TestCase("audiobook", "Audio", "MP3", true)]
        [TestCase("ebook", "Text", "EPUB", false)]
        public void naming_examples_should_use_the_selected_scheme_and_media_metadata(string mediaType, string folder, string quality, bool hasNarrator)
        {
            var builder = CreateBuilder();
            var controller = new NamingConfigController(new StubNamingConfigService(),
                new FileNameSampleService(builder), new FileNameValidationService(new FileNameSampleService(builder)), builder);
            var config = NamingConfig.Default;
            config.Id = 1;
            config.RenameBooks = config.EbookRenameBooks = true;
            config.StandardBookFormat = "Audio/{Book Title}{ - Quality Title}{ - Narrator}{ (PartNumber:smart)}";
            config.EbookStandardBookFormat = "Text/{Book Title}{ - Quality Title}{ - Narrator}{ (PartNumber:smart)}";
            config.AuthorFolderFormat = "Audio/{Author Name}";
            config.EbookAuthorFolderFormat = "Text/{Author Name}";

            var result = (NamingExampleResource)controller.GetExamples(config.ToResource(), mediaType);
            var name = "Grave Peril - " + quality + (hasNarrator ? " - James Marsters" : "");
            Assert.That(result.SingleBookExample, Is.EqualTo(Path.Combine(folder, name)));
            Assert.That(result.MultiPartBookExample, Is.EqualTo(Path.Combine(folder, name + " (01)")));
            Assert.That(result.AuthorFolderExample, Is.EqualTo(Path.Combine(folder, "Jim Butcher")));
        }

        [TestCase("audiobook", true, "Q")]
        [TestCase("ebook", true, "Q")]
        [TestCase("audiobook", false, "Q")]
        [TestCase("ebook", false, "Q")]
        [TestCase("audiobook", true, "bogus")]
        [TestCase("ebook", true, "bogus")]
        public async Task invalid_multipart_format_should_be_a_field_error_and_never_saved(string mediaType, bool rename, string format)
        {
            var builder = CreateBuilder();
            var service = new StubNamingConfigService();
            var controller = new NamingConfigController(service, new FileNameSampleService(builder), new FileNameValidationService(new FileNameSampleService(builder)), builder);
            var config = NamingConfig.Default;
            config.Id = 1;
            config.RenameBooks = config.EbookRenameBooks = rename;
            if (mediaType == "ebook")
            {
                config.EbookStandardBookFormat = "{Book Title}{ (PartNumber:" + format + ")}";
            }
            else
            {
                config.StandardBookFormat = "{Book Title}{ (PartNumber:" + format + ")}";
            }

            var exception = Assert.Throws<ValidationException>(() => controller.UpdateNamingConfig(config.ToResource()));
            Assert.That(exception.Errors.Select(e => e.PropertyName), Is.EquivalentTo(new[]
            {
                mediaType == "ebook" ? "EbookStandardBookFormat" : "StandardBookFormat"
            }));
            Assert.That(service.SaveCount, Is.Zero);
            var context = new DefaultHttpContext();
            context.Response.Body = new MemoryStream();
            context.Features.Set<IExceptionHandlerPathFeature>(new ExceptionHandlerFeature { Error = exception });
            await new ChaptarrErrorPipeline(LogManager.GetCurrentClassLogger()).HandleException(context);
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
            context.Response.Body.Position = 0;
            using var json = await JsonDocument.ParseAsync(context.Response.Body);
            Assert.That(json.RootElement[0].GetProperty("propertyName").GetString(),
                Is.EqualTo(mediaType == "ebook" ? "EbookStandardBookFormat" : "StandardBookFormat"));
            var examples = (NamingExampleResource)controller.GetExamples(config.ToResource(), mediaType);
            Assert.That(examples.MultiPartBookExample, Is.Null);
        }

        [TestCase("audiobook", "{Book Title}{ (PartNumber:00)}")]
        [TestCase("audiobook", "{Book Title}{ (PartNumber:smart)}")]
        [TestCase("audiobook", "{Book Title}{ (PartNumber:bogus)}{ (PartNumber:00)}")]
        [TestCase("audiobook", "{Original Title}")]
        [TestCase("ebook", "{Book Title}{ (PartNumber:bogus)}{ (PartNumber:00)}")]
        [TestCase("ebook", "{Original Title}")]
        public void valid_multipart_formats_should_be_saved_when_complete_sample_names_differ(string mediaType, string pattern)
        {
            var builder = CreateBuilder();
            var service = new StubNamingConfigService();
            var controller = new NamingConfigController(service, new FileNameSampleService(builder), new FileNameValidationService(new FileNameSampleService(builder)), builder);
            var config = NamingConfig.Default;
            config.Id = 1;
            if (mediaType == "ebook")
            {
                config.EbookStandardBookFormat = pattern;
            }
            else
            {
                config.StandardBookFormat = pattern;
            }

            Assert.DoesNotThrow(() => controller.UpdateNamingConfig(config.ToResource()));
            Assert.That(service.SaveCount, Is.EqualTo(1));
        }

        [TestCase("Q", false)]
        [TestCase("00", true)]
        [TestCase("smart", true)]
        [TestCase("bogus", false)]
        public void visual_builder_should_validate_multipart_formats(string format, bool valid)
        {
            var service = new NamingPatternService(new PatternCompiler(), CreateBuilder());
            var result = service.ValidatePattern("{Book Title}{ (PartNumber:" + format + ")}");
            Assert.That(result.IsValid, Is.EqualTo(valid));
            var controller = new NamingPatternController(service);
            var response = JsonSerializer.SerializeToElement(controller.Validate(new ValidateRequest
            {
                Pattern = "{Book Title}{ (PartNumber:" + format + ")}"
            }));
            Assert.That(response.GetProperty("ok").GetBoolean(), Is.EqualTo(valid));
        }

        [TestCase("{Book Title}{ (PartNumber:bogus)}", false)]
        [TestCase("{Book Title}{ (PartNumber:bogus)}{ (PartNumber:00)}", true)]
        [TestCase("{Book Title}{ (PartNumber:00)}", true)]
        [TestCase("{Book Title}{ (PartNumber:smart)}", true)]
        [TestCase("{Original Title}", true)]
        public void visual_builder_should_validate_complete_multipart_sample_names(string pattern, bool valid)
        {
            var service = new NamingPatternService(new PatternCompiler(), CreateBuilder());
            var result = service.ValidatePattern(pattern);
            Assert.That(result.IsValid, Is.EqualTo(valid));
        }

        [TestCase("MP3", "audiobook")]
        [TestCase("EPUB", "ebook")]
        public void pattern_preview_should_render_series_tokens_for_the_sample_media_type(string quality, string mediaType)
        {
            var service = new NamingPatternService(new PatternCompiler(), CreateBuilder());
            var result = service.PreviewPattern("{Book SeriesTitle}/{Book Title}", sampleContext: new SampleContext
            {
                AuthorName = "Jim Butcher",
                BookTitle = "Grave Peril",
                SeriesTitle = "Dresden Files",
                SeriesPosition = "3",
                Quality = quality
            });

            Assert.That(result.Path, Is.EqualTo(Path.Combine("Dresden Files #3", "Grave Peril")), mediaType);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void filename_builder_should_keep_media_schemes_and_rename_switches_independent(bool renameAudio)
        {
            var builder = CreateBuilder();
            var author = new Author { Name = "Jim Butcher" };
            var edition = new Edition { Title = "Grave Peril", Book = new Book { Author = author } };
            var config = NamingConfig.Default;
            config.RenameBooks = renameAudio;
            config.EbookRenameBooks = !renameAudio;
            config.StandardBookFormat = "Audio/{Book Title}";
            config.EbookStandardBookFormat = "Text/{Book Title}";
            foreach (var mediaType in new[] { "audiobook", "ebook" })
            {
                var file = new BookFile
                {
                    Path = @"C:\books\original-file.epub".AsOsAgnostic(),
                    MediaType = mediaType,
                    Quality = new QualityModel(mediaType == "ebook" ? Quality.EPUB : Quality.MP3)
                };
                var rename = mediaType == "ebook" ? !renameAudio : renameAudio;
                Assert.That(builder.BuildBookFileName(author, edition, file, config, new List<CustomFormat>()),
                    Is.EqualTo(Path.Combine(mediaType == "ebook" ? "Text" : "Audio", rename ? edition.Title : "original-file")));
            }
        }

        [Test]
        public void samples_should_not_leak_metadata_between_media_types_or_calls()
        {
            var service = new FileNameSampleService(CreateBuilder());
            var config = NamingConfig.Default;
            config.RenameBooks = config.EbookRenameBooks = true;
            config.StandardBookFormat = config.EbookStandardBookFormat = "{Book Title}{ - Quality Title}{ - Narrator}{ (PartNumber:00)}";
            var audio = service.GetMultiDiscTrackSample(config, "audiobook");
            var ebook = service.GetMultiDiscTrackSample(config, "ebook");
            Assert.That(audio.BookFile.PartCount, Is.EqualTo(12));
            Assert.That(ebook.BookFile.PartCount, Is.EqualTo(12));
            Assert.That(ebook.BookFile.MediaType, Is.EqualTo("ebook"));
            Assert.That(ebook.BookFile.MediaInfo, Is.Null);
            Assert.That(ebook.BookFile.Edition.NarratorNames, Is.Empty);
            audio.BookFile.Edition.Title = "changed sample";
            Assert.That(service.GetMultiDiscTrackSample(config, "audiobook").FileName,
                Is.EqualTo("Grave Peril - MP3 - James Marsters (01)"));
            Assert.That(ebook.FileName, Is.EqualTo("Grave Peril - EPUB (01)"));
        }

        [TestCase("EPUB", "Grave Peril - EPUB")]
        [TestCase("M4B", "Grave Peril - M4B - Narrator Name")]
        public void pattern_preview_should_use_the_requested_quality_without_inventing_ebook_narrators(string quality, string expected)
        {
            var controller = new NamingPatternController(new NamingPatternService(new PatternCompiler(), CreateBuilder()));
            var result = controller.Preview(new PreviewRequest
            {
                Pattern = "{Book Title}{ - Quality Title}{ - Narrator}",
                Sample = new SampleContext { AuthorName = "Jim Butcher", BookTitle = "Grave Peril", Quality = quality }
            });
            Assert.That(JsonSerializer.SerializeToElement(result).GetProperty("path").GetString(), Is.EqualTo(expected));
        }

        [TestCase("audiobook", 1, "Shared.Release")]
        [TestCase("audiobook", 2, "chapter-1")]
        [TestCase("ebook", 1, "Shared.Release")]
        [TestCase("ebook", 2, "chapter-1")]
        public void original_release_title_should_not_bypass_multipart_filename_protection(string mediaType, int partCount, string expected)
        {
            var builder = CreateBuilder();
            var author = new Author { Name = "Jim Butcher" };
            var edition = new Edition { Title = "Grave Peril", Book = new Book { Author = author } };
            var config = NamingConfig.Default;
            config.RenameBooks = config.EbookRenameBooks = true;
            config.StandardBookFormat = "Audio/{Book Title}/{Original Title}";
            config.EbookStandardBookFormat = "Text/{Book Title}/{Original Title}";
            var file = new BookFile
            {
                Path = @"C:\downloads\chapter-1.mp3".AsOsAgnostic(),
                SceneName = "Shared.Release",
                MediaType = mediaType,
                Quality = new QualityModel(mediaType == "ebook" ? Quality.EPUB : Quality.MP3),
                Part = 1,
                PartCount = partCount
            };
            var result = builder.BuildBookFileName(author, edition, file, config, new List<CustomFormat>());
            Assert.That(result, Is.EqualTo(Path.Combine(mediaType == "ebook" ? "Text" : "Audio", edition.Title, expected)));
            if (partCount > 1)
            {
                file.Part = 2;
                file.Path = @"C:\downloads\chapter-2.mp3".AsOsAgnostic();
                Assert.That(builder.BuildBookFileName(author, edition, file, config, new List<CustomFormat>()),
                    Is.EqualTo(Path.Combine(mediaType == "ebook" ? "Text" : "Audio", edition.Title, "chapter-2")));
            }
        }

        [TestCase("Dresden Files", null, "Dresden Files #3")]
        [TestCase("Dresden Files", "3.5,4.5,6.7", "Dresden Files #3")]
        [TestCase(null, null, "Dresden Files #3")]
        [TestCase(null, "99", "Dresden Files #3")]
        public void series_membership_should_override_scalar_and_keep_name_and_position_paired(string name, string position, string expected)
        {
            var author = new Author { Name = "Jim Butcher" };
            var book = new Book
            {
                Author = author,
                SeriesName = name,
                SeriesPosition = position,
                SeriesLinks = new List<SeriesBookLink>
                {
                    new SeriesBookLink { Series = new Series { Title = "Other Series", GoodreadsSeriesId = "gr:1" }, Position = "1", SeriesPosition = 1 },
                    new SeriesBookLink { Series = new Series { Title = "Dresden Files", GoodreadsSeriesId = "gr:40346" }, Position = "3", SeriesPosition = 3, IsPrimary = true }
                }
            };
            var edition = new Edition { Title = "Grave Peril", Book = book };
            var config = NamingConfig.Default;
            config.RenameBooks = config.EbookRenameBooks = true;
            config.StandardBookFormat = config.EbookStandardBookFormat = "{Book SeriesTitle}";
            foreach (var mediaType in new[] { "audiobook", "ebook" })
            {
                var file = new BookFile { Path = @"C:\books\Grave Peril.epub".AsOsAgnostic(), MediaType = mediaType, Quality = new QualityModel(Quality.EPUB) };
                Assert.That(CreateBuilder().BuildBookFileName(author, edition, file, config, new List<CustomFormat>()), Is.EqualTo(expected));
            }
        }

        internal static FileNameBuilder CreateBuilder()
        {
            return new FileNameBuilder(new StubNamingConfigService(), new StubQualityDefinitionService(),
                new CacheManager(), new StubCustomFormatCalculationService(), LogManager.GetCurrentClassLogger());
        }

        private static List<SeriesBookLink> SeriesLinks(string name, string position) => new()
        {
            new SeriesBookLink { Position = position, IsPrimary = true,
                Series = new Series { Title = name, GoodreadsSeriesId = "gr:40346" } }
        };

        [Test]
        public void should_not_render_unbacked_scalar_series_tokens()
        {
            var cacheManager = new CacheManager();
            var builder = new FileNameBuilder(
                new StubNamingConfigService(),
                new StubQualityDefinitionService(),
                cacheManager,
                new StubCustomFormatCalculationService(),
                LogManager.GetCurrentClassLogger());

            var author = new Author { Name = "Jim Butcher" };
            var book = new Book
            {
                Author = author,
                SeriesName = "Dresden Files",
                SeriesPosition = "3"
            };
            var edition = new Edition { Title = "Grave Peril", Book = book };
            var bookFile = new BookFile
            {
                Path = "/books/Grave Peril.m4b",
                Quality = new QualityModel(Quality.Unknown)
            };

            var namingConfig = NamingConfig.Default;
            namingConfig.RenameBooks = true;
            namingConfig.StandardBookFormat = "{Book SeriesTitle}/{Book Title}";

            var result = builder.BuildBookFileName(author, edition, bookFile, namingConfig, customFormats: new List<CustomFormat>());

            Assert.That(result, Is.EqualTo("Grave Peril"));
        }

        [Test]
        public void should_drop_connector_only_series_path_segments_when_series_is_missing()
        {
            var cacheManager = new CacheManager();
            var builder = new FileNameBuilder(
                new StubNamingConfigService(),
                new StubQualityDefinitionService(),
                cacheManager,
                new StubCustomFormatCalculationService(),
                LogManager.GetCurrentClassLogger());

            var author = new Author { Name = "Jim Butcher" };
            var book = new Book { Author = author };
            var edition = new Edition
            {
                Title = "Standalone Book",
                Book = book,
                Narrator = "James Marsters"
            };

            var bookFile = new BookFile
            {
                Path = "/books/Standalone Book.m4b",
                Quality = new QualityModel(Quality.M4B)
            };

            var namingConfig = NamingConfig.Default;
            namingConfig.RenameBooks = true;
            namingConfig.StandardBookFormat = "{Author Name}/{Book Series}/{Book Series}{, Book SeriesPosition}/{Book Title}{ - Narrator}/{Book Title}{ - Narrator}";

            var result = builder.BuildBookFileName(author, edition, bookFile, namingConfig, customFormats: new List<CustomFormat>());

            Assert.That(result, Is.EqualTo(Path.Combine("Jim Butcher", "Standalone Book - James Marsters", "Standalone Book - James Marsters")));
        }

        [Test]
        public void should_keep_comma_connector_when_series_position_exists()
        {
            var cacheManager = new CacheManager();
            var builder = new FileNameBuilder(
                new StubNamingConfigService(),
                new StubQualityDefinitionService(),
                cacheManager,
                new StubCustomFormatCalculationService(),
                LogManager.GetCurrentClassLogger());

            var author = new Author { Name = "Jim Butcher" };
            var book = new Book
            {
                Author = author,
                SeriesName = "Dresden Files",
                SeriesPosition = "3",
                SeriesLinks = SeriesLinks("Dresden Files", "3")
            };
            var edition = new Edition
            {
                Title = "Grave Peril",
                Book = book,
                Narrator = "James Marsters"
            };

            var bookFile = new BookFile
            {
                Path = "/books/Grave Peril.m4b",
                Quality = new QualityModel(Quality.M4B)
            };

            var namingConfig = NamingConfig.Default;
            namingConfig.RenameBooks = true;
            namingConfig.StandardBookFormat = "{Author Name}/{Book Series}/{Book Series}{, Book SeriesPosition}/{Book Title}{ - Narrator}/{Book Title}{ - Narrator}";

            var result = builder.BuildBookFileName(author, edition, bookFile, namingConfig, customFormats: new List<CustomFormat>());

            Assert.That(result, Is.EqualTo(Path.Combine("Jim Butcher", "Dresden Files", "Dresden Files, 3", "Grave Peril - James Marsters", "Grave Peril - James Marsters")));
        }

        // Known gap: TrimSeparatorsRegex only collapses connectors at the anchored start/end of a
        // path segment. When a token in the MIDDLE of a segment is empty and dashes/commas live
        // BETWEEN braces (not as wrapping prefix/suffix), they dangle. The bidirectional comma-aware
        // trim added 2026-05-12 deliberately does not address this — it's a separate fix that would
        // need to extend FileNameCleanupRegex or add an interior-connector collapser. Pin the
        // current output so a future "this looks ugly" fix updates this test intentionally.
        [Test]
        public void mid_string_connectors_dangle_when_inner_token_is_empty()
        {
            var cacheManager = new CacheManager();
            var builder = new FileNameBuilder(
                new StubNamingConfigService(),
                new StubQualityDefinitionService(),
                cacheManager,
                new StubCustomFormatCalculationService(),
                LogManager.GetCurrentClassLogger());

            var author = new Author { Name = "Jim Butcher" };
            var book = new Book { Author = author };
            var edition = new Edition
            {
                Title = "Grave Peril",
                Book = book,
                Narrator = string.Empty
            };

            var bookFile = new BookFile
            {
                Path = "/books/Grave Peril.m4b",
                Quality = new QualityModel(Quality.M4B)
            };

            var namingConfig = NamingConfig.Default;
            namingConfig.RenameBooks = true;
            // Literal " - " BETWEEN braces (not as wrapping prefix/suffix). When {Narrator} is empty,
            // the surrounding " - " literals remain in the middle of the segment.
            namingConfig.StandardBookFormat = "{Author Name}/{Book Title} - {Narrator} - {Quality Title}";

            var result = builder.BuildBookFileName(author, edition, bookFile, namingConfig, customFormats: new List<CustomFormat>());

            var segments = result.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.None);
            Assert.That(segments[0], Is.EqualTo("Jim Butcher"));
            // Today's behavior: "Grave Peril -  - M4B" → cleanup collapses double-space → "Grave Peril - - M4B".
            // When mid-string dangling is fixed, update this assertion to "Grave Peril - M4B".
            Assert.That(segments[1], Is.EqualTo("Grave Peril - - M4B"));
        }
    }
}
