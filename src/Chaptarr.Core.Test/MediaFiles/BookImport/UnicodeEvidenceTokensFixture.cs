using System.Collections;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles.BookImport;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class UnicodeEvidenceTokensFixture
    {
        [TestCase("NzbDrone.Core.MediaFiles.BookImport.FileMatchingService", "TokenizeForLeftoverGateSequence")]
        [TestCase("NzbDrone.Core.MediaFiles.BookImport.FileMatchingService", "GetNormalizedWordSpans")]
        [TestCase("NzbDrone.Core.MediaFiles.BookImport.Services.ExactMatchEvidenceBuilder", "TokenizeNameTokens")]
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
    }
}
