using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Services;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class UnicodeEvidenceTokensFixture
    {
        [TestCase("NzbDrone.Core.MediaFiles.BookImport.FileMatchingService", "TokenizeForLeftoverGateSequence")]
        [TestCase("NzbDrone.Core.MediaFiles.BookImport.FileMatchingService", "GetNormalizedWordSpans")]
        public void import_evidence_should_not_discard_the_distinguishing_supplementary_letter(string typeName, string methodName)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
            var type = typeof(FileMatchingService).Assembly.GetType(typeName);
            var method = type.GetMethod(methodName, flags);
            foreach (var letter in new[] { "𠀀", "𠀁" })
            {
                var input = $"🌶️ Astorias{letter}Smut";
                var result = (IEnumerable)method.Invoke(method.IsStatic ? null : RuntimeHelpers.GetUninitializedObject(type), new object[] { input });
                var tokens = result.Cast<object>().Select(v => v is string text ? text :
                    (string)(v.GetType().GetProperty("Token", flags) ?? v.GetType().GetProperty("Value", flags)).GetValue(v));
                Assert.That(tokens, Is.EqualTo(new[] { $"astorias{letter}smut" }));
            }
        }

        [Test]
        public void multipart_proof_should_preserve_supplementary_letters()
        {
            var type = typeof(FileMatchingService).Assembly.GetType("NzbDrone.Core.MediaFiles.BookImport.Services.TrackedMultipartAudioRepairHelper");
            var method = type.GetMethod("NormalizeProofValue", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method.Invoke(null, new object[] { "Astorias𠀀Smut 🌶️", false }), Is.EqualTo("astorias𠀀smut"));
        }

        [TestCase("Renée Ballard", "Renee Ballard", true)]
        [TestCase("Renée Ballard", "Rene\u0301e Ballard", true)]
        [TestCase("Astorias𠀀Smut", "Astorias𠀀Smut", true)]
        [TestCase("Astorias𠀀Smut", "Astorias𠀁Smut", false)]
        [TestCase("がとう", "かとう", false)]
        [TestCase("がとう", "か\u3099とう", true)]
        [TestCase("𐐀 Reader", "𐐨 Reader", true)]
        public void narrator_evidence_consumers_should_preserve_shared_character_identity(string narrator, string observed, bool expected)
        {
            Assert.Multiple(() =>
            {
                foreach (var typeName in new[]
                {
                    "NzbDrone.Core.MediaFiles.BookImport.FileMatchingService",
                    "NzbDrone.Core.MediaFiles.BookImport.Services.ExactMatchEvidenceBuilder"
                })
                {
                    var type = typeof(FileMatchingService).Assembly.GetType(typeName);
                    var method = type.GetMethod("FindNarratorEvidenceFields", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
                    var subject = method.IsStatic ? null : RuntimeHelpers.GetUninitializedObject(type);
                    var tags = new Dictionary<string, List<string>> { ["COMPOSER"] = new() { observed } };
                    var fields = InvokeNarratorEvidence(method, subject, narrator, "Unrelated Writer", tags);

                    Assert.That(fields.Cast<object>().Any(), Is.EqualTo(expected), typeName);
                }
            });
        }

        [TestCase("Renée Ballard", "Renee Ballard", true)]
        [TestCase("Astorias𠀀Smut", "Astorias𠀀Smut", true)]
        [TestCase("Astorias𠀀Smut", "Astorias𠀁Smut", false)]
        [TestCase("か\u3099とう", "がとう", true)]
        [TestCase("Dune Ⅱ", "Dune II", true)]
        public void multipart_root_comparison_should_keep_shared_character_identity(string folderName, string editionTitle, bool expected)
        {
            var folder = Path.Combine(TestContext.CurrentContext.WorkDirectory.GetActualCasing(), "unrelated-writer", folderName);
            var path = Path.Combine(folder, "track.mp3");

            Assert.That(BookCoalescingHelper.FindBookRootFolder(path, editionTitle), Is.EqualTo(expected ? folder : null));
        }

        [TestCase("CD1")]
        [TestCase("Disc 2")]
        public void author_ready_retry_should_use_the_shared_disc_root_even_when_folder_title_differs(string disc)
        {
            var root = Path.Combine(TestContext.CurrentContext.WorkDirectory.GetActualCasing(), "unrelated-writer", "Local Folder");
            var path = Path.Combine(root, disc, "track.mp3");

            Assert.That(BookCoalescingHelper.FindBookRootFolder(path, "Published Title"), Is.EqualTo(root));
        }

        [TestCase("Jennifer L Reader", "Jennifer Q Reader", 1, 1)]
        [TestCase("J. K. Reader", "M. K. Reader", 1, 1)]
        [TestCase("Robert Louis Stevenson", "Robert Stevenson", 1, 0)]
        [TestCase("Robert Louis Stevenson", "Robert Stevenson", 2, 2)]
        [TestCase("John Smith", "Smith, John", 1, 0)]
        [TestCase("John Smith", "John Smith; Other Narrator", 1, 0)]
        [TestCase("John Smith", "John Smith; Other Narrator", 2, 2)]
        [TestCase("John Smith", "Pat Smith; John Doe", 1, 1)]
        [TestCase("John Smith", "Pat Smith, John Doe", 1, 1)]
        [TestCase("Renée Ballard", "Rene\u0301e Ballard", 1, 0)]
        [TestCase("Renée Ballard", "Rene\u0301e Ballard", 2, 2)]
        [TestCase("がとう 読者", "か\u3099とう 読者", 1, 0)]
        [TestCase("がとう 読者", "かとう 読者", 1, 1)]
        public void self_narration_should_use_complete_shared_author_credit_and_keep_the_extra_field_gate(
            string author,
            string narrator,
            int fieldCount,
            int expected)
        {
            Assert.Multiple(() =>
            {
                foreach (var typeName in new[]
                {
                    "NzbDrone.Core.MediaFiles.BookImport.FileMatchingService",
                    "NzbDrone.Core.MediaFiles.BookImport.Services.ExactMatchEvidenceBuilder"
                })
                {
                    var type = typeof(FileMatchingService).Assembly.GetType(typeName);
                    var method = type.GetMethod("FindNarratorEvidenceFields", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
                    var subject = method.IsStatic ? null : RuntimeHelpers.GetUninitializedObject(type);
                    var tags = new Dictionary<string, List<string>> { ["COMPOSER"] = new() { narrator } };
                    if (fieldCount == 2)
                    {
                        tags["TITLE"] = new List<string> { narrator };
                    }

                    var fields = InvokeNarratorEvidence(method, subject, narrator, author, tags);

                    Assert.That(fields.Cast<object>().Count(), Is.EqualTo(expected), typeName);
                }
            });
        }

        [TestCase("Jennifer Q Reader", "Jennifer L Reader", false)]
        [TestCase("J. K. Reader", "M. K. Reader", false)]
        [TestCase("John Smith", "Pat Smith; John Doe", false)]
        [TestCase("John Smith", "Pat Smith, John Doe", false)]
        [TestCase("Robert Louis Stevenson", "Robert Stevenson", true)]
        [TestCase("John Smith", "Smith, John", true)]
        [TestCase("Smith, John Louis", "John Smith", true)]
        [TestCase("John Smith", "Smith, John Louis", true)]
        [TestCase("John Smith; Other Narrator", "John Smith", false)]
        [TestCase("John Smith; Other Narrator", "Other Narrator; John Smith", true)]
        [TestCase("John Smith; Other Narrator", "John Smith; Different Narrator", false)]
        [TestCase("John Smith; John Q Smith", "John Smith", false)]
        [TestCase("Pat Smith, John Doe", "Pat Doe", false)]
        public void narrator_evidence_should_require_a_coherent_complete_credit(string narrator, string observed, bool expected)
        {
            Assert.Multiple(() =>
            {
                foreach (var typeName in new[]
                {
                    "NzbDrone.Core.MediaFiles.BookImport.FileMatchingService",
                    "NzbDrone.Core.MediaFiles.BookImport.Services.ExactMatchEvidenceBuilder"
                })
                {
                    var type = typeof(FileMatchingService).Assembly.GetType(typeName);
                    var method = type.GetMethod("FindNarratorEvidenceFields", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
                    var subject = method.IsStatic ? null : RuntimeHelpers.GetUninitializedObject(type);
                    var tags = new Dictionary<string, List<string>> { ["AUTHOR"] = new() { observed } };
                    var fields = InvokeNarratorEvidence(method, subject, narrator, "Unrelated Writer", tags);

                    Assert.That(fields.Cast<object>().Any(), Is.EqualTo(expected), typeName);
                }
            });
        }

        [TestCase("Jim Collins", "Jim Collins", "Jim Collins", 1, 0)]
        [TestCase("Jim Collins", "Jim Collins", "Jim Collins", 2, 2)]
        [TestCase("Jennifer Q Reader", "Jennifer L Reader", "Jennifer L Reader", 2, 0)]
        [TestCase("Robert Louis Stevenson", "Robert Stevenson", "Robert Stevenson", 2, 2)]
        [TestCase("John Smith; Other Narrator", "Unrelated Writer", "John Smith", 1, 0)]
        [TestCase("John Smith; Other Narrator", "Unrelated Writer", "Other Narrator; John Smith", 1, 1)]
        public void exact_narrator_evidence_builder_should_keep_complete_credit_and_two_field_self_gate(
            string narrator, string author, string observed, int fieldCount, int expected)
        {
            var tags = new Dictionary<string, List<string>> { ["COMPOSER"] = new() { observed } };
            if (fieldCount == 2)
            {
                tags["ARTIST"] = new List<string> { observed };
            }

            var evidence = ExactMatchEvidenceBuilder.BuildNarratorEvidenceTags(new Edition { Narrator = narrator }, author, tags);

            Assert.That(evidence.Count, Is.EqualTo(expected));
        }

        [Test]
        public void narrator_candidate_producer_should_preserve_raw_and_structured_credit_boundaries()
        {
            var edition = new Edition
            {
                NarratorNames = new List<string> { "Smith, John", "Other Narrator" },
                Narrator = "Pat Smith, John Doe"
            };

            Assert.That(ExactMatchEvidenceBuilder.GetNarratorCandidates(edition),
                Is.EqualTo(new[] { "Smith, John", "Other Narrator", "Pat Smith, John Doe" }));
        }

        private static IEnumerable InvokeNarratorEvidence(MethodInfo method, object subject, string narrator, string author,
            Dictionary<string, List<string>> tags)
        {
            var arguments = method.GetParameters().Length == 4
                ? new object[] { narrator, author, tags, true }
                : new object[] { narrator, author, tags };
            return (IEnumerable)method.Invoke(subject, arguments);
        }
    }
}
