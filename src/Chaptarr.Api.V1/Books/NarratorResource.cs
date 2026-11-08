using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.MediaFiles;

namespace Chaptarr.Api.V1.Books
{
    public class NarratorResource
    {
        public string Name { get; set; }
        public bool IsCastLabel => AudioProductionDetector.IsCastLabel(Name);

        public static List<NarratorResource> FromNames(IEnumerable<string> names, string fallback = null)
        {
            var result = (names ?? Enumerable.Empty<string>())
                .Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).Select(name => new NarratorResource { Name = name }).ToList();
            if (result.Count == 0 && !string.IsNullOrWhiteSpace(fallback))
            {
                result.Add(new NarratorResource { Name = fallback.Trim() });
            }

            return result;
        }
    }
}
