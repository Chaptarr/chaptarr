using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Chaptarr.Core.Test.Common;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NUnit.Framework;

namespace Chaptarr.Core.Test.MediaCover
{
    [TestFixture]
    public class EditionSelectionNotificationFixture
    {
        // This inventories known writer APIs, not runtime control flow or arbitrary SQL.
        // Transaction-only writers name their delegation boundary; producer routes name the notification.
        // Symbol presence is not proof of delivery. Keep behavioral commit/failure tests alongside producers.
        private static readonly Dictionary<string, (int Calls, string Symbol, string Route)> Routes = new()
        {
            ["AddBookService.CompleteRequestedNarration.AddWantedEditions"] = (1, "NotifyBookSelectionsChanged", "Standalone AddBook finally notifies the saved narration targets"),
            ["ProcessPendingImportsCommandHandler.PinRequestedNarrations.AddWantedEditions"] = (1, "NotifyBookSelectionsChanged", "Notify saved selections before later fallible pending-import bookkeeping"),
            ["ImportListSyncService.ProcessListItems.AddWantedEditions"] = (1, "NotifyBookSelectionsChanged", "Notify saved selections before later fallible list bookkeeping"),
            ["AddBookService.EnsureAutoSelectedEdition.SetMonitored"] = (1, "NotifyBookSelectionsChanged", "Initial selection only (no previously monitored edition); BookAddedHandler queues author refresh when requested, otherwise startup/daily repair"),
            ["AuthorLibraryService.ProcessBooksForAuthor.InsertMany"] = (1, "AuthorRefreshCompleteEvent", "Author import publishes AuthorRefreshCompleteEvent"),
            ["AuthorLibraryService.SelectMonitoredEditionsForMediaType.UpdateMany"] = (1, "AuthorRefreshCompleteEvent", "Author import publishes AuthorRefreshCompleteEvent"),
            ["BackfillMatchingTitlesCommandHandler.Execute.UpdateMany"] = (1, "RebuildIndex", "Matching-title backfill preserves selection; no cover change"),
            ["BookService.AddBook.InsertMany"] = (1, "BookAddedEvent", "New book; BookAddedHandler queues author refresh when requested, otherwise startup/daily repair"),
            ["BookService.AddBook.SetMonitored"] = (1, "BookAddedEvent", "New book; BookAddedHandler queues author refresh when requested, otherwise startup/daily repair"),
            ["BookService.PersistWithLifecycle.UpdateMany"] = (2, "PublishBookEditedEvents", "BookController inline EnsureBookCovers; refresh/import completion for other callers"),
            ["BookService.SelectWantedEditions.UpdateSelection"] = (1, null, "delegation, unchecked: AddBookService, ImportListSyncService and ProcessPendingImportsCommandHandler notify after operation"),
            ["EditionService.InsertMany.InsertMany"] = (2, null, "delegation, unchecked: enclosing callers above own notifications after the transaction"),
            ["EditionService.RetryEditionInsertWithDeduplicatedTitleSlugs.InsertMany"] = (1, null, "delegation, unchecked: enclosing callers above own notifications after the transaction"),
            ["EditionService.UpdateMany.UpdateMany"] = (2, null, "delegation, unchecked: enclosing callers above own notifications after the transaction"),
            ["EditionService.UpdateSelection.UpdateSelection"] = (1, null, "delegation, unchecked: enclosing callers above own notifications after the transaction"),
            ["EditionService.SetMonitored.SetMonitored"] = (1, null, "delegation, unchecked: enclosing callers above own notifications after the transaction"),
            ["EditionRepository.Update.UpdateMany"] = (1, null, "delegation, unchecked: enclosing callers above own notifications after the transaction"),
            ["EditionRepository.UpdateMany.UpdateMany"] = (1, null, "delegation, unchecked: enclosing callers above own notifications after the transaction"),
            ["EditionRepository.Insert.InsertMany"] = (1, null, "delegation, unchecked: enclosing callers above own notifications after the transaction"),
            ["EditionRepository.InsertMany.InsertMany"] = (1, null, "delegation, unchecked: enclosing callers above own notifications after the transaction"),
            ["EditionRepository.InsertMany.PrepareSelection"] = (1, null, "delegation, unchecked: enclosing callers above own notifications after the transaction"),
            ["EditionRepository.UpdateMany.PrepareSelection"] = (1, null, "delegation, unchecked: enclosing callers above own notifications after the transaction"),
            ["EditionRepository.SetMonitored.UpdateSelection"] = (1, null, "delegation, unchecked: enclosing callers above own notifications after the transaction"),
            ["EditionRepository.UpdateSelection.PersistSelection"] = (1, null, "delegation, unchecked: enclosing callers above own notifications after the transaction"),
            ["EditionRepository.PersistSelection.PrepareSelection"] = (1, null, "delegation, unchecked: enclosing callers above own notifications after the transaction"),
            ["RefreshAuthorService.AddChildren.InsertMany"] = (1, "AuthorRefreshCompleteEvent", "AuthorRefreshCompleteEvent"),
            ["RefreshBookService.RefreshChildren.InsertMany"] = (1, "BookUpdatedEvent", "Explicit/bulk command completion or author refresh completion"),
            ["RefreshEditionService.RefreshEditionInfo.UpdateMany"] = (1, null, "delegation, unchecked: RefreshBookService/RefreshAuthorService completion reaches DeferredCoverDownloadService"),
            ["UpdateEditionSelectionOnMetadataProfileChangeService.UpdateAuthorEditionSelection.UpdateSelection"] = (1, "NotifyBookSelectionsChanged", "Handle finally calls NotifyBookSelectionsChanged once for all authors"),
            ["FixMultipleMonitoredEditions.Repair.PersistSelection"] = (1, "NotifyBookSelectionsChanged", "Clean notifies after transaction commit; migration use precedes startup repair"),
            ["ImportApprovedBooks.SelectImportedEdition.SetMonitored"] = (1, "NotifyBookSelectionsChanged", "Import finally calls NotifyBookSelectionsChanged"),
            ["ImportApprovedBooks.CreateNewBookInstanceForBatch.InsertMany"] = (1, "NotifyBookSelectionsChanged", "New copy; import file events/cover embedding completion"),
            ["BookImportService.SelectImportedEdition.SetMonitored"] = (1, "NotifyBookSelectionsChanged", "NotifyBookSelectionsChanged after the saved batch selection"),
            ["BookUnitDestinationService.CloneBookAndEdition.InsertMany"] = (1, null, "delegation, unchecked: ImportApprovedBooks and BookImportService notify after import selection/file events"),
            ["LibraryIdentityWriter.MergeEdition.Execute"] = (1, null, "delegation, unchecked: RefreshAuthorService, RefreshBookService and BookService publish LibraryIdentityChangedEvent after commit"),
            ["LibraryIdentityWriter.MergeBook.Execute"] = (2, null, "delegation, unchecked: RefreshAuthorService, RefreshBookService and BookService publish LibraryIdentityChangedEvent after commit"),
            ["LibraryIdentityWriter.SelectEdition.Execute"] = (2, null, "delegation, unchecked: RefreshAuthorService, RefreshBookService and BookService publish LibraryIdentityChangedEvent after commit"),
            ["LibraryIdentityWriter.MoveEditions.Execute"] = (1, null, "delegation, unchecked: RefreshAuthorService, RefreshBookService and BookService publish LibraryIdentityChangedEvent after commit")
        };

        [Test]
        public void selection_writers_must_have_an_explained_cover_notification_route()
        {
            var root = RepositorySource.FindRoot();
            var found = new Dictionary<string, int>();
            var symbols = new Dictionary<string, HashSet<string>>();
            foreach (var directory in new[] { "NzbDrone.Core", "Chaptarr.Api.V1" })
            foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "src", directory), "*.cs", SearchOption.AllDirectories)
                .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)))
            {
                var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot();
                foreach (var declaration in tree.DescendantNodes().OfType<ClassDeclarationSyntax>())
                {
                    var owner = declaration.Identifier.ValueText;
                    if (!symbols.TryGetValue(owner, out var names)) symbols[owner] = names = new HashSet<string>();
                    names.UnionWith(declaration.DescendantTokens().Where(token => token.RawKind == (int)SyntaxKind.IdentifierToken).Select(token => token.ValueText));
                }
                // Resolve declared field/local/parameter receivers, rather than relying on one variable name.
                var receivers = tree.DescendantNodes().OfType<VariableDeclarationSyntax>()
                    .Where(node => IsEditionWriter(node.Type.ToString())).SelectMany(node => node.Variables.Select(v => v.Identifier.ValueText))
                    .Concat(tree.DescendantNodes().OfType<ParameterSyntax>().Where(p => IsEditionWriter(p.Type?.ToString())).Select(p => p.Identifier.ValueText))
                    .ToHashSet();
                var bookReceivers = tree.DescendantNodes().OfType<VariableDeclarationSyntax>()
                    .Where(node => IsBookWriter(node.Type.ToString())).SelectMany(node => node.Variables.Select(v => v.Identifier.ValueText))
                    .Concat(tree.DescendantNodes().OfType<ParameterSyntax>().Where(p => IsBookWriter(p.Type?.ToString())).Select(p => p.Identifier.ValueText))
                    .ToHashSet();
                foreach (var call in tree.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var method = call.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                    var owner = call.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText;
                    if (method == null) continue;
                    var member = call.Expression as MemberAccessExpressionSyntax;
                    var name = member?.Name.Identifier.ValueText ?? (call.Expression as IdentifierNameSyntax)?.Identifier.ValueText;
                    var receiver = member?.Expression.ToString();
                    var writer = receiver == "EditionRepository" || receivers.Contains(receiver ?? "") ||
                        (member == null && owner == "EditionRepository");
                    var selection = writer && new[] { "SetMonitored", "UpdateSelection", "PersistSelection", "PrepareSelection", "UpdateMany", "InsertMany", "Update" }.Contains(name);
                    var sql = owner == "LibraryIdentityWriter" && name == "Execute" && call.ArgumentList.Arguments.FirstOrDefault()?.ToString() is string statement &&
                        Regex.IsMatch(statement.Replace("\\\"", "\"").Replace("\"\"", "\""), @"UPDATE\s+""?Editions""?\s+SET\b", RegexOptions.IgnoreCase);
                    var wanted = name == "AddWantedEditions" && bookReceivers.Contains(receiver ?? "");
                    if (!selection && !sql && !wanted) continue;
                    var key = owner + "." + method.Identifier.ValueText + "." + name;
                    found[key] = found.GetValueOrDefault(key) + 1;
                }
            }
            const string limits = "This inventories declared writer APIs and known merge SQL; producer checks are class-wide identifier presence only. It does not prove runtime ordering, arbitrary SQL, aliases or indirect calls. Keep behavioral commit/failure tests.";
            Assert.Multiple(() =>
            {
                Assert.That(found.Keys.Except(Routes.Keys), Is.Empty, "New selection writer needs a reviewed notification route. " + limits);
                Assert.That(Routes.Keys.Except(found.Keys), Is.Empty, "Remove stale routes. " + limits);
                foreach (var route in Routes)
                {
                    Assert.That(found.GetValueOrDefault(route.Key), Is.EqualTo(route.Value.Calls), route.Key + ": writer call count changed. " + limits);
                    if (route.Value.Symbol != null)
                    {
                        Assert.That(symbols.GetValueOrDefault(route.Key.Split('.')[0]), Does.Contain(route.Value.Symbol),
                            route.Key + ": missing producer symbol for " + route.Value.Route + ". " + limits);
                    }
                }
            });
        }

        private static bool IsBookWriter(string type) => type?.Split('.').Last() == "IBookService";

        private static bool IsEditionWriter(string type) => type?.Split('.').Last() is "IEditionService" or "IEditionRepository" or "EditionRepository";
    }
}
