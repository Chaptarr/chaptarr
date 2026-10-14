using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Organizer.NamingPattern;

namespace Chaptarr.Core.Test.Organizer
{
    [TestFixture]
    public class PatternCompilerReleasePartFixture
    {
        [TestCase("{ReleasePart}", "ReleasePart", null)]
        [TestCase("{ReleasePartCount}", "ReleasePartCount", null)]
        [TestCase("{ReleasePart:00}", "ReleasePart", "00")]
        [TestCase("{ReleasePartCount:0}", "ReleasePartCount", "0")]
        public void should_round_trip_release_part_tokens(string pattern, string tokenKey, string format)
        {
            var compiler = new PatternCompiler();

            var ast = compiler.Decompile(pattern);
            var token = ast.NodesById.Values.OfType<TokenNode>().Single();

            Assert.That(token.TokenKey, Is.EqualTo(tokenKey));
            Assert.That(token.Args.TryGetValue("format", out var actualFormat) ? actualFormat : null, Is.EqualTo(format));
            Assert.That(compiler.Compile(ast), Is.EqualTo(pattern));
        }
    }
}
