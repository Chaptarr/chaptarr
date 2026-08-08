using System.Linq;
using Chaptarr.Api.V1.Indexers;
using NUnit.Framework;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture]
    public class IndexerFlagControllerFixture
    {
        [Test]
        public void should_return_user_friendly_flag_names_for_interactive_search()
        {
            var flags = new IndexerFlagController().GetAll();

            Assert.That(flags.Single(f => f.Id == (int)IndexerFlags.Freeleech).Name, Is.EqualTo("Freeleech"));
            Assert.That(flags.Single(f => f.Id == (int)IndexerFlags.Halfleech).Name, Is.EqualTo("50% Freeleech"));
            Assert.That(flags.Single(f => f.Id == (int)IndexerFlags.DoubleUpload).Name, Is.EqualTo("Double Upload"));
            Assert.That(flags.Single(f => f.Id == (int)IndexerFlags.VipExclusive).Name, Is.EqualTo("VIP Only"));
            Assert.That(flags.Single(f => f.Id == (int)IndexerFlags.VipFreeleech).Name, Is.EqualTo("VIP Freeleech"));
        }
    }
}
