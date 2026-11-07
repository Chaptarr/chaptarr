using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Chaptarr.Http.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using NzbDrone.Common.Serializer;

namespace Chaptarr.Api.V1.Indexers
{
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class BookshelfReleaseCompatibilityAttribute : ResultFilterAttribute
    {
        // BookshelfNG .76 ReleaseResource, including its optional grab request fields.
        private static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
        {
            "id", "guid", "quality", "qualityWeight", "age", "ageHours", "ageMinutes", "size", "indexerId", "indexer",
            "releaseGroup", "subGroup", "releaseHash", "title", "discography", "sceneSource", "airDate", "authorName", "bookTitle",
            "approved", "temporarilyRejected", "rejected", "rejections", "publishDate", "commentUrl", "downloadUrl", "infoUrl",
            "downloadAllowed", "releaseWeight", "customFormats", "customFormatScore", "magnetUrl", "infoHash", "seeders", "leechers",
            "protocol", "indexerFlags", "authorId", "bookId", "seriesId", "downloadClientId", "downloadClient", "adoptExistingTorrent"
        };

        public override void OnResultExecuting(ResultExecutingContext context)
        {
            if (context.HttpContext.GetReadarrFacadeContext()?.IsBookshelfCompatibility != true || context.Result is not ObjectResult result)
            {
                return;
            }

            var options = STJson.GetSerializerSettings();
            var searchResult = HttpMethods.IsGet(context.HttpContext.Request.Method);
            JsonNode projected = result.Value switch
            {
                ReleaseResource release => Project(release, options, searchResult),
                IEnumerable<ReleaseResource> releases => new JsonArray(releases.Select(release => (JsonNode)Project(release, options, searchResult)).ToArray()),
                _ => null
            };
            if (projected != null)
            {
                result.Value = projected;
                result.DeclaredType = typeof(JsonNode);
            }
        }

        private static JsonObject Project(ReleaseResource release, JsonSerializerOptions options, bool searchResult)
        {
            var value = JsonSerializer.SerializeToNode(release, options).AsObject();
            foreach (var name in value.Select(property => property.Key).Where(name => !Fields.Contains(name)).ToList())
            {
                value.Remove(name);
            }

            if (searchResult)
            {
                // The predecessor mapper leaves these request-only identities unset.
                value.Remove("authorId");
                value.Remove("bookId");
                value.Remove("seriesId");
                if (release.HasOriginalInfoHash)
                {
                    if (release.OriginalInfoHash == null)
                    {
                        value.Remove("infoHash");
                    }
                    else
                    {
                        value["infoHash"] = release.OriginalInfoHash;
                    }
                }
                if (value["releaseHash"]?.GetValue<string>() == string.Empty)
                {
                    value.Remove("releaseHash");
                }
            }

            if (value["quality"]?["quality"] is JsonObject quality)
            {
                quality.Remove("isConversionTarget");
            }

            return value;
        }
    }
}
