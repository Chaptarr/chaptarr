using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using NUnit.Framework;
using NzbDrone.Core.Utilities;

namespace Chaptarr.Core.Test.Utilities
{
    [TestFixture]
    public class UnicodeComparisonFoldJavascriptFixture
    {
        [TestCase("\n")]
        [TestCase("\r\n")]
        public void browser_and_server_generated_fold_tables_must_stay_identical(string newline)
        {
            var source = File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "unicodeComparisonFoldData.js"));
            source = source.Replace("\r\n", "\n").Replace("\n", newline);
            var rows = Regex.Matches(source, @"^  0x([0-9A-F]+): (.*),\r?$", RegexOptions.Multiline)
                .ToDictionary(match => int.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    match => JsonSerializer.Deserialize<string>(match.Groups[2].Value));

            Assert.Multiple(() =>
            {
                Assert.That(rows, Is.EquivalentTo(UnicodeComparisonFoldData.Replacements),
                    "Regenerate both fold tables together; browser and server search must use the same mapping.");
                Assert.That(source, Does.Contain($"export const sourceSHA256 = '{UnicodeComparisonFoldData.SourceSHA256}';"));
                Assert.That(source, Does.Contain($"export const dataSHA256 = '{UnicodeComparisonFoldData.DataSHA256}';"));
            });
        }

    }
}
