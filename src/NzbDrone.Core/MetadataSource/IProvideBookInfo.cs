using System;
using System.Collections.Generic;
using NzbDrone.Core.Books;

namespace NzbDrone.Core.MetadataSource
{
    public interface IProvideBookInfo
    {
        Tuple<string, Book, List<Author>> GetBookInfo(string id, BookMediaType mediaType = BookMediaType.Audiobook, string authorHintProviderId = null);
        Tuple<string, Book, List<Author>> GetWorkInfo(string id, BookMediaType mediaType = BookMediaType.Audiobook, string authorHintProviderId = null);
        Tuple<string, Book, List<Author>> GetEditionInfo(string id, BookMediaType mediaType = BookMediaType.Audiobook);
    }
}
