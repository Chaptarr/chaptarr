using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Chaptarr.Api.V1.Author;
using Chaptarr.Core.Test;
using Chaptarr.Http.Middleware;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.RootFolders;
using CoreAuthor = NzbDrone.Core.Books.Author;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class AuthorEditorControllerMonitoredCascadeFixture
    {
        // Reuses the AuthorServiceProxy/RootFolderServiceProxy/RecordingCommandQueue conventions
        // already established in AuthorEditorMissingRootHydrationFixture, rather than hand-rolling
        // weaker doubles. Unlike that fixture's proxy, UpdateAuthors here also applies the real
        // Monitored recompute (author.Monitored = author.IsMonitoredFromMediaSettings()) that
        // AuthorService.UpdateAuthors actually does - that recompute IS the bug this fixture guards
        // against, so a stub that skips it would only prove the cascade wrote plausible-looking
        // tri-state fields, not that persisting monitored:false through this endpoint actually works.
        private class AuthorServiceProxy : DispatchProxy
        {
            public List<CoreAuthor> Authors { get; set; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case nameof(IAuthorService.GetAuthors):
                        var ids = ((IEnumerable<int>)args[0]).ToHashSet();
                        return Authors.Where(author => ids.Contains(author.Id)).ToList();
                    case nameof(IAuthorService.UpdateAuthors):
                        var updated = (List<CoreAuthor>)args[0];
                        foreach (var author in updated)
                        {
                            author.Monitored = author.IsMonitoredFromMediaSettings();
                        }

                        Authors = updated.ToList();
                        return Authors;
                    default:
                        throw new NotImplementedException($"Test proxy does not implement {targetMethod?.Name}");
                }
            }
        }

        private class RootFolderServiceProxy : DispatchProxy
        {
            public List<RootFolder> RootFolders { get; set; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    nameof(IRootFolderService.All) => RootFolders,
                    _ => throw new NotImplementedException($"Test proxy does not implement {targetMethod?.Name}")
                };
            }
        }

        private sealed class RecordingCommandQueue : IManageCommandQueue
        {
            public List<CommandModel> PushedCommands { get; } = new();

            public List<CommandModel> PushMany<TCommand>(List<TCommand> commands) where TCommand : Command
            {
                return commands.Select(command => Push(command)).ToList();
            }

            public CommandModel Push<TCommand>(TCommand command, CommandPriority priority = CommandPriority.Normal, CommandTrigger trigger = CommandTrigger.Unspecified) where TCommand : Command
            {
                var model = new CommandModel
                {
                    Name = command.Name,
                    Body = command,
                    Priority = priority,
                    Trigger = trigger,
                    Status = CommandStatus.Queued
                };

                PushedCommands.Add(model);
                return model;
            }

            public CommandModel Push(string commandName, DateTime? lastExecutionTime, DateTime? lastStartTime, CommandPriority priority = CommandPriority.Normal, CommandTrigger trigger = CommandTrigger.Unspecified) => throw new NotImplementedException();
            public IEnumerable<CommandModel> Queue(CancellationToken cancellationToken) => throw new NotImplementedException();
            public List<CommandModel> All() => throw new NotImplementedException();
            public CommandModel Get(int id) => throw new NotImplementedException();
            public List<CommandModel> GetStarted() => throw new NotImplementedException();
            public void SetMessage(CommandModel command, string message) => throw new NotImplementedException();
            public void TouchProgress(CommandModel command) => throw new NotImplementedException();
            public void SetResult(CommandModel command, CommandResult result) => throw new NotImplementedException();
            public void Start(CommandModel command) => throw new NotImplementedException();
            public void Complete(CommandModel command, string message) => throw new NotImplementedException();
            public void Fail(CommandModel command, string message, Exception e) => throw new NotImplementedException();
            public void Requeue() => throw new NotImplementedException();
            public void Cancel(int id) => throw new NotImplementedException();
            public void Pause(int id) => throw new NotImplementedException();
            public void Resume(int id) => throw new NotImplementedException();
            public void CleanCommands() => throw new NotImplementedException();
            public CancellationToken GetCancellationToken(int commandId) => throw new NotImplementedException();
            public void RegisterCancellationToken(int commandId, CancellationTokenSource cancellationTokenSource) => throw new NotImplementedException();
            public void UnregisterCancellationToken(int commandId) => throw new NotImplementedException();
        }

        private static AuthorEditorController BuildController(
            AuthorServiceProxy authorServiceProxy,
            List<RootFolder> rootFolders = null,
            RecordingCommandQueue commandQueue = null)
        {
            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            ((AuthorServiceProxy)(object)authorService).Authors = authorServiceProxy.Authors;

            var rootFolderService = DispatchProxy.Create<IRootFolderService, RootFolderServiceProxy>();
            ((RootFolderServiceProxy)(object)rootFolderService).RootFolders = rootFolders ?? new List<RootFolder>();

            return new AuthorEditorController(
                authorService,
                commandQueue ?? new RecordingCommandQueue(),
                rootFolderService,
                new TestQualityProfileService(),
                new TestMetadataProfileService());
        }

        [Test]
        public void should_persist_monitored_false_for_the_real_bulk_editor_ui_payload_shape()
        {
            // AuthorEditorFooter.js's buildMonitoringPayload sends { monitored, audiobookMonitorExisting,
            // ebookMonitorExisting } when the user picks "Unmonitored" in the bulk editor - MonitorFuture
            // is never included. An earlier version of this fix protected a whole media type the moment
            // EITHER of its two tri-state fields was present in the request, which meant the explicit
            // MonitorExisting:0 the UI sends made the cascade treat audiobook as "already handled" and
            // skip it - leaving MonitorFuture at its old `true`, so IsMonitoredFromMediaSettings() (and
            // therefore the recomputed Monitored flag) stayed true. This is the actual reported bug,
            // reached through the actual UI.
            var author = new CoreAuthor
            {
                Id = 1,
                AudiobookRootFolderPath = @"C:\audiobooks",
                AudiobookMonitorExisting = 1,
                AudiobookMonitorFuture = true,
                EbookRootFolderPath = @"C:\ebooks",
                EbookMonitorExisting = 2,
                EbookMonitorFuture = true
            };

            var authorServiceProxy = new AuthorServiceProxy { Authors = new List<CoreAuthor> { author } };
            var controller = BuildController(authorServiceProxy);

            controller.SaveAll(new AuthorEditorResource
            {
                AuthorIds = new List<int> { 1 },
                Monitored = false,
                AudiobookMonitorExisting = 0,
                EbookMonitorExisting = 0
                // AudiobookMonitorFuture / EbookMonitorFuture deliberately omitted - the UI never sends these
            });

            var updatedAuthor = authorServiceProxy.Authors.Single();

            Assert.Multiple(() =>
            {
                Assert.That(updatedAuthor.AudiobookMonitorExisting, Is.EqualTo(0), "the client's explicit edit");
                Assert.That(updatedAuthor.AudiobookMonitorFuture, Is.False, "untouched by the request, so the legacy flag must cascade into it");
                Assert.That(updatedAuthor.EbookMonitorExisting, Is.EqualTo(0));
                Assert.That(updatedAuthor.EbookMonitorFuture, Is.False);
                Assert.That(updatedAuthor.Monitored, Is.False, "this is what AuthorService.UpdateAuthors' real recompute reads - it must land on false, not silently stay true");
            });
        }

        [Test]
        public void should_not_force_future_monitoring_on_for_the_real_bulk_editor_monitored_true_payload()
        {
            // The bulk editor's "Monitored" (on) action sends the same shape as "Unmonitored" - just
            // MonitorExisting, never MonitorFuture. Unlike turning off, MonitorExisting alone already
            // satisfies monitored:true, so this must NOT silently flip on "monitor new releases" for a
            // media type whose future-monitoring preference this request never touched.
            var author = new CoreAuthor
            {
                Id = 1,
                AudiobookRootFolderPath = @"C:\audiobooks",
                AudiobookMonitorExisting = 0,
                AudiobookMonitorFuture = false,
                EbookRootFolderPath = @"C:\ebooks",
                EbookMonitorExisting = 0,
                EbookMonitorFuture = false
            };

            var authorServiceProxy = new AuthorServiceProxy { Authors = new List<CoreAuthor> { author } };
            var controller = BuildController(authorServiceProxy);

            controller.SaveAll(new AuthorEditorResource
            {
                AuthorIds = new List<int> { 1 },
                Monitored = true,
                AudiobookMonitorExisting = 1,
                EbookMonitorExisting = 1
                // AudiobookMonitorFuture / EbookMonitorFuture deliberately omitted, same as the real UI
            });

            var updatedAuthor = authorServiceProxy.Authors.Single();

            Assert.Multiple(() =>
            {
                Assert.That(updatedAuthor.AudiobookMonitorExisting, Is.EqualTo(1), "the client's explicit edit");
                Assert.That(updatedAuthor.AudiobookMonitorFuture, Is.False, "not forced on - Existing alone already satisfies monitored:true");
                Assert.That(updatedAuthor.EbookMonitorExisting, Is.EqualTo(1));
                Assert.That(updatedAuthor.EbookMonitorFuture, Is.False);
                Assert.That(updatedAuthor.Monitored, Is.True);
            });
        }

        [Test]
        public void should_cascade_bulk_unmonitor_independently_per_author()
        {
            // Two authors with genuinely different prior monitoring states in the same bulk request:
            // author 1 is monitored via audiobook only, author 2 via ebook only. Both should end up
            // fully unmonitored, and neither author's snapshot/cascade should leak into the other's.
            var audiobookMonitoredAuthor = new CoreAuthor
            {
                Id = 1,
                AudiobookRootFolderPath = @"C:\audiobooks",
                AudiobookMonitorExisting = 1,
                AudiobookMonitorFuture = true,
                EbookRootFolderPath = @"C:\ebooks",
                EbookMonitorExisting = 0,
                EbookMonitorFuture = false
            };
            var ebookMonitoredAuthor = new CoreAuthor
            {
                Id = 2,
                AudiobookRootFolderPath = @"C:\audiobooks",
                AudiobookMonitorExisting = 0,
                AudiobookMonitorFuture = false,
                EbookRootFolderPath = @"C:\ebooks",
                EbookMonitorExisting = 2,
                EbookMonitorFuture = true
            };

            var authorServiceProxy = new AuthorServiceProxy { Authors = new List<CoreAuthor> { audiobookMonitoredAuthor, ebookMonitoredAuthor } };
            var controller = BuildController(authorServiceProxy);

            controller.SaveAll(new AuthorEditorResource
            {
                AuthorIds = new List<int> { 1, 2 },
                Monitored = false
            });

            var updatedAuthor1 = authorServiceProxy.Authors.Single(a => a.Id == 1);
            var updatedAuthor2 = authorServiceProxy.Authors.Single(a => a.Id == 2);

            Assert.Multiple(() =>
            {
                Assert.That(updatedAuthor1.AudiobookMonitorFuture, Is.False);
                Assert.That(updatedAuthor1.AudiobookMonitorExisting, Is.EqualTo(0));
                Assert.That(updatedAuthor1.Monitored, Is.False);

                Assert.That(updatedAuthor2.EbookMonitorFuture, Is.False);
                Assert.That(updatedAuthor2.EbookMonitorExisting, Is.EqualTo(0));
                Assert.That(updatedAuthor2.Monitored, Is.False);
            });
        }

        [Test]
        public void should_not_override_a_tri_state_field_the_bulk_request_genuinely_changes()
        {
            var author = new CoreAuthor
            {
                Id = 1,
                AudiobookRootFolderPath = @"C:\audiobooks",
                AudiobookMonitorExisting = 1,
                AudiobookMonitorFuture = false, // stored value the bulk edit is about to genuinely change
                EbookRootFolderPath = @"C:\ebooks",
                EbookMonitorExisting = 2,
                EbookMonitorFuture = true
            };

            var authorServiceProxy = new AuthorServiceProxy { Authors = new List<CoreAuthor> { author } };
            var controller = BuildController(authorServiceProxy);

            controller.SaveAll(new AuthorEditorResource
            {
                AuthorIds = new List<int> { 1 },
                Monitored = false,
                AudiobookMonitorFuture = true // explicit bulk edit: turn audiobook future-monitoring ON, genuinely differs from the stored false
            });

            var updatedAuthor = authorServiceProxy.Authors.Single();

            Assert.Multiple(() =>
            {
                Assert.That(updatedAuthor.AudiobookMonitorFuture, Is.True, "client's genuine bulk edit to this field wins");
                Assert.That(updatedAuthor.AudiobookMonitorExisting, Is.EqualTo(0), "untouched by the request, so the legacy flag cascades into it");
                Assert.That(updatedAuthor.EbookMonitorFuture, Is.False, "not touched by the bulk edit at all, so the legacy flag cascades into it");
                Assert.That(updatedAuthor.EbookMonitorExisting, Is.EqualTo(0));
            });
        }

        [Test]
        public void should_do_nothing_when_bulk_monitored_is_omitted()
        {
            var author = new CoreAuthor
            {
                Id = 1,
                AudiobookRootFolderPath = @"C:\audiobooks",
                AudiobookMonitorExisting = 1,
                AudiobookMonitorFuture = true
            };

            var authorServiceProxy = new AuthorServiceProxy { Authors = new List<CoreAuthor> { author } };
            var controller = BuildController(authorServiceProxy);

            controller.SaveAll(new AuthorEditorResource
            {
                AuthorIds = new List<int> { 1 },
                Monitored = null
            });

            var updatedAuthor = authorServiceProxy.Authors.Single();

            Assert.Multiple(() =>
            {
                Assert.That(updatedAuthor.AudiobookMonitorFuture, Is.True);
                Assert.That(updatedAuthor.AudiobookMonitorExisting, Is.EqualTo(1));
            });
        }

        [Test]
        public void should_cascade_a_newly_added_root_folder_media_type_in_a_bulk_request()
        {
            // Mirrors the single-author "gained a root folder in this exact request" case, but through
            // the real SaveAll loop: this is the one branch whose correctness depends on the cascade
            // running AFTER this author's own root-folder writes in the loop, not before - a change
            // that moved the cascade call earlier in the method would break this silently.
            var author = new CoreAuthor
            {
                Id = 1
                // no root folders configured at all yet
            };

            var authorServiceProxy = new AuthorServiceProxy { Authors = new List<CoreAuthor> { author } };
            var rootFolders = new List<RootFolder>
            {
                new RootFolder { Path = @"C:\ebooks", FolderType = FolderType.Ebook }
            };
            var controller = BuildController(authorServiceProxy, rootFolders);

            controller.SaveAll(new AuthorEditorResource
            {
                AuthorIds = new List<int> { 1 },
                Monitored = false,
                EbookRootFolderPath = @"C:\ebooks" // assigned by this same request
            });

            var updatedAuthor = authorServiceProxy.Authors.Single();

            Assert.Multiple(() =>
            {
                Assert.That(updatedAuthor.EbookRootFolderPath, Is.EqualTo(@"C:\ebooks"));
                Assert.That(updatedAuthor.EbookMonitorFuture, Is.False);
                Assert.That(updatedAuthor.EbookMonitorExisting, Is.EqualTo(0));
            });
        }

        [Test]
        public void should_not_cascade_under_readarr_facade_context()
        {
            // A Readarr-facade client (e.g. a media-type-scoped path like /audiobook/api/v1/author/editor)
            // reaches this same endpoint. Unlike AuthorController.UpdateAuthor, this endpoint isn't
            // media-type-scoped by its own resource shape, so nothing else stops a facade request's
            // "monitored" from cascading into both media types - it has to be skipped explicitly.
            var author = new CoreAuthor
            {
                Id = 1,
                AudiobookRootFolderPath = @"C:\audiobooks",
                AudiobookMonitorExisting = 1,
                AudiobookMonitorFuture = true,
                EbookRootFolderPath = @"C:\ebooks",
                EbookMonitorExisting = 2,
                EbookMonitorFuture = true
            };

            var authorServiceProxy = new AuthorServiceProxy { Authors = new List<CoreAuthor> { author } };
            var controller = BuildController(authorServiceProxy);
            controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext()
            };
            controller.HttpContext.Items[ReadarrFacadeContext.ItemKey] = new ReadarrFacadeContext("gr", "audiobook", "readarr");

            controller.SaveAll(new AuthorEditorResource
            {
                AuthorIds = new List<int> { 1 },
                Monitored = false
            });

            var updatedAuthor = authorServiceProxy.Authors.Single();

            Assert.Multiple(() =>
            {
                Assert.That(updatedAuthor.AudiobookMonitorFuture, Is.True, "facade context present - the cascade must not run at all here");
                Assert.That(updatedAuthor.EbookMonitorFuture, Is.True);
            });
        }
    }
}
