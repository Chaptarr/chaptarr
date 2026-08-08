using System;
using System.Collections.Generic;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test
{
    public sealed class TestQualityProfileService : IQualityProfileService
    {
        public QualityProfile Add(QualityProfile profile) => throw new NotImplementedException();
        public void Update(QualityProfile profile) => throw new NotImplementedException();
        public void Delete(int id) => throw new NotImplementedException();
        public List<QualityProfile> All() => throw new NotImplementedException();
        public List<QualityProfile> GetByType(ProfileType type) => throw new NotImplementedException();
        public QualityProfile Get(int id) => throw new NotImplementedException();
        public bool Exists(int id) => id > 0;
        public QualityProfile GetDefaultProfile(string name, Quality cutoff = null, params Quality[] allowed) => throw new NotImplementedException();
    }
}
