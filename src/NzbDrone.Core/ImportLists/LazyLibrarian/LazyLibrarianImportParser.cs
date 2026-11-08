using System;
using System.Collections.Generic;
using System.Net;
using Newtonsoft.Json;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.ImportLists.Exceptions;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.ImportLists.LazyLibrarianImport
{
    public class LazyLibrarianImportParser : IParseImportListResponse
    {
        private ImportListResponse _importListResponse;
        private readonly Logger _logger = LogManager.GetCurrentClassLogger();

        public IList<ImportListItemInfo> ParseResponse(ImportListResponse importListResponse)
        {
            _importListResponse = importListResponse;

            var items = new List<ImportListItemInfo>();

            if (!PreProcess(_importListResponse))
            {
                return items;
            }

            var jsonResponse = JsonConvert.DeserializeObject<List<LazyLibrarianBook>>(_importListResponse.Content);

            if (jsonResponse == null)
            {
                return items;
            }

            foreach (var item in jsonResponse)
            {
                var identity = GetProviderIdentity(item);
                if (identity.ProviderId == null)
                {
                    _logger.Warn("Skipping LazyLibrarian book '{0}' by '{1}': BookID and BookLink do not establish a supported provider identity.",
                        item?.BookName, item?.AuthorName);
                    continue;
                }

                // Provider-selected records establish identity without choosing a narration.
                items.Add(new ImportListItemInfo
                {
                    Author = item.AuthorName,
                    Book = item.BookName,
                    BookProviderId = identity.IsEdition ? null : identity.ProviderId,
                    EditionProviderId = identity.IsEdition ? identity.ProviderId : null,
                    IsGenericBookRequest = true
                });
            }

            return items;
        }

        private static (string ProviderId, bool IsEdition) GetProviderIdentity(LazyLibrarianBook item)
        {
            var id = item?.BookId?.Trim();
            if (id.IsNullOrWhiteSpace())
            {
                return default;
            }

            var isEdition = false;
            string prefix;
            if (id.Contains(":"))
            {
                isEdition = BookEditionIdentity.IsExplicitEditionProviderId(id);
                var canonical = BookEditionIdentity.NormalizeEditionLookupId(id);

                if (!ProviderIdHelper.TryNormalize(canonical, null, out var normalized))
                {
                    return default;
                }

                prefix = normalized.Substring(0, normalized.IndexOf(':'));
                id = ProviderIdHelper.StripPrefix(normalized);
            }
            else
            {
                Uri.TryCreate(item.BookLink, UriKind.Absolute, out var link);
                prefix = link?.Scheme is "http" or "https" ? link.Host.ToLowerInvariant() switch
                {
                    "goodreads.com" or "www.goodreads.com" => "gr",
                    "hardcover.app" or "www.hardcover.app" => "hc",
                    "openlibrary.org" or "www.openlibrary.org" => "ol",
                    "books.google.com" => "gb",
                    _ => null
                } : null;
                if (prefix == null && (item.BookLink?.StartsWith("/works/OL", StringComparison.OrdinalIgnoreCase) == true ||
                                       item.BookLink?.StartsWith("/books/OL", StringComparison.OrdinalIgnoreCase) == true))
                {
                    prefix = "ol";
                }

                isEdition = prefix == "gr";
            }

            if (prefix is not ("hc" or "gr" or "ol" or "gb") || ProviderIdHelper.ContainsUnsafeProviderAliasValue(id))
            {
                return default;
            }

            isEdition |= prefix == "gb" || (prefix == "ol" && !id.EndsWith("W", StringComparison.OrdinalIgnoreCase));
            var providerId = ProviderIdHelper.WithPrefix(prefix, id);
            return (isEdition && (prefix is "hc" or "gr") ? $"{prefix}-ed:{id}" : providerId, isEdition);
        }

        protected virtual bool PreProcess(ImportListResponse importListResponse)
        {
            if (importListResponse.HttpResponse.StatusCode != HttpStatusCode.OK)
            {
                throw new ImportListException(importListResponse, "Import List API call resulted in an unexpected StatusCode [{0}]", importListResponse.HttpResponse.StatusCode);
            }

            if (importListResponse.HttpResponse.Headers.ContentType != null && importListResponse.HttpResponse.Headers.ContentType.Contains("text/json") &&
                importListResponse.HttpRequest.Headers.Accept != null && !importListResponse.HttpRequest.Headers.Accept.Contains("text/json"))
            {
                throw new ImportListException(importListResponse, "Import List responded with html content. Site is likely blocked or unavailable.");
            }

            return true;
        }
    }
}
