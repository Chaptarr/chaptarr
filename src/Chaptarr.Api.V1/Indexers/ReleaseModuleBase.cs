using System;
using System.Collections.Generic;
using Chaptarr.Http.REST;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.DecisionEngine;

namespace Chaptarr.Api.V1.Indexers
{
    public abstract class ReleaseControllerBase : RestController<ReleaseResource>
    {
        [NonAction]
        public override ActionResult<ReleaseResource> GetResourceByIdWithErrorHandler(int id)
        {
            return base.GetResourceByIdWithErrorHandler(id);
        }

        protected override ReleaseResource GetResourceById(int id)
        {
            throw new NotImplementedException();
        }

        protected virtual List<ReleaseResource> MapDecisions(IEnumerable<DownloadDecision> decisions)
        {
            var result = new List<ReleaseResource>();

            foreach (var downloadDecision in decisions)
            {
                var release = MapDecision(downloadDecision, result.Count);

                result.Add(release);
            }

            return result;
        }

        protected virtual ReleaseResource MapDecision(DownloadDecision decision, int initialWeight)
        {
            var release = decision.ToResource();

            release.ReleaseWeight = initialWeight;
            release.Rank = initialWeight + 1; // 1-based ranking (1=best, 2=second, etc.)
            release.IsPreferredChoice = initialWeight == 0 && decision.RemoteBook.DownloadAllowed;

            if (decision.RemoteBook.Author != null)
            {
                var qualityProfile = decision.RemoteBook.Author.GetQualityProfileForQuality(release.Quality.Quality);
                if (qualityProfile != null)
                {
                    release.QualityWeight = qualityProfile.GetIndex(release.Quality.Quality).Index * 100;
                }
            }

            release.QualityWeight += release.Quality.Revision.Real * 10;
            release.QualityWeight += release.Quality.Revision.Version;

            return release;
        }
    }
}
