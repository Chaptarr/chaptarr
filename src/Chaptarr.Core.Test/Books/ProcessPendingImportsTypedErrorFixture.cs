using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Chaptarr.Core.Test.Common;
using FluentValidation;
using FluentValidation.Results;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    [NonParallelizable]
    public class ProcessPendingImportsTypedErrorFixture
    {
        public static IEnumerable<TestCaseData> Errors()
        {
            var errors = new[]
            {
                ("untyped", "Invalid response while the upstream service restarts."),
                ("untyped", "Quality profile lookup timed out temporarily."),
                ("untyped", "Metadata profile lookup timed out temporarily."),
                ("untyped", "Root folder scan was temporarily interrupted."),
                ("untyped", "Requested book remained unmonitored while the writer was unavailable."),
                ("untyped", "Réponse invalide pendant le redémarrage du service."),
                ("untyped", "Ungültige Antwort während des Dienstneustarts."),
                ("untyped", "Ogiltigt svar medan tjänsten startar om."),
                ("author", "Identität ist nicht eindeutig."),
                ("work", "El trabajo no puede recuperarse."),
                ("excluded", "La lista excluye al autor.")
            };
            foreach (var postgres in new[] { false, true })
            {
                for (var index = 0; index < errors.Length; index++)
                {
                    yield return new TestCaseData(postgres, errors[index].Item1, errors[index].Item2)
                        .SetName($"pending_error_{index}_{(postgres ? "postgres" : "sqlite")}");
                }
            }
        }

        [TestCaseSource(nameof(Errors))]
        public void author_only_pending_outcome_should_follow_declared_error_type_without_matching_message(bool postgres, string kind, string message)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                const string providerId = "gr:562254";
                var logger = LogManager.GetCurrentClassLogger();
                var published = new List<object>();
                var events = InterfaceProxy.Proxy<IEventAggregator>((_, args) =>
                {
                    published.Add(args[0]);
                    return null;
                });
                var repository = new PendingAuthorImportRepository(database, events);
                var pending = new PendingAuthorImportService(repository,
                    InterfaceProxy.Proxy<IAuthorService>((_, _) => null), events, logger);
                var id = pending.EnqueueAsync(providerId, new MonitoringConfig
                {
                    CreateAudiobook = true,
                    CreateEbook = false,
                    AudiobookQualityProfileId = 1,
                    AudiobookMetadataProfileId = 1,
                    AudiobookRootFolderPath = System.IO.Path.GetTempPath(),
                    AudiobookMonitored = true,
                    AudiobookMonitorExistingMode = MonitorTypes.All,
                    AudiobookMonitorNewItems = NewItemMonitorTypes.All,
                    AuthorName = "Requested Author"
                }, "ImportList:Typed error control").GetAwaiter().GetResult();
                var original = repository.Get(id);
                Exception error = kind switch
                {
                    "author" => new AuthorTerminalException("author_identity_ambiguous", providerId, providerId, message, true),
                    "work" => new WorkRescueTerminalException("gr:1001", message),
                    "excluded" => new ValidationException(new[]
                    {
                        new ValidationFailure("Author", message) { ErrorCode = AuthorLibraryService.ImportListAuthorExcludedErrorCode }
                    }),
                    _ => new InvalidOperationException(message)
                };
                var attempts = 0;
                var library = InterfaceProxy.Proxy<IAuthorLibraryService>((method, _) =>
                {
                    Assert.That(method.Name, Is.EqualTo(nameof(IAuthorLibraryService.AddAuthorAsync)));
                    attempts++;
                    return Task.FromException<Author>(error);
                });
                var handler = new ProcessPendingImportsCommandHandler(pending, library, null, null, null, events, logger);
                handler.Execute(new ProcessPendingImportsCommand());
                var saved = repository.Get(id);
                var terminal = kind != "untyped";
                Assert.Multiple(() =>
                {
                    Assert.That(saved.OverallStatus, Is.EqualTo(terminal ? PendingImportStatus.Failed : PendingImportStatus.Retrying), saved.LastError);
                    Assert.That(saved.AttemptCount, Is.EqualTo(terminal ? 0 : 1));
                    Assert.That(saved.LastError, Is.EqualTo(error.Message));
                    Assert.That(saved.LastAttemptAt, Is.Not.Null);
                    Assert.That(saved.AudiobookQualityProfileId, Is.EqualTo(original.AudiobookQualityProfileId));
                    Assert.That(saved.AudiobookMetadataProfileId, Is.EqualTo(original.AudiobookMetadataProfileId));
                    Assert.That(saved.AudiobookRootFolderPath, Is.EqualTo(original.AudiobookRootFolderPath));
                    Assert.That(saved.AudiobookMonitored, Is.True);
                    Assert.That(saved.AudiobookMonitorExistingMode, Is.EqualTo(MonitorTypes.All));
                    Assert.That(saved.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.All));
                    Assert.That(saved.AuthorName, Is.EqualTo(original.AuthorName));
                    Assert.That(saved.EbookStatus, Is.EqualTo(PendingImportStatus.NotRequested));
                    Assert.That(attempts, Is.EqualTo(1));
                    Assert.That(published.OfType<PendingAuthorImportFailedEvent>().Count(), Is.EqualTo(terminal ? 1 : 0));
                    Assert.That(repository.GetActiveByProviderId(providerId) != null, Is.EqualTo(!terminal));
                });
                if (terminal)
                {
                    Assert.That(pending.GetDueForProcessing(), Is.Empty);
                    return;
                }

                Assert.That(saved.NextAttemptAt, Is.GreaterThan(DateTime.UtcNow));
                Assert.That(pending.GetDueForProcessing(), Is.Empty);
                saved.NextAttemptAt = DateTime.UtcNow.AddMinutes(-1);
                repository.Update(saved);
                Assert.That(pending.GetDueForProcessing().Select(item => item.Id), Is.EqualTo(new[] { id }));
                handler.Execute(new ProcessPendingImportsCommand());
                var retried = repository.GetActiveByProviderId(providerId);
                Assert.Multiple(() =>
                {
                    Assert.That(retried, Is.Not.Null);
                    Assert.That(retried.OverallStatus, Is.EqualTo(PendingImportStatus.Retrying));
                    Assert.That(retried.AttemptCount, Is.EqualTo(2));
                    Assert.That(retried.LastError, Is.EqualTo(error.Message));
                    Assert.That(retried.AudiobookQualityProfileId, Is.EqualTo(original.AudiobookQualityProfileId));
                    Assert.That(retried.AudiobookMetadataProfileId, Is.EqualTo(original.AudiobookMetadataProfileId));
                    Assert.That(retried.AudiobookRootFolderPath, Is.EqualTo(original.AudiobookRootFolderPath));
                    Assert.That(retried.AudiobookMonitored, Is.True);
                    Assert.That(retried.AudiobookMonitorExistingMode, Is.EqualTo(MonitorTypes.All));
                    Assert.That(retried.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.All));
                    Assert.That(retried.AuthorName, Is.EqualTo(original.AuthorName));
                    Assert.That(attempts, Is.EqualTo(2));
                    Assert.That(published.OfType<PendingAuthorImportFailedEvent>(), Is.Empty);
                });
            });
        }
    }
}
