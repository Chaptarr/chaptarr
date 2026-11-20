using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NUnit.Framework;

namespace Chaptarr.Core.Test.Common
{
    // A literal existing elsewhere passes; this cannot prove it belongs to the exercised path.
    // Covers literal Not/None substring constraints only, not Is.False, StringAssert, variables or interpolation.
    [TestFixture]
    public class NegativeAssertionLiteralFixture
    {
        [Test]
        public void negative_message_literals_must_exist_in_production_or_explain_their_absence()
        {
            var root = RepositorySource.FindRoot();
            var tests = Path.Combine(root, "src", "Chaptarr.Core.Test");
            var scope = Sources(tests).Where(path => Path.GetFileName(path).StartsWith("Retag", StringComparison.Ordinal) ||
                    Path.GetFileName(path).StartsWith("Calibre", StringComparison.Ordinal) ||
                    Path.GetFileName(path).StartsWith("AudioTag", StringComparison.Ordinal))
                .OrderBy(path => path).ToList();
            Assert.That(scope, Is.Not.Empty);
            var messages = new HashSet<string>(StringComparer.Ordinal);
            foreach (var project in Directory.EnumerateDirectories(Path.Combine(root, "src"))
                .Where(path => !Path.GetFileName(path).Contains("Test", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var path in Sources(project))
                {
                    var syntax = CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot();
                    foreach (var literal in syntax.DescendantNodes().OfType<LiteralExpressionSyntax>().Where(node => node.IsKind(SyntaxKind.StringLiteralExpression)))
                        messages.Add(literal.Token.ValueText);
                    foreach (var text in syntax.DescendantNodes().OfType<InterpolatedStringTextSyntax>())
                        messages.Add(text.TextToken.ValueText);
                }
            }

            using var translations = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "src", "NzbDrone.Core", "Localization", "Core", "en.json")));
            foreach (var entry in translations.RootElement.EnumerateObject()) messages.Add(entry.Value.GetString());
            var failures = new List<string>();
            var checkedCount = 0;
            foreach (var path in scope)
            {
                Assert.That(File.Exists(path), Is.True, path);
                var source = File.ReadAllText(path);
                var tree = CSharpSyntaxTree.ParseText(source);
                foreach (var call in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (call.Expression is not MemberAccessExpressionSyntax member ||
                        member.Name.Identifier.ValueText is not ("Contain" or "Contains" or "StartWith" or "StartsWith" or "EndWith" or "EndsWith") ||
                        call.ArgumentList.Arguments.FirstOrDefault()?.Expression is not LiteralExpressionSyntax literal ||
                        !literal.IsKind(SyntaxKind.StringLiteralExpression) || !IsNegative(member.Expression)) continue;
                    checkedCount++;
                    var value = literal.Token.ValueText;
                    if (messages.Any(message => message.Contains(value, StringComparison.Ordinal))) continue;
                    var line = tree.GetText().Lines.GetLineFromPosition(literal.SpanStart);
                    // The exception must be a real comment on this assertion's line, not text in a string.
                    var explanation = tree.GetRoot().DescendantTrivia()
                        .Where(trivia => trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) && line.Span.Contains(trivia.SpanStart))
                        .Select(trivia => trivia.ToString()).FirstOrDefault();
                    const string marker = "// absent-literal:";
                    if (explanation?.StartsWith(marker, StringComparison.Ordinal) == true &&
                        !string.IsNullOrWhiteSpace(explanation[marker.Length..])) continue;
                    failures.Add($"{Path.GetRelativePath(tests, path)}:{line.LineNumber + 1}: {value}");
                }
            }

            TestContext.WriteLine($"Checked {checkedCount} negative literal assertions in {scope.Count} source files against {messages.Count} production strings.");
            Assert.That(checkedCount, Is.GreaterThan(0));
            Assert.That(failures, Is.Empty, string.Join(Environment.NewLine, failures));
        }

        private static bool IsNegative(ExpressionSyntax expression)
        {
            while (expression is MemberAccessExpressionSyntax member)
            {
                if (member.Name.Identifier.ValueText is "Not" or "None") return true;
                expression = member.Expression;
            }

            return false;
        }

        private static IEnumerable<string> Sources(string directory)
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.cs")) yield return file;
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if (Path.GetFileName(child) is "bin" or "obj" or "TestResults") continue;
                foreach (var file in Sources(child)) yield return file;
            }
        }
    }
}
