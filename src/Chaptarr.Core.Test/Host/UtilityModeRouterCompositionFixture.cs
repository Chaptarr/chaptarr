using DryIoc;
using Moq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common;
using NzbDrone.Common.Composition.Extensions;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Processes;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore;
using NzbDrone.Host;
using NzbDrone.Host.AccessControl;

namespace Chaptarr.Core.Test.Host
{
    [TestFixture]
    public class UtilityModeRouterCompositionFixture
    {
        [Test]
        public void should_not_resolve_config_service_while_composing_migration_utility_router()
        {
            var configServiceResolved = false;
            using var container = new Container(rules => rules.WithNzbDroneRules());
            container.RegisterDelegate<IConfigService>(
                _ =>
                {
                    configServiceResolved = true;
                    return Mock.Of<IConfigService>();
                },
                Reuse.Singleton);
            container.RegisterInstance<NzbDrone.Common.IServiceProvider>(Mock.Of<NzbDrone.Common.IServiceProvider>());
            container.RegisterInstance<IConsoleService>(Mock.Of<IConsoleService>());
            container.RegisterInstance<IProcessProvider>(Mock.Of<IProcessProvider>());
            container.RegisterInstance<IRemoteAccessAdapter>(Mock.Of<IRemoteAccessAdapter>());
            container.RegisterInstance<IAppFolderFactory>(Mock.Of<IAppFolderFactory>());
            container.RegisterInstance<IConnectionStringFactory>(Mock.Of<IConnectionStringFactory>());
            container.RegisterInstance<IConfigFileProvider>(Mock.Of<IConfigFileProvider>());
            container.RegisterInstance<IStartupContext>(new StartupContext("-preview-bookshelf-migration"));
            container.RegisterInstance(LogManager.GetLogger(nameof(UtilityModeRouterCompositionFixture)));
            container.Register<UtilityModeRouter>(Reuse.Singleton);

            _ = container.Resolve<UtilityModeRouter>();

            Assert.That(configServiceResolved, Is.False,
                "Resolving utility mode dependencies must not instantiate the main database before the selected command runs.");
        }
    }
}
