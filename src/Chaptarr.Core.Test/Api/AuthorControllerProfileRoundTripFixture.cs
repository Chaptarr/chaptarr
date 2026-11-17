using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Chaptarr.Http;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Datastore.Events;
using Chaptarr.Api.V1.Author;
using Chaptarr.Core.Test.Common;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.AuthorStats;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;
using NzbDrone.SignalR;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public partial class AuthorControllerProfileRoundTripFixture
    {
        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void delayed_cover_event_broadcasts_the_stored_photo_choice(bool postgres, bool downloadCommand)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database, false, false, connected: true);
                const string oldUrl = "https://images.example/old-choice.png";
                const string newUrl = "https://images.example/new-choice.png";
                h.Author.Images = new() { new(MediaCoverTypes.Poster, oldUrl), new(MediaCoverTypes.Poster, newUrl) };
                h.AuthorService.UpdateAuthor(h.Author);
                var oldHash = MediaCoverRendition.ComputeStableAuthorImageHash(oldUrl, MediaCoverTypes.Poster);
                var newHash = MediaCoverRendition.ComputeStableAuthorImageHash(newUrl, MediaCoverTypes.Poster);
                h.AuthorService.UpdateSelectedPosterHash(h.Author.Id, oldHash);
                var stale = h.Authors.Get(h.Author.Id);
                h.Broadcasts.Clear();
                if (downloadCommand)
                {
                    var covers = Proxy<IMapCoversToLocal>((method, _) =>
                    {
                        if (method.Name == nameof(IMapCoversToLocal.EnsureAuthorCovers))
                        {
                            h.AuthorService.UpdateSelectedPosterHash(h.Author.Id, newHash);
                            return true;
                        }
                        return null;
                    });
                    var events = Proxy<IEventAggregator>((_, args) => { h.Controller.Handle((MediaCoversUpdatedEvent)args[0]); return null; });
                    var download = new DownloadMediaService(h.AuthorService, Proxy<IBookService>((_, _) => new List<Book>()),
                        Proxy<IDeferredCoverService>((_, _) => false), covers, events, LogManager.GetCurrentClassLogger());
                    download.Execute(new NzbDrone.Core.MediaCover.Commands.DownloadAuthorMediaCommand { AuthorId = h.Author.Id });
                }
                else
                {
                    h.AuthorService.UpdateSelectedPosterHash(h.Author.Id, newHash);
                    h.Controller.Handle(new MediaCoversUpdatedEvent(stale));
                }
                var resource = ((ResourceChangeMessage<AuthorResource>)h.Broadcasts.Single().Body).Resource;
                Assert.That(resource.SelectedPosterHash, Is.EqualTo(newHash));
            });
        }

        [TestCase(false, "single")]
        [TestCase(true, "single")]
        [TestCase(false, "many")]
        [TestCase(true, "many")]
        [TestCase(false, "transaction")]
        [TestCase(true, "transaction")]
        public void stale_author_save_cannot_overwrite_a_new_photo_choice(bool postgres, string route)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database, false, false, connected: true);
                h.AuthorService.UpdateSelectedPosterHash(h.Author.Id, "old-photo");
                var stale = h.Authors.Get(h.Author.Id);
                h.AuthorService.UpdateSelectedPosterHash(h.Author.Id, "new-photo");
                stale.Name = "Refreshed metadata";
                if (route == "many") h.Authors.UpdateMany(new[] { stale });
                else if (route == "transaction") h.AuthorService.UpdateAuthor(stale, (_, _) => { });
                else h.AuthorService.UpdateAuthor(stale);
                var saved = h.Authors.Get(stale.Id);
                Assert.That(saved.Name, Is.EqualTo("Refreshed metadata"));
                Assert.That(saved.SelectedPosterHash, Is.EqualTo("new-photo"));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void rejected_photo_notifies_the_ui_even_when_download_already_removed_it(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database, false, false, connected: true);
                const string url = "https://images.example/rejected-pick.png";
                h.Author.Images = new() { new(MediaCoverTypes.Poster, url) };
                h.AuthorService.UpdateAuthor(h.Author);
                h.PhotoResult = author =>
                {
                    h.AuthorService.RemoveImageUrls(author.Id, new[] { url });
                    author.Images.Clear();
                    return new EnsureImageResult { State = "error", ErrorCode = "placeholder_image" };
                };
                h.Broadcasts.Clear();
                var result = h.Controller.SetPrimaryPhoto(h.Author.Id, new SetPrimaryPhotoRequest { PhotoUrl = url }).GetAwaiter().GetResult();
                Assert.That(result, Is.TypeOf<BadRequestObjectResult>());
                Assert.That(h.Broadcasts, Has.Count.EqualTo(1));
                Assert.That(h.Authors.Get(h.Author.Id).Images, Is.Empty);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void photo_selection_preserves_a_concurrent_author_refresh_and_notifies_the_ui(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database, false, false, connected: true);
                const string url = "https://images.example/choice.png";
                h.Author.Images = new() { new(MediaCoverTypes.Poster, url) };
                h.AuthorService.UpdateAuthor(h.Author);
                h.BeforePhotoDownloaded = () =>
                {
                    var newer = h.Authors.Get(h.Author.Id);
                    newer.Name = "Concurrent author refresh";
                    newer.AudiobookMonitored = false;
                    h.Authors.SetFields(newer, a => a.Name, a => a.AudiobookMonitored);
                };
                h.Events.Clear();
                var result = h.Controller.SetPrimaryPhoto(h.Author.Id, new SetPrimaryPhotoRequest { PhotoUrl = url }).GetAwaiter().GetResult();
                Assert.That(result, Is.TypeOf<OkResult>());
                var saved = h.Authors.Get(h.Author.Id);
                Assert.That(saved.Name, Is.EqualTo("Concurrent author refresh"));
                Assert.That(saved.AudiobookMonitored, Is.False);
                Assert.That(saved.SelectedPosterHash, Is.EqualTo(AuthorImageHashHelper.ComputeStableImageHash(url, MediaCoverTypes.Poster)));
                Assert.That(h.Broadcasts, Has.Count.EqualTo(1));
                Assert.That(h.AuthorService.UpdateSelectedPosterHash(saved.Id, null, "another-selection"), Is.False);
                Assert.That(h.AuthorService.UpdateSelectedPosterHash(saved.Id, saved.SelectedPosterHash), Is.False);
                Assert.That(h.AuthorService.UpdateSelectedPosterHash(saved.Id, null, saved.SelectedPosterHash), Is.True);
                h.Events.Clear();
                Assert.That(h.AuthorService.RemoveImageUrls(saved.Id, new[] { url }), Is.True);
                Assert.That(h.Events.OfType<MediaCoversUpdatedEvent>(), Is.Empty);
            });
        }

        [Test]
        public void author_edit_payload_retains_required_path_and_omits_unchanged_format_folders(
            [Values] bool postgres, [Values] bool ebook, [Values] bool folderEdit)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database, ebook, false);
                var resource = h.GetPayload();
                resource.AudiobookFolder = null;
                resource.EbookFolder = null;
                if (folderEdit)
                {
                    if (ebook) resource.EbookFolder = Path.Combine(h.Author.EbookRootFolderPath, "Ted Chiang new");
                    else resource.AudiobookFolder = Path.Combine(h.Author.AudiobookRootFolderPath, "Ted Chiang new");
                }
                Assert.DoesNotThrow(() => h.Validate(resource));
                Assert.That(h.Controller.UpdateAuthor(resource).Result, Is.TypeOf<AcceptedAtActionResult>());
            });
        }

        [Test]
        public void partial_put_preserves_omitted_sync_and_new_book_settings(
            [Values(false, true)] bool postgres, [Values(false, true)] bool ebook, [Values(false, true)] bool sync)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var harness = new Harness(database, ebook, false, initialSync: sync);
                Assert.That(harness.Authors.Get(harness.Author.Id).SyncMonitoredAcrossFormats, Is.EqualTo(sync));
                var expectedAudio = harness.Author.AudiobookMonitorNewItems;
                var expectedEbook = harness.Author.EbookMonitorNewItems;
                var resource = harness.GetPayload();
                Assert.That(resource.SyncMonitoredAcrossFormats, Is.Null);
                Assert.That(resource.AudiobookMonitorNewItems, Is.Null);
                Assert.That(resource.EbookMonitorNewItems, Is.Null);
                harness.Controller.UpdateAuthor(resource);
                var saved = harness.Authors.Get(harness.Author.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(saved.SyncMonitoredAcrossFormats, Is.EqualTo(sync));
                    Assert.That(saved.AudiobookMonitorNewItems, Is.EqualTo(expectedAudio));
                    Assert.That(saved.EbookMonitorNewItems, Is.EqualTo(expectedEbook));
                    Assert.That(harness.Commands, Is.Empty);
                });
            });
        }

        [Test]
        public void native_get_then_save_or_toggle_keeps_the_other_format_unconfigured(
            [Values(false, true)] bool postgres,
            [Values(false, true)] bool ebook,
            [Values(false, true)] bool toggle,
            [Values(false, true)] bool generalLegacyProfile)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var harness = new Harness(database, ebook, generalLegacyProfile);
                var resource = harness.GetPayload();
                Assert.That(resource.MetadataProfileId, Is.EqualTo(harness.Author.MetadataProfileId));
                Assert.That(resource.RootFolderPath, Is.Null);
                Assert.That(resource.AudiobookTags, Is.Not.Null);
                Assert.That(resource.EbookTags, Is.Not.Null);

                if (toggle)
                {
                    if (ebook) resource.EbookMonitored = false;
                    else resource.AudiobookMonitored = false;
                }

                Assert.DoesNotThrow(() => harness.Validate(resource));
                var result = harness.Controller.UpdateAuthor(resource);
                Assert.That(result.Result, Is.TypeOf<AcceptedAtActionResult>());

                var stored = harness.Authors.Get(harness.Author.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(stored.AudiobookMetadataProfileId, Is.EqualTo(harness.Author.AudiobookMetadataProfileId));
                    Assert.That(stored.EbookMetadataProfileId, Is.EqualTo(harness.Author.EbookMetadataProfileId));
                    Assert.That(stored.AudiobookQualityProfileId, Is.EqualTo(harness.Author.AudiobookQualityProfileId));
                    Assert.That(stored.EbookQualityProfileId, Is.EqualTo(harness.Author.EbookQualityProfileId));
                    Assert.That(stored.AudiobookRootFolderPath, Is.EqualTo(harness.Author.AudiobookRootFolderPath));
                    Assert.That(stored.EbookRootFolderPath, Is.EqualTo(harness.Author.EbookRootFolderPath));
                    Assert.That(ebook ? stored.EbookMonitored : stored.AudiobookMonitored, Is.EqualTo(!toggle));
                    Assert.That(ebook ? stored.AudiobookMonitored : stored.EbookMonitored, Is.Null);
                    Assert.That(ebook ? stored.AudiobookMonitorNewItems : stored.EbookMonitorNewItems, Is.Null);
                    Assert.That(stored.AudiobookTags, Is.EquivalentTo(harness.Author.AudiobookTags));
                    Assert.That(stored.EbookTags, Is.EquivalentTo(harness.Author.EbookTags));
                    Assert.That(harness.Commands, Is.Empty);
                });
            });
        }

        [Test]
        public void explicit_wrong_side_profile_is_still_rejected_without_saving(
            [Values(false, true)] bool postgres, [Values(false, true)] bool ebook)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var harness = new Harness(database, ebook, false);
                var resource = harness.GetPayload();
                if (ebook) resource.AudiobookMetadataProfileId = harness.Author.EbookMetadataProfileId;
                else resource.EbookMetadataProfileId = harness.Author.AudiobookMetadataProfileId;

                var error = Assert.Throws<ValidationException>(() => harness.Validate(resource));
                Assert.That(error.Errors, Has.Some.Matches<FluentValidation.Results.ValidationFailure>(failure =>
                    failure.PropertyName == (ebook ? "AudiobookMetadataProfileId" : "EbookMetadataProfileId") &&
                    failure.ErrorMessage.Contains("incompatible")));
                var stored = harness.Authors.Get(harness.Author.Id);
                Assert.That(ebook ? stored.AudiobookMetadataProfileId : stored.EbookMetadataProfileId, Is.Null);
            });
        }

        [Test]
        public void legacy_only_request_maps_and_saves_to_its_explicit_format(
            [Values(false, true)] bool postgres, [Values(false, true)] bool ebook)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var harness = new Harness(database, ebook, false);
                var resource = new AuthorResource
                {
                    Id = harness.Author.Id,
                    AuthorName = harness.Author.Name,
                    ForeignAuthorId = harness.Author.GoodreadsAuthorId,
                    Path = harness.Author.Path,
                    RootFolderPath = ebook ? harness.Author.EbookRootFolderPath : harness.Author.AudiobookRootFolderPath,
                    QualityProfileId = ebook ? harness.Author.EbookQualityProfileId : harness.Author.AudiobookQualityProfileId,
                    MetadataProfileId = harness.Author.MetadataProfileId,
                    Monitored = true,
                    MonitorNewItems = "none"
                };

                var scope = ebook ? "ebook" : "audiobook";
                harness.Controller.HttpContext.Request.QueryString = new QueryString("?mediaType=" + scope);
                harness.Validate(resource);
                Assert.That(harness.Controller.UpdateAuthor(resource, mediaType: scope).Result, Is.TypeOf<AcceptedAtActionResult>());
                var stored = harness.Authors.Get(harness.Author.Id);
                Assert.That(stored.AudiobookMetadataProfileId, Is.EqualTo(harness.Author.AudiobookMetadataProfileId));
                Assert.That(stored.EbookMetadataProfileId, Is.EqualTo(harness.Author.EbookMetadataProfileId));
                Assert.That(ebook ? stored.AudiobookMonitored : stored.EbookMonitored, Is.Null);
            });
        }

        [Test]
        public void book_identity_change_updates_the_author_page([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database, ebook: false, generalLegacyProfile: false, connected: true);
                h.Controller.Handle(new LibraryIdentityChangedEvent(new[] { h.Author.Id }, new[] { 10, 11 }, new[] { 11 }));
                Assert.That(h.Broadcasts, Has.Count.EqualTo(1));
                Assert.That(h.Broadcasts.Single().Action, Is.EqualTo(ModelAction.Updated));
                Assert.That(((ResourceChangeMessage<AuthorResource>)h.Broadcasts.Single().Body).Resource.Id, Is.EqualTo(h.Author.Id));
            });
        }

        private sealed class Harness
        {
            public AuthorController Controller { get; }
            public AuthorRepository Authors { get; }
            public Author Author { get; }
            public List<Command> Commands { get; } = new List<Command>();
            public List<SignalRMessage> Broadcasts { get; } = new();
            public List<MonitoringConfig> Adds { get; } = new();
            public IRootFolderService Roots { get; }
            public IAuthorService AuthorService { get; }
            public Action BeforePhotoDownloaded;
            public Func<Author, EnsureImageResult> PhotoResult;
            public List<object> Events { get; } = new();

            public Harness(IMainDatabase database, bool ebook, bool generalLegacyProfile, bool connected = false, bool? initialSync = null)
            {
                var logger = LogManager.GetCurrentClassLogger();
                var events = Proxy<IEventAggregator>((method, args) => { Events.Add(args[0]); return null; });
                var formats = Proxy<ICustomFormatService>((method, args) =>
                    method.Name == "All" ? new List<CustomFormat>() : throw new NotImplementedException(method.Name));
                var quality = new QualityProfileService(new QualityProfileRepository(database, events, formats),
                    null, null, formats, null, null, logger);
                var metadata = new MetadataProfileService(new MetadataProfileRepository(database, events),
                    null, null, null, null, null, null, null, events, logger);
                var qualityId = quality.Add(new QualityProfile
                {
                    Name = "Selected quality",
                    ProfileType = ebook ? ProfileType.Ebook : ProfileType.Audiobook,
                    Items = new List<QualityProfileQualityItem>()
                }).Id;
                var metadataId = metadata.Add(new MetadataProfile
                {
                    Name = "Selected metadata",
                    ProfileType = ebook ? MetadataProfileType.Ebook : MetadataProfileType.Audiobook
                }).Id;
                var otherQualityId = initialSync.HasValue ? quality.Add(new QualityProfile
                {
                    Name = "Other format quality", ProfileType = ebook ? ProfileType.Audiobook : ProfileType.Ebook,
                    Items = new List<QualityProfileQualityItem>()
                }).Id : 0;
                var otherMetadataId = initialSync.HasValue ? metadata.Add(new MetadataProfile
                {
                    Name = "Other format metadata", ProfileType = ebook ? MetadataProfileType.Audiobook : MetadataProfileType.Ebook
                }).Id : 0;
                var legacyId = generalLegacyProfile
                    ? metadata.Add(new MetadataProfile { Name = "Legacy general", ProfileType = MetadataProfileType.General }).Id
                    : metadataId;
                var rootPath = @"C:\fixture\books".AsOsAgnostic();
                var folders = new List<RootFolder>
                {
                    new RootFolder { Path = rootPath, FolderType = initialSync.HasValue ? FolderType.Mixed : ebook ? FolderType.Ebook : FolderType.Audiobook }
                };
                var roots = Proxy<IRootFolderService>((method, args) =>
                    method.Name switch
                    {
                        nameof(IRootFolderService.All) => folders,
                        nameof(IRootFolderService.GetBestRootFolder) => folders.Single(),
                        _ => throw new NotImplementedException(method.Name)
                    });
                Roots = roots;
                var commands = Proxy<IManageCommandQueue>((method, args) =>
                {
                    if (method.Name == nameof(IManageCommandQueue.PushMany))
                    {
                        var batch = ((IEnumerable<Command>)args[0]).ToList();
                        Commands.AddRange(batch);
                        return batch.Select(item => new CommandModel { Body = item }).ToList();
                    }
                    if (method.Name != nameof(IManageCommandQueue.Push) || args[0] is not Command command)
                        throw new NotImplementedException(method.Name);
                    Commands.Add(command);
                    return new CommandModel { Body = command };
                });
                Authors = new AuthorRepository(database, events, logger);
                var authorService = new AuthorService(Authors, events, null, roots, commands, new CacheManager(),
                    new BookRepository(database, events), null, logger, null, null, null, database);
                AuthorService = authorService;
                Author = authorService.AddAuthor(new Author
                {
                    Name = "Ted Chiang",
                    GoodreadsAuthorId = "gr:130698",
                    Path = Path.Combine(rootPath, "Ted Chiang"),
                    MetadataProfileId = legacyId,
                    AudiobookQualityProfileId = ebook ? (initialSync.HasValue ? otherQualityId : null) : qualityId,
                    EbookQualityProfileId = ebook ? qualityId : (initialSync.HasValue ? otherQualityId : null),
                    AudiobookMetadataProfileId = ebook ? (initialSync.HasValue ? otherMetadataId : null) : metadataId,
                    EbookMetadataProfileId = ebook ? metadataId : (initialSync.HasValue ? otherMetadataId : null),
                    AudiobookRootFolderPath = !ebook || initialSync.HasValue ? rootPath : null,
                    EbookRootFolderPath = ebook || initialSync.HasValue ? rootPath : null,
                    AudiobookMonitored = !ebook || initialSync.HasValue ? true : null,
                    EbookMonitored = ebook || initialSync.HasValue ? true : null,
                    SyncMonitoredAcrossFormats = initialSync,
                    AudiobookMonitorNewItems = initialSync.HasValue ? NewItemMonitorTypes.All : ebook ? null : NewItemMonitorTypes.None,
                    EbookMonitorNewItems = initialSync.HasValue ? (initialSync.Value ? NewItemMonitorTypes.All : NewItemMonitorTypes.New) : ebook ? NewItemMonitorTypes.None : null,
                    AudiobookTags = ebook ? new HashSet<int>() : new HashSet<int> { 9 },
                    EbookTags = ebook ? new HashSet<int> { 9 } : new HashSet<int>()
                }, doRefresh: false);
                Commands.Clear();

                var books = Proxy<IBookService>((method, args) => method.Name switch
                {
                    nameof(IBookService.GetNextBooksByAuthorId) or nameof(IBookService.GetLastBooksByAuthorId) => new List<Book>(),
                    _ => throw new NotImplementedException(method.Name)
                });
                var statistics = Proxy<IAuthorStatisticsService>((method, args) =>
                    method.Name == nameof(IAuthorStatisticsService.AuthorStatistics)
                        ? new AuthorStatistics { AuthorId = Author.Id } : throw new NotImplementedException(method.Name));
                var covers = Proxy<IMapCoversToLocal>((method, args) =>
                {
                    if (method.Name != nameof(IMapCoversToLocal.EnsureAuthorImage)) return null;
                    BeforePhotoDownloaded?.Invoke();
                    return Task.FromResult(PhotoResult?.Invoke((Author)args[0]) ?? new EnsureImageResult { State = "downloaded", Path = "verified.png" });
                });
                var config = Proxy<IConfigService>((method, args) =>
                    method.Name == "get_RecycleBin" ? string.Empty : throw new NotImplementedException(method.Name));
                var runtime = Proxy<IRuntimeInfo>((method, args) =>
                    method.Name == "get_IsWindowsService" ? false : throw new NotImplementedException(method.Name));
                var names = Proxy<IBuildFileNames>((method, args) =>
                    method.Name == nameof(IBuildFileNames.GetAuthorFolder) ? "Ted Chiang" : throw new NotImplementedException(method.Name));
                var signalR = Proxy<IBroadcastSignalRMessage>((method, args) =>
                {
                    if (method.Name == "get_IsConnected") return connected;
                    if (method.Name == nameof(IBroadcastSignalRMessage.BroadcastMessage))
                    {
                        Broadcasts.Add((SignalRMessage)args[0]);
                        return Task.CompletedTask;
                    }
                    throw new NotImplementedException(method.Name);
                });
                var library = Proxy<IAuthorLibraryService>((method, args) =>
                {
                    if (method.Name != nameof(IAuthorLibraryService.AddAuthorAsync)) throw new NotImplementedException(method.Name);
                    Adds.Add((MonitoringConfig)args[1]);
                    return Task.FromResult(new Author { Id = -77 });
                });
                Controller = new AuthorController(signalR,
                    authorService, books, null, library, statistics, covers, commands, roots, events, null, names, logger,
                    new RecycleBinValidator(config), new RootFolderValidator(roots), new MappedNetworkDriveValidator(runtime, null),
                    new AuthorPathValidator(authorService), new AuthorExistsValidator(authorService), new AuthorAncestorValidator(authorService),
                    new SystemFolderValidator(), new QualityProfileExistsValidator(quality), new MetadataProfileExistsValidator(metadata),
                    new AuthorFolderAsRootFolderValidator(names))
                {
                    ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
                };
            }

            public AuthorResource GetPayload()
            {
                var resource = STJson.Deserialize<AuthorResource>(STJson.ToJson(Controller.GetAuthorById(Author.Id).Value));
                resource.SyncMonitoredAcrossFormats = null;
                resource.AudiobookMonitorNewItems = null;
                resource.EbookMonitorNewItems = null;
                return resource;
            }

            public void Validate(AuthorResource resource)
            {
                var context = Controller.HttpContext;
                context.Request.Method = HttpMethods.Put;
                var action = new ActionContext(context, new RouteData(), new ControllerActionDescriptor
                {
                    MethodInfo = typeof(AuthorController).GetMethod(nameof(AuthorController.UpdateAuthor))
                }, new ModelStateDictionary());
                Controller.OnActionExecuting(new ActionExecutingContext(action, new List<IFilterMetadata>(),
                    new Dictionary<string, object> { ["authorResource"] = resource }, Controller));
            }
        }
    }
}
