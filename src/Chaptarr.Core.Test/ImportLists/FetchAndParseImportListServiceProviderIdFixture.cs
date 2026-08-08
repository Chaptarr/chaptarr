using System;
using System.Collections.Generic;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.ImportLists
{
    [TestFixture]
    public class FetchAndParseImportListServiceProviderIdFixture
    {
        private class ImportListProxy : DispatchProxy
        {
            public ImportListDefinition DefinitionValue { get; set; }
            public IList<ImportListItemInfo> Items { get; set; } = new List<ImportListItemInfo>();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    "Fetch" => Items,
                    "get_Name" => "Hardcover Library",
                    "get_Definition" => DefinitionValue,
                    "set_Definition" => DefinitionValue = args?[0] as ImportListDefinition,
                    "get_ListType" => ImportListType.Other,
                    "get_MinRefreshInterval" => TimeSpan.Zero,
                    _ => throw new NotImplementedException($"Test proxy does not implement IImportList.{targetMethod?.Name}")
                };
            }
        }

        private class ImportListFactoryProxy : DispatchProxy
        {
            public IImportList ImportList { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == "GetInstance")
                {
                    return ImportList;
                }

                throw new NotImplementedException($"Test proxy does not implement IImportListFactory.{targetMethod?.Name}");
            }
        }

        private class ImportListStatusServiceProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    nameof(IImportListStatusService.GetLastSyncListInfo) => null,
                    nameof(IImportListStatusService.UpdateListSyncStatus) => null,
                    _ => throw new NotImplementedException($"Test proxy does not implement IImportListStatusService.{targetMethod?.Name}")
                };
            }
        }

        [Test]
        public void fetch_single_list_should_dedupe_hardcover_provider_ids_without_goodreads_assumption()
        {
            var definition = new ImportListDefinition
            {
                Id = 1,
                Name = "Hardcover Library",
                EnableAutomaticAdd = true
            };

            var importList = DispatchProxy.Create<IImportList, ImportListProxy>();
            var importListProxy = (ImportListProxy)(object)importList;
            importListProxy.DefinitionValue = definition;
            importListProxy.Items = new List<ImportListItemInfo>
            {
                new()
                {
                    ImportListId = definition.Id,
                    ImportList = definition.Name,
                    Author = "Author",
                    AuthorProviderId = "hc:10",
                    Book = "Book",
                    BookProviderId = "hc:20",
                    EditionProviderId = "hc-ed:30"
                },
                new()
                {
                    ImportListId = definition.Id,
                    ImportList = definition.Name,
                    Author = "Author",
                    AuthorProviderId = "hc:10",
                    Book = "Book",
                    BookProviderId = "hc:20",
                    EditionProviderId = "hc-ed:30"
                }
            };

            var factory = DispatchProxy.Create<IImportListFactory, ImportListFactoryProxy>();
            ((ImportListFactoryProxy)(object)factory).ImportList = importList;

            var status = DispatchProxy.Create<IImportListStatusService, ImportListStatusServiceProxy>();

            var service = new FetchAndParseImportListService(factory, status, LogManager.GetCurrentClassLogger());

            Assert.DoesNotThrow(() =>
            {
                var result = service.FetchSingleList(definition);
                Assert.That(result, Has.Count.EqualTo(1));
                Assert.That(result[0].AuthorProviderId, Is.EqualTo("hc:10"));
                Assert.That(result[0].BookProviderId, Is.EqualTo("hc:20"));
                Assert.That(result[0].EditionProviderId, Is.EqualTo("hc-ed:30"));
            });
        }
    }
}
