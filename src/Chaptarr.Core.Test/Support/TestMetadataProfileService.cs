using System;
using System.Collections.Generic;
using NzbDrone.Core.Books;
using NzbDrone.Core.Profiles.Metadata;

namespace Chaptarr.Core.Test
{
    public sealed class TestMetadataProfileService : IMetadataProfileService
    {
        public MetadataProfile Add(MetadataProfile profile) => throw new NotImplementedException();
        public void Update(MetadataProfile profile) => throw new NotImplementedException();
        public void Delete(int id) => throw new NotImplementedException();
        public List<MetadataProfile> All() => throw new NotImplementedException();
        public MetadataProfile Get(int id) => throw new NotImplementedException();
        public bool Exists(int id) => id > 0;
        public List<Book> FilterBooks(Author input, int profileId) => throw new NotImplementedException();
    }
}
