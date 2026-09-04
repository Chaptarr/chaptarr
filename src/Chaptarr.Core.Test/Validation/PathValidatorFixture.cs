using Chaptarr.Http.REST;
using FluentValidation;
using NUnit.Framework;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;

namespace Chaptarr.Core.Test.Validation
{
    [TestFixture]
    public class PathValidatorFixture
    {
        [Test]
        public void path_validator_should_replace_the_path_placeholder_for_null()
        {
            var validator = new ResourceValidator<PathResource>();
            validator.RuleFor(resource => resource.Path).IsValidPath();

            var result = validator.Validate(new PathResource());

            Assert.That(result.Errors[0].ErrorMessage, Is.EqualTo("Invalid Path: ''"));
        }

        [Test]
        public void folder_validator_should_replace_the_path_placeholder_for_null()
        {
            var validator = new ResourceValidator<PathResource>();
            validator.RuleFor(resource => resource.Path).SetValidator(new FolderValidator());

            var result = validator.Validate(new PathResource());

            Assert.That(result.Errors[0].ErrorMessage, Is.EqualTo("Invalid Path: ''"));
        }

        private sealed class PathResource
        {
            public string Path { get; set; }
        }
    }
}
