using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.MediaFiles.TagExtraction;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    [NonParallelizable]
    public class TagReaderContractFixture
    {
        public static IEnumerable<TestCaseData> ContainerAliases()
        {
            var aliases = (IReadOnlyDictionary<string, IReadOnlyList<string>>)typeof(TagCanonicalizer)
                .GetField("CanonicalAliasSources", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            // This table also records ebook/external lineage; AddCanonicalKeys generates container aliases.
            foreach (var pair in aliases)
            {
                foreach (var source in pair.Value.Where(source => source.Contains(':')))
                {
                    var numberFrame = source == "ID3v2:TRCK" || source == "ID3v2:TPOS";
                    var raw = numberFrame ? "3/14" : "Alias contract value";
                    var expected = numberFrame ? (pair.Key.StartsWith("TOTAL", StringComparison.Ordinal) ? "14" : "3") : raw;
                    yield return new TestCaseData(pair.Key, source.Replace("*", "fixture"), raw, expected);
                }
            }
        }

        [TestCaseSource(nameof(ContainerAliases))]
        public void declared_container_alias_must_be_generated(string canonical, string source, string raw, string expected)
        {
            var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase) { [source] = new() { raw } };
            TagCanonicalizer.AddCanonicalKeys(tags);
            Assert.That(tags.TryGetValue(canonical, out var values), Is.True, canonical + " from " + source);
            Assert.That(values, Does.Contain(expected));
        }

        public static IEnumerable<TestCaseData> GeneratedContainerSources()
        {
            // Read the generator's own inputs, independently of the provenance map.
            // Embed the build's source using the existing migration-source fixture convention.
            using var stream = typeof(TagReaderContractFixture).Assembly.GetManifestResourceStream("TagSources.TagCanonicalizer.cs");
            Assert.That(stream, Is.Not.Null, "Missing embedded TagCanonicalizer source: TagSources.TagCanonicalizer.cs");
            using var reader = new StreamReader(stream);
            var source = reader.ReadToEnd();
            source = source.Substring(source.IndexOf("public static void AddCanonicalKeys(", StringComparison.Ordinal));
            foreach (var key in Regex.Matches(source, "(?:\\bGet|\\bStartsWith)\\s*\\(\\s*\"([^\"]+)\"").Cast<Match>()
                .Select(match => match.Groups[1].Value)
                .Concat(Regex.Matches(source, "\"([^\"\r\n]*)\"").Cast<Match>().Select(match => match.Groups[1].Value).Where(key => key.Contains(':')))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                yield return new TestCaseData(key.EndsWith(":", StringComparison.Ordinal) ? key + "fixture" : key);
            }
        }

        [TestCaseSource(nameof(GeneratedContainerSources))]
        public void generated_container_aliases_must_have_declared_sources(string source)
        {
            var raw = source == "ID3v2:TRCK" || source == "ID3v2:TPOS" ? "3/14" : "Alias contract value";
            var original = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase) { [source] = new() { raw } };
            var tags = new Dictionary<string, List<string>>(original, StringComparer.OrdinalIgnoreCase);
            TagCanonicalizer.AddCanonicalKeys(tags);
            var generated = tags.Where(pair => pair.Key != source && pair.Value.Count > 0).ToList();
            Assert.That(generated, Is.Not.Empty, "Generator source is no longer exercised: " + source);
            foreach (var pair in generated)
            {
                // Numeric-pair transformations are checked by the forward test; here verify lineage.
                Assert.That(TagCanonicalizer.IsCanonicalAliasBackedByRawSource(pair.Key, raw, original), Is.True,
                    pair.Key + " generated from undeclared source " + source);
            }
        }

        [TestCase("ExtractId3v2Tags", ".flac")]
        [TestCase("ExtractXiphTags", ".mp3")]
        [TestCase("ExtractApeTags", ".mp3")]
        [TestCase("ExtractAsfTags", ".mp3")]
        [TestCase("ExtractAppleTags", ".mp3")]
        public void container_read_failure_is_debug_logged_with_path(string method, string extension)
        {
            var loggerName = NzbDroneLogger.GetLogger(typeof(TagLibSharpExtractor)).Name;
            var prior = LogManager.Configuration;
            var target = new MemoryTarget { Layout = "${level}: ${message}" };
            var config = new LoggingConfiguration();
            config.AddRule(LogLevel.Debug, LogLevel.Fatal, target, loggerName);
            try
            {
                LogManager.Configuration = config;
                var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "Audio", "silence" + extension);
                using var file = TagLib.File.Create(path);
                // Inject a missing container using a real file; test the existing recovery boundary.
                typeof(TagLibSharpExtractor).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { file, new Dictionary<string, List<string>>() });
                Assert.That(target.Logs, Is.Not.Empty);
                Assert.That(target.Logs, Is.All.StartsWith("Debug:"));
                Assert.That(target.Logs, Is.All.Contains(path));
            }
            finally
            {
                LogManager.Configuration = prior;
                LogManager.ReconfigExistingLoggers();
            }
        }
    }
}
