using System;
using System.Collections.Generic;
using Moq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Authentication;
using NzbDrone.Core.Configuration;
using NzbDrone.Host;

namespace Chaptarr.Core.Test.Authentication
{
    [TestFixture]
    public class FirstRunAuthenticationSetupFixture
    {
        [Test]
        public void should_disable_authentication_when_no_user_exists()
        {
            var authenticationMethod = AuthenticationType.Forms;
            var configFileProvider = new Mock<IConfigFileProvider>();
            configFileProvider.SetupGet(config => config.AuthenticationMethod).Returns(() => authenticationMethod);
            configFileProvider
                .Setup(config => config.SaveConfigDictionary(It.IsAny<Dictionary<string, object>>()))
                .Callback<Dictionary<string, object>>(values => authenticationMethod = (AuthenticationType)values[nameof(IConfigFileProvider.AuthenticationMethod)]);

            var userService = new Mock<IUserService>();
            userService.Setup(service => service.FindUser()).Returns((User)null);

            var setup = new FirstRunAuthenticationSetup(configFileProvider.Object, new Lazy<IUserService>(() => userService.Object), LogManager.GetCurrentClassLogger());
            setup.EnsureInitialAccountSetup();

            Assert.That(authenticationMethod, Is.EqualTo(AuthenticationType.None));
            configFileProvider.Verify(config => config.SaveConfigDictionary(It.Is<Dictionary<string, object>>(values =>
                values.Count == 1 &&
                Equals(values[nameof(IConfigFileProvider.AuthenticationMethod)], AuthenticationType.None))), Times.Once);
        }

        [Test]
        public void should_preserve_configured_authentication_when_a_user_exists()
        {
            var configFileProvider = new Mock<IConfigFileProvider>();
            configFileProvider.SetupGet(config => config.AuthenticationMethod).Returns(AuthenticationType.Forms);

            var userService = new Mock<IUserService>();
            userService.Setup(service => service.FindUser()).Returns(new User());

            var setup = new FirstRunAuthenticationSetup(configFileProvider.Object, new Lazy<IUserService>(() => userService.Object), LogManager.GetCurrentClassLogger());
            setup.EnsureInitialAccountSetup();

            configFileProvider.Verify(config => config.SaveConfigDictionary(It.IsAny<Dictionary<string, object>>()), Times.Never);
        }

        [Test]
        public void should_not_touch_users_when_authentication_is_already_disabled()
        {
            var configFileProvider = new Mock<IConfigFileProvider>();
            configFileProvider.SetupGet(config => config.AuthenticationMethod).Returns(AuthenticationType.None);
            var userService = new Mock<IUserService>();

            var setup = new FirstRunAuthenticationSetup(configFileProvider.Object, new Lazy<IUserService>(() => userService.Object), LogManager.GetCurrentClassLogger());
            setup.EnsureInitialAccountSetup();

            userService.Verify(service => service.FindUser(), Times.Never);
            configFileProvider.Verify(config => config.SaveConfigDictionary(It.IsAny<Dictionary<string, object>>()), Times.Never);
        }
    }
}
