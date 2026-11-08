using System.Runtime.CompilerServices;
using Chaptarr.Core.Test.Common;
using NUnit.Framework;
using NzbDrone.Core.Datastore;

namespace Chaptarr.Core.Test
{
    [SetUpFixture]
    public class DatabaseTemplateCleanup
    {
        [ModuleInitializer]
        public static void Initialize()
        {
            RuntimeHelpers.RunClassConstructor(typeof(DbFactory).TypeHandle);
        }

        [OneTimeTearDown]
        public void Cleanup()
        {
            DualDatabaseFixture.CleanupTemplates();
        }
    }
}
