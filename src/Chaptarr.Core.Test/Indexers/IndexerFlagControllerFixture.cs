using System.Linq;
using Chaptarr.Api.V1.Indexers;
using Chaptarr.Http.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NUnit.Framework;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture]
    public class IndexerFlagControllerFixture
    {
        [TestCase(true)]
        [TestCase(false)]
        public void should_return_enum_names_for_bookshelf_and_explicit_readarr_clients(bool migrated)
        {
            var context = new DefaultHttpContext();
            context.Items[ReadarrFacadeContext.ItemKey] = new ReadarrFacadeContext(
                "hc", null, migrated ? string.Empty : "/readarr/hc/ebook", migrated);
            var controller = new IndexerFlagController
            {
                ControllerContext = new ControllerContext { HttpContext = context }
            };

            var flags = controller.GetAll();

            foreach (var flag in flags)
            {
                var expectedName = ((IndexerFlags)flag.Id).ToString();
                Assert.That(flag.Name, Is.EqualTo(expectedName));
                Assert.That(flag.NameLower, Is.EqualTo(expectedName.ToLowerInvariant()));
            }
        }

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
