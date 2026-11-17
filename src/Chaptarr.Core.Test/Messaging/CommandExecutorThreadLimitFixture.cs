using NUnit.Framework;
using NzbDrone.Core.Messaging.Commands;

namespace Chaptarr.Core.Test.Messaging
{
    [TestFixture]
    public class CommandExecutorThreadLimitFixture
    {
        [TestCase(null, 3)]
        [TestCase("", 3)]
        [TestCase("abc", 3)]
        [TestCase("0", 3)]
        [TestCase("-4", 3)]
        [TestCase("1", 1)]
        [TestCase("8", 8)]
        [TestCase("32", 32)]
        [TestCase("500", 32)]
        public void should_parse_thread_limit(string value, int expected)
        {
            Assert.AreEqual(expected, CommandExecutor.ParseThreadLimit(value));
        }
    }
}
