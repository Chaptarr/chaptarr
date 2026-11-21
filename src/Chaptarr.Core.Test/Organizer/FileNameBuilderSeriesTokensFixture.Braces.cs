using System;
using System.Collections.Generic;
using Chaptarr.Api.V1.Config;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Configuration.SettingsBackups;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Organizer.NamingPattern;
using NUnit.Framework;
using NLog;
using NLog.Config;
using NLog.Targets;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Organizer
{
    public partial class FileNameBuilderSeriesTokensFixture
    {
        [Test, Combinatorial]
        public void unmatched_naming_braces_are_refused_by_save_preview_and_restore(
            [Values(nameof(NamingConfig.StandardBookFormat), nameof(NamingConfig.AuthorFolderFormat),
                nameof(NamingConfig.EbookStandardBookFormat), nameof(NamingConfig.EbookAuthorFolderFormat))] string field,
            [Values("{Book Title} - {Smart Narrator:AudioProductionType,FullCast,Narrators}}/{Book Title}{ (PartNumber:smart)}",
                "{Book Title}{ (PartNumber:smart)}{", "}{Book Title}{ (PartNumber:smart)}")] string pattern)
        {
            var config = NamingConfig.Default;
            config.Id = 1;
            if (field.Contains("AuthorFolder"))
            {
                pattern = pattern.Replace("{Book Title}", "{Author NameFirstLast}");
            }
            typeof(NamingConfig).GetProperty(field).SetValue(config, pattern);
            var builder = CreateBuilder();
            var samples = new FileNameSampleService(builder);
            var validation = new FileNameValidationService(samples);
            var naming = new StubNamingConfigService();
            var controller = new NamingConfigController(naming, samples, validation, builder);
            var saveError = Assert.Throws<FluentValidation.ValidationException>(() => controller.UpdateNamingConfig(config.ToResource()));
            Assert.That(saveError.Errors, Has.Some.Matches<FluentValidation.Results.ValidationFailure>(
                error => error.PropertyName == field && error.ErrorMessage.Contains("brace at position")));
            Assert.That(naming.SaveCount, Is.Zero);
            var mediaType = field.StartsWith("Ebook", StringComparison.Ordinal) ? "ebook" : "audiobook";
            Assert.Throws<FluentValidation.ValidationException>(() => controller.GetExamples(config.ToResource(), mediaType));

            var patternService = new NamingPatternService(new PatternCompiler(), builder);
            Assert.That(patternService.ValidatePattern(pattern).IsValid, Is.False);
            Assert.That(Assert.Throws<ArgumentException>(() => patternService.PreviewPattern(pattern)).Message, Does.Contain("brace at position"));

            var package = new SettingsBackupPackage { MediaManagement = new MediaManagementBackup { NamingConfig = config } };
            var restoreError = Assert.Throws<NzbDroneClientException>(() => SettingsBackupService.ValidateRestore(package,
                new HashSet<SettingsBackupCategory> { SettingsBackupCategory.MediaManagement }, filenameValidation: validation));
            Assert.That(restoreError.Message, Does.Contain("Naming." + field).And.Contain("brace at position"));
        }

        [Test, Combinatorial]
        public void saved_bad_braces_warn_when_building_names_without_changing_existing_output(
            [Values("audiobook", "ebook")] string mediaType, [Values(false, true)] bool authorFolder,
            [Values(false, true)] bool malformed)
        {
            var target = new MemoryTarget { Layout = "${level}|${message}" };
            using var factory = new LogFactory();
            var logging = new LoggingConfiguration(factory);
            logging.AddRule(LogLevel.Warn, LogLevel.Warn, target);
            factory.Configuration = logging;
            var builder = new FileNameBuilder(new StubNamingConfigService(), new StubQualityDefinitionService(),
                new CacheManager(), new StubCustomFormatCalculationService(), factory.GetLogger("NamingBraceWarning"));
            var config = NamingConfig.Default;
            config.RenameBooks = config.EbookRenameBooks = true;
            config.StandardBookFormat = config.EbookStandardBookFormat = "{Book Title}{ (PartNumber:smart)}" + (malformed ? "}" : "");
            config.AuthorFolderFormat = config.EbookAuthorFolderFormat = "{Author NameFirstLast}" + (malformed ? "}" : "");
            var author = new Author { Name = "Jim Butcher" };
            var edition = new Edition { Title = "Grave Peril", Book = new Book { Author = author } };
            var file = new BookFile { MediaType = mediaType, Path = "original", Quality = new QualityModel(Quality.Unknown) };
            var name = authorFolder ? builder.GetAuthorFolder(author, config, mediaType)
                : builder.BuildBookFileName(author, edition, file, config, new List<CustomFormat>());
            Assert.That(name, Is.EqualTo((authorFolder ? "Jim Butcher" : "Grave Peril") + (malformed ? "}" : "")));
            for (var index = 0; index < 20; index++)
            {
                if (authorFolder) builder.GetAuthorFolder(author, config, mediaType);
                else builder.BuildBookFileName(author, edition, file, config, new List<CustomFormat>());
            }
            Assert.That(target.Logs.Count, Is.EqualTo(malformed ? 1 : 0));
            if (malformed)
            {
                Assert.That(target.Logs[0], Does.Contain(mediaType).And.Contain("brace at position")
                    .And.Contain(authorFolder ? "AuthorFolderFormat" : "StandardBookFormat").And.Contain("Settings > Media Management"));
            }
        }

        [TestCase("{Book Title}{ (PartNumber:smart)}")]
        [TestCase("{Book Title}{ - Smart Narrator}{ (PartNumber:smart)}")]
        [TestCase("{Book Title}{ {Narrator}}/{Book Title}{ (PartNumber)}")]
        [TestCase("{Book Title}{ (PartNumber/PartCount)}")]
        [TestCase("{Original Filename}")]
        public void balanced_existing_naming_forms_remain_valid(string pattern)
        {
            var config = NamingConfig.Default;
            config.StandardBookFormat = pattern;
            Assert.That(ConfigValueValidation.ValidateNaming(config), Is.Empty);
        }

        [TestCase(" Narrators , FullCast ", "Narrators", "FullCast")]
        [TestCase("AudioProductionType, FullCast", "AudioProductionType", "FullCast")]
        public void smart_order_accepts_spacing_between_choices(string input, string first, string second)
        {
            Assert.That(SmartNarratorToken.ParseOrder(input), Is.EqualTo(new[] { first, second }));
        }

        [TestCase("Narrators, Narrators")]
        [TestCase("Narrators, ")]
        [TestCase("Narrators, Unknown")]
        public void smart_order_still_rejects_duplicates_empty_and_unknown_choices(string input)
        {
            Assert.Throws<NamingFormatException>(() => SmartNarratorToken.ParseOrder(input));
        }

        [TestCase("ipek Reader", "IR")]
        [TestCase("𐐨name Reader", "𐐀R")]
        [TestCase("e\u0301mile Reader", "E\u0301R")]
        [SetCulture("tr-TR")]
        public void narrator_initials_use_complete_text_elements_and_invariant_case(string narrator, string expected)
        {
            var service = new NamingPatternService(new PatternCompiler(), CreateBuilder());
            Assert.That(service.PreviewPattern("{Narrator Initials}", sampleContext: new SampleContext { NarratorName = narrator }).Path, Is.EqualTo(expected));
        }
    }
}
