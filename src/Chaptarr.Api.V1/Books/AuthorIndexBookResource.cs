using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Http.Middleware;
using Chaptarr.Http.REST;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Books;
using CoreAuthor = NzbDrone.Core.Books.Author;

namespace Chaptarr.Api.V1.Books
{
    /// <summary>
    /// BookshelfNG exposed author metadata as a lazy relation from its core Book model.
    /// ChaptarrNG stores these fields directly on Author, but retains this contract type
    /// so generated clients continue to see the legacy relation schema.
    /// </summary>
    public class AuthorMetadata
    {
        public int Id { get; set; }
        public string ForeignAuthorId { get; set; }
        public string TitleSlug { get; set; }
        public string Name { get; set; }
        public string SortName { get; set; }
        public string NameLastFirst { get; set; }
        public string SortNameLastFirst { get; set; }
        public List<string> Aliases { get; set; }
        public string Overview { get; set; }
        public string Disambiguation { get; set; }
        public string Gender { get; set; }
        public string Hometown { get; set; }
        public DateTime? Born { get; set; }
        public DateTime? Died { get; set; }
        public AuthorStatusType Status { get; set; }
        public List<MediaCover> Images { get; set; }
        public List<Links> Links { get; set; }
        public List<string> Genres { get; set; }
        public Ratings Ratings { get; set; }
    }

    /// <summary>
    /// BookshelfNG exposed its core Book entity in an AuthorResource's nextBook and
    /// lastBook fields. Keep those legacy fields and leave unloaded relations null,
    /// as the original lazy-loaded entity serializer did.
    /// </summary>
    public class AuthorIndexBookResource : RestResource
    {
        public int AuthorMetadataId { get; set; }
        public string ForeignBookId { get; set; }
        public string ForeignEditionId { get; set; }
        public string TitleSlug { get; set; }
        public string Title { get; set; }
        public DateTime? ReleaseDate { get; set; }
        public List<Links> Links { get; set; }
        public List<string> Genres { get; set; }
        public List<int> RelatedBooks { get; set; }
        public Ratings Ratings { get; set; }
        public DateTime? LastSearchTime { get; set; }
        public string CleanTitle { get; set; }
        public bool Monitored { get; set; }
        public bool AnyEditionOk { get; set; }
        public DateTime? LastInfoSync { get; set; }
        public DateTime Added { get; set; }
        public AddBookOptions AddOptions { get; set; }

        // Keep the legacy lazy-loaded relation contracts. These remain null in the
        // author index response, matching BookshelfNG's unloaded-navigation behavior.
        [global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
        public LazyLoaded<AuthorMetadata> AuthorMetadata { get; set; }
        [global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
        public LazyLoaded<CoreAuthor> Author { get; set; }
        [global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
        public LazyLoaded<List<Edition>> Editions { get; set; }
        [global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
        public LazyLoaded<List<BookFile>> BookFiles { get; set; }
        [global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
        public LazyLoaded<List<SeriesBookLink>> SeriesLinks { get; set; }
    }

    public static class AuthorIndexBookResourceMapper
    {
        public static AuthorIndexBookResource ToAuthorIndexResource(
            this Book model,
            int authorMetadataId,
            ReadarrFacadeContext facadeContext = null)
        {
            if (model == null)
            {
                return null;
            }

            return new AuthorIndexBookResource
            {
                Id = model.Id,
                AuthorMetadataId = authorMetadataId,
                ForeignBookId = BookResourceMapper.BuildForeignBookId(model, facadeContext),
                ForeignEditionId = model.ForeignEditionId,
                TitleSlug = model.TitleSlug,
                Title = model.Title,
                ReleaseDate = model.ReleaseDate,
                Links = model.Links?.ToList() ?? new List<Links>(),
                Genres = model.Genres?.ToList() ?? new List<string>(),
                RelatedBooks = model.RelatedBooks?.ToList() ?? new List<int>(),
                Ratings = model.Ratings ?? new Ratings(),
                LastSearchTime = model.LastSearchTime,
                CleanTitle = model.CleanTitle,
                Monitored = model.IsMonitored(),
                AnyEditionOk = model.AnyEditionOk,
                LastInfoSync = model.LastInfoSync,
                Added = model.Added,
                AddOptions = model.AddOptions
            };
        }
    }
}
