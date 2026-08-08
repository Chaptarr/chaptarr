using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Books;
using NzbDrone.Core.Parser;

namespace NzbDrone.Core.Housekeeping.Housekeepers
{
    public class UpdateCleanTitleForAuthor : IHousekeepingTask
    {
        private readonly IAuthorRepository _authorRepository;

        public UpdateCleanTitleForAuthor(IAuthorRepository authorRepository)
        {
            _authorRepository = authorRepository;
        }

        public void Clean()
        {
            var authors = _authorRepository.All().ToList();

            var authorsToUpdate = new List<Author>();
            authors.ForEach(s =>
            {
                var cleanName = s.Name.CleanAuthorName();
                if (s.CleanName != cleanName)
                {
                    s.CleanName = cleanName;
                    authorsToUpdate.Add(s);
                }
            });

            if (authorsToUpdate.Any())
            {
                _authorRepository.UpdateMany(authorsToUpdate);
            }
        }
    }
}
