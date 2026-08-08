using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Indexers.Newznab;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture]
    public class NewznabCategoryFieldOptionsConverterFixture
    {
        [Test]
        public void should_hide_categories_that_do_not_make_sense_for_books_app()
        {
            var categories = new List<NewznabCategory>
            {
                new NewznabCategory { Id = 1000, Name = "Console" },
                new NewznabCategory { Id = 2000, Name = "Movies" },
                new NewznabCategory { Id = 3000, Name = "Audio" },
                new NewznabCategory { Id = 4000, Name = "PC" },
                new NewznabCategory { Id = 5000, Name = "TV" },
                new NewznabCategory { Id = 6000, Name = "XXX" },
                new NewznabCategory { Id = 7000, Name = "Books" }
            };

            var options = NewznabCategoryFieldOptionsConverter.GetFieldSelectOptions(categories);

            Assert.That(options.Select(o => o.Value), Is.EqualTo(new[] { 3000, 7000 }));
        }
    }
}
