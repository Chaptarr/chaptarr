using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class AuthorRootFolderBackfillServiceFixture
    {
        private List<Author> _authors;
        private List<Author> _updated;
        private List<Command> _pushed;
        private AuthorRootFolderBackfillService _service;

        [SetUp]
        public void Setup()
        {
            _authors = new List<Author>();
            _updated = new List<Author>();
            _pushed = new List<Command>();

            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            var authorProxy = (AuthorServiceProxy)(object)authorService;
            authorProxy.Owner = this;

            var rootService = DispatchProxy.Create<IRootFolderService, RootFolderServiceProxy>();

            var config = DispatchProxy.Create<IConfigService, Chaptarr.Core.Test.ConfigServiceTestProxy>();

            _service = new AuthorRootFolderBackfillService(authorService, rootService, config, new RecordingQueue(_pushed), LogManager.GetCurrentClassLogger());
        }

        private Author AddAuthor(int id, string audiobookRoot, string ebookRoot, bool audioOverridden = false, bool ebookOverridden = false, int? qualityProfileId = 1)
        {
            var author = new Author
            {
                Id = id,
                Name = "Author " + id,
                AudiobookRootFolderPath = audiobookRoot,
                EbookRootFolderPath = ebookRoot,
                AudiobookQualityProfileId = qualityProfileId,
                EbookQualityProfileId = qualityProfileId,
                AudiobookSettingsManuallyOverridden = audioOverridden,
                EbookSettingsManuallyOverridden = ebookOverridden
            };
            _authors.Add(author);
            return author;
        }

        [Test]
        public void should_skip_a_missing_side_that_has_no_quality_profile()
        {
            var author = AddAuthor(9, "/lib/audio", null, qualityProfileId: null);

            var result = _service.Backfill(false);

            Assert.That(author.EbookRootFolderPath, Is.Null);
            Assert.That(result.SkippedNoProfile, Is.EqualTo(1));
            Assert.That(result.Updated, Is.EqualTo(0));
        }

        [Test]
        public void should_fill_only_the_missing_side_from_the_default_root()
        {
            var audioOnly = AddAuthor(1, "/lib/audio", null);
            var ebookOnly = AddAuthor(2, null, "/lib/ebooks");
            var both = AddAuthor(3, "/lib/audio", "/lib/other-ebooks");
            var neither = AddAuthor(4, null, null);

            var result = _service.Backfill(false);

            Assert.Multiple(() =>
            {
                Assert.That(audioOnly.EbookRootFolderPath, Is.EqualTo("/lib/ebooks"));
                Assert.That(audioOnly.AudiobookRootFolderPath, Is.EqualTo("/lib/audio"));
                Assert.That(ebookOnly.AudiobookRootFolderPath, Is.EqualTo("/lib/audio"));
                Assert.That(both.EbookRootFolderPath, Is.EqualTo("/lib/other-ebooks"));
                Assert.That(neither.AudiobookRootFolderPath, Is.Null);
                Assert.That(neither.EbookRootFolderPath, Is.Null);
                Assert.That(result.Candidates, Is.EqualTo(2));
                Assert.That(result.Updated, Is.EqualTo(2));
                Assert.That(_updated.Select(a => a.Id), Is.EquivalentTo(new[] { 1, 2 }));
            });
        }

        [Test]
        public void should_skip_a_missing_side_whose_settings_were_set_manually()
        {
            var author = AddAuthor(1, "/lib/audio", null, ebookOverridden: true);

            var result = _service.Backfill(false);

            Assert.Multiple(() =>
            {
                Assert.That(author.EbookRootFolderPath, Is.Null);
                Assert.That(result.SkippedManuallyOverridden, Is.EqualTo(1));
                Assert.That(result.Updated, Is.Zero);
                Assert.That(_updated, Is.Empty);
            });
        }

        [Test]
        public void should_change_nothing_in_a_dry_run_but_report_counts()
        {
            var author = AddAuthor(1, "/lib/audio", null);

            var result = _service.Backfill(true);

            Assert.Multiple(() =>
            {
                Assert.That(author.EbookRootFolderPath, Is.Null);
                Assert.That(result.Updated, Is.EqualTo(1));
                Assert.That(_updated, Is.Empty);
            });
        }

        [Test]
        public void should_queue_refreshes_in_batches_and_carry_the_remainder()
        {
            for (var i = 1; i <= 60; i++)
            {
                AddAuthor(i, "/lib/audio", null);
            }

            _service.Execute(new BackfillAuthorRootFoldersCommand());

            var refreshes = _pushed.OfType<RefreshAuthorCommand>().ToList();
            var continuation = _pushed.OfType<BackfillAuthorRootFoldersCommand>().Single();

            Assert.Multiple(() =>
            {
                Assert.That(refreshes, Has.Count.EqualTo(AuthorRootFolderBackfillService.RefreshBatchSize));
                Assert.That(refreshes.All(r => r.ForceRefresh && r.RefreshMetadata && !r.RescanFolders), Is.True);
                Assert.That(continuation.PendingRefreshAuthorIds, Has.Count.EqualTo(60 - AuthorRootFolderBackfillService.RefreshBatchSize));
            });

            _pushed.Clear();
            _updated.Clear();

            // The continuation only refreshes; it does not rescan authors for backfill again.
            _service.Execute(continuation);

            Assert.Multiple(() =>
            {
                Assert.That(_updated, Is.Empty);
                Assert.That(_pushed.OfType<RefreshAuthorCommand>().Count(), Is.EqualTo(25));
                Assert.That(_pushed.OfType<BackfillAuthorRootFoldersCommand>().Single().PendingRefreshAuthorIds, Has.Count.EqualTo(10));
            });
        }

        private class AuthorServiceProxy : DispatchProxy
        {
            public AuthorRootFolderBackfillServiceFixture Owner { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case nameof(IAuthorService.GetAllAuthors):
                        return Owner._authors.ToList();
                    case nameof(IAuthorService.UpdateAuthors):
                        var list = (List<Author>)args[0];
                        Owner._updated.AddRange(list);
                        return list;
                    default:
                        throw new NotImplementedException(targetMethod?.Name);
                }
            }
        }

        private class RootFolderServiceProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IRootFolderService.All))
                {
                    return new List<RootFolder>
                    {
                        new RootFolder { Path = "/lib/audio", FolderType = FolderType.Audiobook },
                        new RootFolder { Path = "/lib/ebooks", FolderType = FolderType.Ebook }
                    };
                }

                throw new NotImplementedException(targetMethod?.Name);
            }
        }

        private sealed class RecordingQueue : IManageCommandQueue
        {
            private readonly List<Command> _pushed;

            public RecordingQueue(List<Command> pushed)
            {
                _pushed = pushed;
            }

            public List<CommandModel> PushMany<TCommand>(List<TCommand> commands) where TCommand : Command
                => commands.Select(c => Push(c)).ToList();

            public CommandModel Push<TCommand>(TCommand command, CommandPriority priority = CommandPriority.Normal, CommandTrigger trigger = CommandTrigger.Unspecified) where TCommand : Command
            {
                _pushed.Add(command);
                return new CommandModel { Name = command.Name, Body = command };
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
    }
}
