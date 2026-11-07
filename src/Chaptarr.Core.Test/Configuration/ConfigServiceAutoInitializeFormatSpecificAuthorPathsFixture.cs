using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Chaptarr.Api.V1.Config;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Configuration;

namespace Chaptarr.Core.Test.Configuration
{
    [TestFixture]
    public class ConfigServiceAutoInitializeFormatSpecificAuthorPathsFixture
    {
        [Test]
        public void should_default_to_off_and_persist_the_admin_choice()
        {
            var repository = DispatchProxy.Create<IConfigRepository, InMemoryConfigRepositoryProxy>();
            var service = new ConfigService(repository, null, null, LogManager.GetCurrentClassLogger());

            Assert.That(service.AutoInitializeFormatSpecificAuthorPaths, Is.False);
            Assert.That(MediaManagementConfigResourceMapper.ToResource(service).AutoInitializeFormatSpecificAuthorPaths, Is.False);

            service.AutoInitializeFormatSpecificAuthorPaths = true;

            Assert.That(service.AutoInitializeFormatSpecificAuthorPaths, Is.True);
            Assert.That(MediaManagementConfigResourceMapper.ToResource(service).AutoInitializeFormatSpecificAuthorPaths, Is.True);
        }

        [Test]
        public void migrated_bookshelf_instance_should_keep_its_extra_file_extension_default()
        {
            var repository = DispatchProxy.Create<IConfigRepository, InMemoryConfigRepositoryProxy>();
            repository.Upsert("BookshelfNGDatabaseMigrated", "true");
            var service = new ConfigService(repository, null, null, LogManager.GetCurrentClassLogger());

            Assert.That(service.ExtraFileExtensions, Is.EqualTo("srt"));

            service.ExtraFileExtensions = "srt,custom";

            Assert.That(service.ExtraFileExtensions, Is.EqualTo("srt,custom"));
        }

        [Test]
        public void native_instance_should_keep_the_chaptarr_extra_file_extension_default()
        {
            var repository = DispatchProxy.Create<IConfigRepository, InMemoryConfigRepositoryProxy>();
            var service = new ConfigService(repository, null, null, LogManager.GetCurrentClassLogger());

            Assert.That(service.ExtraFileExtensions, Is.EqualTo("jpg,png,cue,m3u,opf"));
        }

        private class InMemoryConfigRepositoryProxy : DispatchProxy
        {
            private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == "All")
                {
                    return _values.Select(entry => new Config { Key = entry.Key, Value = entry.Value }).ToList();
                }

                if (targetMethod.Name == "Upsert")
                {
                    var config = new Config { Key = (string)args[0], Value = (string)args[1] };
                    _values[config.Key] = config.Value;
                    return config;
                }

                return targetMethod.ReturnType != typeof(void) && targetMethod.ReturnType.IsValueType
                    ? Activator.CreateInstance(targetMethod.ReturnType)
                    : null;
            }
        }
    }
}
