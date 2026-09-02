using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Hardcover.Library;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Tags;

namespace Chaptarr.Core.Test.ImportLists
{
    [TestFixture]
    public class HardcoverLibraryImportListCursorFixture
    {
        private class HttpClientProxy : DispatchProxy
        {
            public List<HttpRequest> Requests { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IHttpClient.Execute) && args?.Length >= 1 && args[0] is HttpRequest request)
                {
                    Requests.Add(request);

                    var query = request.ContentSummary ?? (request.ContentData != null ? System.Text.Encoding.UTF8.GetString(request.ContentData) : request.Url?.FullUri ?? "");

                    if (query.Contains("query Me"))
                    {
                        var json = "{\"data\":{\"me\":[{\"id\":42,\"username\":\"testuser\"}]}}";
                        return new HttpResponse(request, new HttpHeader(), json);
                    }

                    if (query.Contains("query OwnedListBooks"))
                    {
                        var json = "{\"data\":{\"list_books\":[{\"id\":20,\"book\":{\"id\":502,\"canonical_id\":502,\"title\":\"Owned Book\",\"contributions\":[{\"author_id\":202,\"contribution\":\"Author\",\"author\":{\"id\":202,\"name\":\"Owned Author\",\"canonical_id\":202,\"identifiers\":\"\"}}]}}]}}";
                        return new HttpResponse(request, new HttpHeader(), json);
                    }

                    if (query.Contains("query OwnedList("))
                    {
                        var json = "{\"data\":{\"lists\":[{\"id\":100}]}}";
                        return new HttpResponse(request, new HttpHeader(), json);
                    }

                    if (query.Contains("query UserBooks"))
                    {
                        var json = "{\"data\":{\"user_books\":[{\"id\":10,\"updated_at\":\"2026-08-19T20:00:00Z\",\"book\":{\"id\":501,\"canonical_id\":501,\"title\":\"Test Book\",\"contributions\":[{\"author_id\":201,\"contribution\":\"Author\",\"author\":{\"id\":201,\"name\":\"Test Author\",\"canonical_id\":201,\"identifiers\":\"\"}}]}}]}}";
                        return new HttpResponse(request, new HttpHeader(), json);
                    }

                    return new HttpResponse(request, new HttpHeader(), "{\"data\":{}}");
                }

                return null;
            }
        }

        private class StateRepositoryProxy : DispatchProxy
        {
            public HardcoverLibraryImportListState State { get; set; }
            public List<HardcoverLibraryImportListState> Inserted { get; } = new();
            public List<HardcoverLibraryImportListState> Updated { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IHardcoverLibraryImportListStateRepository.GetByImportListId))
                {
                    return State;
                }

                if (targetMethod?.Name == nameof(IHardcoverLibraryImportListStateRepository.Insert) && args?.Length >= 1 && args[0] is HardcoverLibraryImportListState insertModel)
                {
                    insertModel.Id = 1;
                    Inserted.Add(insertModel);
                    State = insertModel;
                    return insertModel;
                }

                if (targetMethod?.Name == nameof(IHardcoverLibraryImportListStateRepository.Update) && args?.Length >= 1 && args[0] is HardcoverLibraryImportListState updateModel)
                {
                    Updated.Add(updateModel);
                    State = updateModel;
                    return updateModel;
                }

                return null;
            }
        }

        private class StatusServiceProxy : DispatchProxy
        {
            public List<int> SuccessRecorded { get; } = new();
            public List<int> FailureRecorded { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IImportListStatusService.RecordSuccess) && args?.Length >= 1 && args[0] is int successId)
                {
                    SuccessRecorded.Add(successId);
                    return null;
                }

                if (targetMethod?.Name == nameof(IImportListStatusService.RecordFailure) && args?.Length >= 1 && args[0] is int failureId)
                {
                    FailureRecorded.Add(failureId);
                    return null;
                }

                return null;
            }
        }

        private class ConfigProxy : DispatchProxy
        {
            public bool HardcoverEnabled { get; set; } = true;
            public string HardcoverApiKey { get; set; } = "test-global-key";

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == "get_HardcoverEnabled") return HardcoverEnabled;
                if (targetMethod?.Name == "get_HardcoverApiKey") return HardcoverApiKey;
                return null;
            }
        }

        [Test]
        public void should_not_persist_cursors_in_fetch_until_commit_state_is_called()
        {
            var httpClient = DispatchProxy.Create<IHttpClient, HttpClientProxy>();
            var stateRepo = DispatchProxy.Create<IHardcoverLibraryImportListStateRepository, StateRepositoryProxy>();
            var statusService = DispatchProxy.Create<IImportListStatusService, StatusServiceProxy>();
            var configService = DispatchProxy.Create<IConfigService, ConfigProxy>();

            var stateRepoProxy = (StateRepositoryProxy)(object)stateRepo;
            var statusServiceProxy = (StatusServiceProxy)(object)statusService;

            var definition = new ImportListDefinition
            {
                Id = 1,
                Name = "Test Hardcover Library",
                Implementation = nameof(HardcoverLibraryImportList),
                Settings = new HardcoverLibraryImportListSettings
                {
                    ApiToken = "test-token",
                    ImportWantToRead = true,
                    ImportOwned = true,
                    MonitorAudiobooks = true,
                    MonitorEbooks = true,
                    AudiobookQualityProfileId = 1,
                    EbookQualityProfileId = 1,
                    AudiobookMetadataProfileId = 1,
                    EbookMetadataProfileId = 1,
                    AudiobookRootFolderPath = "/audiobooks",
                    EbookRootFolderPath = "/ebooks"
                }
            };

            var importList = new HardcoverLibraryImportList(
                httpClient,
                new Lazy<IQualityProfileService>(() => null),
                new Lazy<IMetadataProfileService>(() => null),
                new Lazy<ITagService>(() => null),
                null,
                null,
                stateRepo,
                statusService,
                configService,
                null,
                LogManager.GetCurrentClassLogger())
            {
                Definition = definition
            };

            // 1. Run Fetch()
            var items = importList.Fetch();

            Assert.That(items, Has.Count.GreaterThan(0));
            // Verify state is NOT yet saved to repository
            Assert.That(stateRepoProxy.Inserted, Is.Empty, "Cursor should not be inserted during Fetch()");
            Assert.That(stateRepoProxy.Updated, Is.Empty, "Cursor should not be updated during Fetch()");
            Assert.That(statusServiceProxy.SuccessRecorded, Is.Empty, "Success should not be recorded during Fetch()");

            // 2. Run CommitState() (simulating successful ProcessListItems)
            importList.CommitState();

            Assert.That(stateRepoProxy.Inserted, Has.Count.EqualTo(1), "Cursor should be inserted after CommitState()");
            Assert.That(stateRepoProxy.State.CursorUserBookId, Is.EqualTo(10));
            Assert.That(stateRepoProxy.State.OwnedCursorListBookId, Is.EqualTo(20));
            Assert.That(statusServiceProxy.SuccessRecorded, Contains.Item(definition.Id));
        }
    }
}
