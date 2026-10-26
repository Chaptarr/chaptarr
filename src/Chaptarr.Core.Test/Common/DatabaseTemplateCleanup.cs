using Chaptarr.Core.Test.Common;
using NUnit.Framework;

namespace Chaptarr.Core.Test
{
    [SetUpFixture]
    public class DatabaseTemplateCleanup
    {
        [OneTimeTearDown]
        public void Cleanup()
        {
            DualDatabaseFixture.CleanupTemplates();
        }
    }
}
