using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Goodreads;
using NzbDrone.Core.ImportLists.Hardcover.Library;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.Goodreads;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.ImportLists
{
    [TestFixture]
    public class ImportListEditionIntentFixture
    {
        internal static ImportListItemInfo HardcoverItem(bool owned, int? format, bool audio = true, bool ebook = true)
        {
            var list = HardcoverList(audio, ebook);
            var method = typeof(HardcoverLibraryImportList).GetMethod(owned ? "MapOwnedListBook" : "MapUserBook", BindingFlags.Instance | BindingFlags.NonPublic);
            var input = new
            {
                edition_id = format.HasValue ? 202 : (int?)null,
                edition = format.HasValue ? new { id = 202, reading_format_id = format.Value } : null,
                book = new
                {
                    id = 1001,
                    title = "Listed book",
                    contributions = new[] { new { contribution = "Author", author = new { id = 562254, name = "Listed author" } } }
                }
            };
            var dto = JsonSerializer.Deserialize(JsonSerializer.Serialize(input), method.GetParameters().Single().ParameterType);
            var item = (ImportListItemInfo)method.Invoke(list, new[] { dto });
            if (item != null)
            {
                item.ImportListId = 1;
                item.ImportList = "Hardcover Library";
            }

            return item;
        }

        private static HardcoverLibraryImportList HardcoverList(bool audio = true, bool ebook = true)
        {
            return new HardcoverLibraryImportList(null, null, null, null, null, null, null, null, null, null, LogManager.GetCurrentClassLogger())
            {
                Definition = new ImportListDefinition
                {
                    Id = 1,
                    Name = "Hardcover Library",
                    Settings = new HardcoverLibraryImportListSettings { MonitorAudiobooks = audio, MonitorEbooks = ebook }
                }
            };
        }

        internal static ImportListItemInfo SeriesItem()
        {
            var work = new WorkResource();
            work.Parse(XElement.Parse("<work><id>1001</id><original_title>Series book</original_title><best_book><id>202</id><title>Series book</title><author><id>562254</id><name>Listed author</name></author></best_book></work>"));
            var seriesInfo = InterfaceProxy.Proxy<IProvideSeriesInfo>((method, args) => new SeriesResource { Works = new List<WorkResource> { work } });
            var status = InterfaceProxy.Proxy<IImportListStatusService>((method, args) => null);
            var list = new GoodreadsSeriesImportList(seriesInfo, status, null, null, null, null, null, null, null, LogManager.GetCurrentClassLogger())
            {
                Definition = new ImportListDefinition { Id = 1, Settings = new GoodreadsSeriesImportListSettings { SeriesId = 10 } }
            };
            return list.Fetch().Single();
        }

        [Test]
        public void series_should_supply_work_and_author_without_an_edition_request()
        {
            var item = SeriesItem();
            Assert.Multiple(() =>
            {
                Assert.That(item.BookProviderId, Is.EqualTo("gr:1001"));
                Assert.That(item.AuthorProviderId, Is.EqualTo("gr:562254"));
                Assert.That(item.EditionProviderId, Is.Null);
            });
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(null)]
        public void hardcover_shelf_should_preserve_supplied_edition_identity_and_format(int? format)
        {
            var item = HardcoverItem(false, format);
            Assert.Multiple(() =>
            {
                Assert.That(item.BookProviderId, Is.EqualTo("hc:1001"));
                Assert.That(item.EditionProviderId, Is.EqualTo(format.HasValue ? "hc-ed:202" : null));
                Assert.That(item.HardcoverReadingFormatId, Is.EqualTo(format));
            });
        }

        [TestCase(true, 2, false, true)]
        [TestCase(false, 2, false, true)]
        [TestCase(true, 1, true, false)]
        [TestCase(false, 1, true, false)]
        [TestCase(true, 3, true, false)]
        [TestCase(false, 3, true, false)]
        public void source_edition_format_should_not_drop_a_request_for_the_other_enabled_side(bool owned, int format, bool audio, bool ebook)
        {
            Assert.That(HardcoverItem(owned, format, audio, ebook), Is.Not.Null);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void hardcover_dedupe_should_preserve_distinct_owned_and_shelf_editions(bool sameEdition)
        {
            var owned = HardcoverItem(true, 2);
            var shelf = HardcoverItem(false, 2);
            if (!sameEdition)
            {
                shelf.EditionProviderId = "hc-ed:203";
            }

            var generic = HardcoverItem(false, null);
            var method = typeof(HardcoverLibraryImportList).GetMethod("CleanupListItems", BindingFlags.Instance | BindingFlags.NonPublic);
            var items = (IList<ImportListItemInfo>)method.Invoke(HardcoverList(), new object[] { new[] { owned, shelf, generic } });
            Assert.That(items.Select(item => item.EditionProviderId), Is.EquivalentTo(sameEdition ? new[] { "hc-ed:202" } : new[] { "hc-ed:202", "hc-ed:203" }));
        }
    }
}
