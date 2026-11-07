using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace NzbDrone.Core.MediaFiles.BookImport.Services
{
    /// <summary>
    /// Limits how often the same set of unmapped files is queued for another rescan. A rescan is allowed
    /// immediately when the unmapped set (or the author catalog it was matched against) changes; otherwise
    /// repeats back off from one hour up to a day.
    /// </summary>
    internal sealed class UnmappedRescanBackoff
    {
        private static readonly TimeSpan[] Windows =
        {
            TimeSpan.FromHours(1),
            TimeSpan.FromHours(6),
            TimeSpan.FromHours(24)
        };

        private sealed class Entry
        {
            public string Signature;
            public int Attempts;
            public DateTime NextAllowedUtc;
        }

        private readonly ConcurrentDictionary<string, Entry> _entries = new ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        public static string BuildSignature(IEnumerable<BookFile> unmappedFiles, DateTime? catalogStamp)
        {
            var builder = new StringBuilder();
            builder.Append(catalogStamp?.Ticks ?? 0).Append('\n');
            foreach (var file in unmappedFiles.OrderBy(f => f.Path, StringComparer.Ordinal))
            {
                builder.Append(file.Path).Append('|').Append(file.Size).Append('|').Append(file.Modified.Ticks).Append('\n');
            }

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
        }

        public bool ShouldRescan(string key, string signature, DateTime nowUtc)
        {
            lock (_entries)
            {
                if (!_entries.TryGetValue(key, out var entry) || entry.Signature != signature)
                {
                    _entries[key] = new Entry { Signature = signature, Attempts = 1, NextAllowedUtc = nowUtc + Windows[0] };
                    return true;
                }

                if (nowUtc < entry.NextAllowedUtc)
                {
                    return false;
                }

                entry.Attempts++;
                entry.NextAllowedUtc = nowUtc + Windows[Math.Min(entry.Attempts - 1, Windows.Length - 1)];
                return true;
            }
        }

        public void Clear(string key)
        {
            _entries.TryRemove(key, out _);
        }
    }
}
