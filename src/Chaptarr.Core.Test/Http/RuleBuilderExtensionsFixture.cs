using FluentValidation;
using NUnit.Framework;
using Chaptarr.Http.Validation;

namespace Chaptarr.Core.Test.Http
{
    [TestFixture]
    public class RuleBuilderExtensionsFixture
    {
        private class TestModel
        {
            public string Url { get; set; }
            public int Interval { get; set; }
        }

        private class TestValidator : AbstractValidator<TestModel>
        {
            public TestValidator()
            {
                RuleFor(x => x.Url).HaveHttpProtocol();
                RuleFor(x => x.Interval).IsValidRssSyncInterval();
            }
        }

        private static readonly TestValidator Validator = new();

        [TestCase("http://example.com", true)]
        [TestCase("https://example.com", true)]
        [TestCase("ftp://example.com", false)]
        [TestCase("example.com", false)]
        public void have_http_protocol_should_match_core_behavior(string url, bool isValid)
        {
            var result = Validator.Validate(new TestModel { Url = url, Interval = 10 });

            Assert.That(result.IsValid, Is.EqualTo(isValid));
        }

        [TestCase(0, true)]
        [TestCase(10, true)]
        [TestCase(120, true)]
        [TestCase(9, false)]
        [TestCase(121, false)]
        public void rss_sync_interval_should_validate_expected_bounds(int interval, bool isValid)
        {
            var result = Validator.Validate(new TestModel { Url = "https://example.com", Interval = interval });

            Assert.That(result.IsValid, Is.EqualTo(isValid));
        }
    }
}
