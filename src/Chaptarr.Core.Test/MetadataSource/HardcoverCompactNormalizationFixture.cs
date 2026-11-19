using System.Reflection;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using NzbDrone.Core.MetadataSource.Hardcover;

namespace Chaptarr.Core.Test.MetadataSource
{
    [TestFixture]
    public class HardcoverCompactNormalizationFixture
    {
        [TestCase("Renée Ballard", "Renee Ballard", true)]
        [TestCase("Renée Ballard", "Rene\u0301e Ballard", true)]
        [TestCase("Astorias𠀀Smut", "Astorias𠀀Smut", true)]
        [TestCase("Astorias𠀀Smut", "Astorias𠀁Smut", false)]
        [TestCase("がとう", "かとう", false)]
        [TestCase("がとう", "か\u3099とう", true)]
        [TestCase("The Art of War", "The Art War", false)]
        [TestCase("John A Smith", "John Smith", false)]
        [TestCase("...", "...", false)]
        public void exact_result_label_ranking_should_use_shared_character_rules(string query, string label, bool expected)
        {
            var method = typeof(HardcoverSearchClient).GetMethod("IsExactCompactMatch", BindingFlags.NonPublic | BindingFlags.Instance);
            var client = RuntimeHelpers.GetUninitializedObject(typeof(HardcoverSearchClient));

            Assert.That(method.Invoke(client, new object[] { query, label }), Is.EqualTo(expected));
        }
    }
}
