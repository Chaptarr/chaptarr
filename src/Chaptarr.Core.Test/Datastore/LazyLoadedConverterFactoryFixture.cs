using System.Text.Json;
using NUnit.Framework;
using NzbDrone.Core.Datastore;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    public class LazyLoadedConverterFactoryFixture
    {
        private class Wrapper
        {
            public LazyLoaded<Child> Child { get; set; }
        }

        private class Child
        {
            public int Id { get; set; }
        }

        [Test]
        public void should_deserialize_lazy_loaded_child_without_advancing_reader()
        {
            var json = "{\"child\":{\"id\":1}}";

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            var wrapper = JsonSerializer.Deserialize<Wrapper>(json, options);

            Assert.That(wrapper, Is.Not.Null);
            Assert.That(wrapper.Child, Is.Not.Null);
            Assert.That(wrapper.Child.IsLoaded, Is.True);
            Assert.That(wrapper.Child.Value.Id, Is.EqualTo(1));
        }

        [Test]
        public void should_deserialize_lazy_loaded_null_as_null()
        {
            var json = "{\"child\":null}";

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            var wrapper = JsonSerializer.Deserialize<Wrapper>(json, options);

            Assert.That(wrapper, Is.Not.Null);
            Assert.That(wrapper.Child, Is.Null);
        }
    }
}
