using Chaptarr.Http.REST;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Api.V1.RootFolders
{
    public class AuthorPathUpdateResource : RestResource
    {
        public int AuthorId { get; set; }
        public string OldPath { get; set; }
        public string NewPath { get; set; }
        public bool HasExistingFiles { get; set; }
        public int FileCount { get; set; }
    }

    public static class AuthorPathUpdateResourceMapper
    {
        public static AuthorPathUpdateResource ToResource(this AuthorPathUpdate model)
        {
            if (model == null)
            {
                return null;
            }

            return new AuthorPathUpdateResource
            {
                AuthorId = model.AuthorId,
                OldPath = model.OldPath,
                NewPath = model.NewPath,
                HasExistingFiles = model.HasExistingFiles,
                FileCount = model.FileCount
            };
        }
    }
}
