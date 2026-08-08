using System;
using Microsoft.AspNetCore.Http;

namespace Chaptarr.Http.Middleware
{
    public sealed class ReadarrFacadeContext
    {
        public const string ItemKey = "Chaptarr.ReadarrFacadeContext";

        public ReadarrFacadeContext(string dialect, string mediaType, string prefix)
        {
            Dialect = dialect;
            MediaType = mediaType;
            Prefix = prefix;
        }

        public string Dialect { get; }
        public string MediaType { get; }
        public string Prefix { get; }

        public bool IsHardcover => string.Equals(Dialect, "hc", StringComparison.OrdinalIgnoreCase);
        public bool IsGoodreads => string.Equals(Dialect, "gr", StringComparison.OrdinalIgnoreCase);
    }

    public static class ReadarrFacadeHttpContextExtensions
    {
        public static ReadarrFacadeContext GetReadarrFacadeContext(this HttpContext context)
        {
            if (context?.Items == null)
            {
                return null;
            }

            return context.Items.TryGetValue(ReadarrFacadeContext.ItemKey, out var value)
                ? value as ReadarrFacadeContext
                : null;
        }
    }
}
