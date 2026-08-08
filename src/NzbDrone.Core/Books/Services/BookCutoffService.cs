using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.Books
{
    public interface IBookCutoffService
    {
        PagingSpec<Book> BooksWhereCutoffUnmet(PagingSpec<Book> pagingSpec);
        List<QualitiesBelowCutoff> GetQualitiesBelowCutoff() => throw new System.NotImplementedException();
        List<BookSearchTarget> GetCutoffUnmetSearchTargets(List<QualitiesBelowCutoff> qualitiesBelowCutoff, BookMediaType? mediaType, int? authorId) => throw new System.NotImplementedException();
    }

    public class BookCutoffService : IBookCutoffService
    {
        private readonly IBookRepository _bookRepository;
        private readonly IQualityProfileService _qualityProfileService;

        public BookCutoffService(IBookRepository bookRepository, IQualityProfileService qualityProfileService)
        {
            _bookRepository = bookRepository;
            _qualityProfileService = qualityProfileService;
        }

        public PagingSpec<Book> BooksWhereCutoffUnmet(PagingSpec<Book> pagingSpec)
        {
            var qualitiesBelowCutoff = GetQualitiesBelowCutoff();

            if (qualitiesBelowCutoff.Empty())
            {
                pagingSpec.Records = new List<Book>();

                return pagingSpec;
            }

            return _bookRepository.BooksWhereCutoffUnmet(pagingSpec, qualitiesBelowCutoff);
        }

        public List<BookSearchTarget> GetCutoffUnmetSearchTargets(List<QualitiesBelowCutoff> qualitiesBelowCutoff, BookMediaType? mediaType, int? authorId)
        {
            return _bookRepository.GetCutoffUnmetSearchTargets(qualitiesBelowCutoff, mediaType, authorId);
        }

        public List<QualitiesBelowCutoff> GetQualitiesBelowCutoff()
        {
            var qualitiesBelowCutoff = new List<QualitiesBelowCutoff>();
            var profiles = _qualityProfileService.All();

            //Get all items less than the cutoff
            foreach (var profile in profiles)
            {
                var cutoff = profile.UpgradeAllowed ? profile.Cutoff : profile.FirstAllowedQuality().Id;
                var cutoffIndex = profile.GetIndex(cutoff);
                var belowCutoff = profile.Items.Take(cutoffIndex.Index).ToList();

                if (belowCutoff.Any())
                {
                    qualitiesBelowCutoff.Add(new QualitiesBelowCutoff(profile.Id, profile.ProfileType, belowCutoff.SelectMany(i => i.GetQualities().Select(q => q.Id))));
                }
            }

            return qualitiesBelowCutoff;
        }
    }
}
