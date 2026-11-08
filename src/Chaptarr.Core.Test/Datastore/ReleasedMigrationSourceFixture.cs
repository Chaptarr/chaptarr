using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;

namespace Chaptarr.Core.Test.Datastore
{
    public class ReleasedMigrationSourceFixture
    {
        // At each public release, advance this boundary and append hashes from that release.
        private const int ReleasedThrough = 128;
        private const string ShippedMessage = "This migration has shipped. Never edit it; add a new migration.";
        private const string SourcePrefix = "Migrations.Source.";

        [Test]
        public void publicly_released_migration_sources_must_not_change()
        {
            var assembly = typeof(ReleasedMigrationSourceFixture).Assembly;
            byte[] Read(string name)
            {
                using var stream = assembly.GetManifestResourceStream(name);
                Assert.That(stream, Is.Not.Null, $"Missing migration gate resource: {name}");
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                return buffer.ToArray();
            }

            var expected = Encoding.UTF8.GetString(Read("Migrations.Released.sha256"))
                .Split('\n').Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'))
                .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .ToDictionary(parts => parts[1], parts => parts[0], StringComparer.Ordinal);
            var sources = assembly.GetManifestResourceNames().Where(name => name.StartsWith(SourcePrefix, StringComparison.Ordinal))
                .ToDictionary(name => name.Substring(SourcePrefix.Length), Read, StringComparer.Ordinal);

            Assert.That(expected.Keys.Select(Version).Where(version => version != int.MaxValue).Max(), Is.EqualTo(ReleasedThrough), "Update the release boundary and hash manifest together.");
            Assert.That(Changes(sources, expected), Is.Empty, ShippedMessage);
        }

        private static int Version(string name) => int.TryParse(name.Split('_')[0], out var version) ? version : int.MaxValue;

        // Git normalizes text to LF; a Windows checkout must hash the same source.
        private static string Hash(byte[] source) => Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(source).Replace("\r\n", "\n", StringComparison.Ordinal))));

        private static IEnumerable<string> Changes(Dictionary<string, byte[]> sources, Dictionary<string, string> expected) =>
            expected.Where(pin => !sources.TryGetValue(pin.Key, out var source) || Hash(source) != pin.Value).Select(pin => pin.Key)
                .Concat(sources.Keys.Where(name => Version(name) <= ReleasedThrough && !expected.ContainsKey(name)));

        [TestCase("edit")]
        [TestCase("delete")]
        [TestCase("renumber")]
        [TestCase("backfill")]
        [TestCase("helper-edit")]
        [TestCase("helper-delete")]
        public void changed_released_sources_fail_the_gate(string mutation)
        {
            var source = Encoding.UTF8.GetBytes("original migration\n");
            var sources = new Dictionary<string, byte[]> { ["107_shipped.cs"] = source };
            var expected = new Dictionary<string, string> { ["107_shipped.cs"] = Hash(source) };
            sources["ShippedHelper.cs"] = source;
            expected["ShippedHelper.cs"] = Hash(source);
            if (mutation == "helper-edit") sources["ShippedHelper.cs"] = Encoding.UTF8.GetBytes("changed helper");
            if (mutation == "helper-delete") sources.Remove("ShippedHelper.cs");
            if (mutation == "edit") sources["107_shipped.cs"] = Encoding.UTF8.GetBytes("changed migration\n");
            if (mutation is "delete" or "renumber") sources.Remove("107_shipped.cs");
            if (mutation == "renumber") sources["108_renumbered.cs"] = source;
            if (mutation == "backfill") sources["106_new.cs"] = source;
            Assert.That(Changes(sources, expected), Is.Not.Empty);
        }

        [Test]
        public void prerelease_edits_and_checkout_line_endings_do_not_fail_the_gate()
        {
            var expected = new Dictionary<string, string> { ["107_shipped.cs"] = Hash(Encoding.UTF8.GetBytes("original\n")) };
            var sources = new Dictionary<string, byte[]>
            {
                ["107_shipped.cs"] = Encoding.UTF8.GetBytes("original\r\n"),
                ["129_not_shipped.cs"] = Encoding.UTF8.GetBytes("unpublished changes")
            };
            Assert.That(Changes(sources, expected), Is.Empty);
            sources.Remove("129_not_shipped.cs");
            Assert.That(Changes(sources, expected), Is.Empty);
        }
    }
}
