using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.DecisionEngine
{
    [TestFixture]
    public class DownloadDecisionComparerTitleMatchFixture
    {
        [Test]
        public void should_prefer_monitored_edition_exact_match_before_custom_format_score()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var comparer = new DownloadDecisionComparer(null, null, null, logger);
            var author = new Author { Name = "J.K. Rowling" };

            var monitoredDecision = new DownloadDecision(new RemoteBook
            {
                Author = author,
                Release = new ReleaseInfo { Title = "Harry Potter and the Sorcerer's Stone", Size = 0 },
                ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel { Quality = Quality.M4B } },
                SearchCriteriaMatch = new TitleMatchResult
                {
                    IsMatch = true,
                    PrimaryTitle = "Harry Potter and the Sorcerer's Stone",
                    MatchedVariant = "Harry Potter and the Sorcerer's Stone"
                }
            });

            var siblingVipDecision = new DownloadDecision(new RemoteBook
            {
                Author = author,
                Release = new ReleaseInfo { Title = "Harry Potter and the Philosopher's Stone", Size = 0 },
                ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel { Quality = Quality.M4B } },
                SearchCriteriaMatch = new TitleMatchResult
                {
                    IsMatch = true,
                    PrimaryTitle = "Harry Potter and the Sorcerer's Stone",
                    MatchedVariant = "Harry Potter and the Philosopher's Stone"
                },
                CustomFormatScore = 1000
            });

            Assert.That(comparer.Compare(monitoredDecision, siblingVipDecision), Is.GreaterThan(0));
            Assert.That(comparer.Compare(siblingVipDecision, monitoredDecision), Is.LessThan(0));
        }
    }
}
