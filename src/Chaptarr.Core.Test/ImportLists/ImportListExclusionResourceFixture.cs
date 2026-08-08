using System;
using System.Collections.Generic;
using Chaptarr.Api.V1.ImportLists;
using FluentValidation;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.ImportLists.Exclusions;

namespace Chaptarr.Core.Test.ImportLists
{
    [TestFixture]
    public class ImportListExclusionResourceFixture
    {
        [TestCase("hc:12345", "hc:12345")]
        [TestCase("HC:12345", "hc:12345")]
        [TestCase("gr:98765", "gr:98765")]
        public void provider_ids_should_validate_and_normalize(string foreignId, string expectedForeignId)
        {
            var validator = new TestValidator();
            var resource = new ImportListExclusionResource
            {
                ForeignId = foreignId,
                AuthorName = "Author",
                MediaType = "audiobook"
            };

            var result = validator.Validate(resource);
            var model = resource.ToModel();

            Assert.Multiple(() =>
            {
                Assert.That(result.IsValid, Is.True);
                Assert.That(model.ForeignId, Is.EqualTo(expectedForeignId));
                Assert.That(model.MediaType, Is.EqualTo(BookMediaType.Audiobook));
            });
        }

        [Test]
        public void mapper_should_round_trip_media_type_as_api_string()
        {
            var model = new ImportListExclusion
            {
                Id = 7,
                ForeignId = "gr:98765",
                Name = "Author - Book",
                MediaType = BookMediaType.Ebook
            };

            var resource = model.ToResource();
            var mappedModel = resource.ToModel();

            Assert.Multiple(() =>
            {
                Assert.That(resource.MediaType, Is.EqualTo("ebook"));
                Assert.That(mappedModel.MediaType, Is.EqualTo(BookMediaType.Ebook));
                Assert.That(mappedModel.ForeignId, Is.EqualTo("gr:98765"));
            });
        }

        [TestCase("9780123456789")]
        [TestCase("0123456789")]
        public void isbn_values_should_not_validate_as_provider_ids(string foreignId)
        {
            var validator = new TestValidator();

            var result = validator.Validate(new ImportListExclusionResource
            {
                ForeignId = foreignId,
                AuthorName = "Author"
            });

            Assert.That(result.IsValid, Is.False);
        }

        [Test]
        public void duplicate_validation_should_respect_media_scope()
        {
            var validator = new TestValidator(new ImportListExclusion
            {
                Id = 1,
                ForeignId = "hc:12345",
                Name = "Author - Book",
                MediaType = BookMediaType.Audiobook
            });

            var ebookResult = validator.Validate(new ImportListExclusionResource
            {
                ForeignId = "HC:12345",
                AuthorName = "Author - Book",
                MediaType = "ebook"
            });

            var audiobookResult = validator.Validate(new ImportListExclusionResource
            {
                ForeignId = "hc:12345",
                AuthorName = "Author - Book",
                MediaType = "audiobook"
            });

            var unscopedResult = validator.Validate(new ImportListExclusionResource
            {
                ForeignId = "hc:12345",
                AuthorName = "Author - Book"
            });

            Assert.Multiple(() =>
            {
                Assert.That(ebookResult.IsValid, Is.True);
                Assert.That(audiobookResult.IsValid, Is.False);
                Assert.That(unscopedResult.IsValid, Is.False);
            });
        }

        private sealed class TestValidator : AbstractValidator<ImportListExclusionResource>
        {
            public TestValidator(params ImportListExclusion[] existing)
            {
                var service = new StubImportListExclusionService(existing);
                var existsValidator = new ImportListExclusionExistsValidator(service);

                RuleFor(c => c.ForeignId)
                    .NotEmpty()
                    .SetValidator(new ImportListExclusionProviderIdValidator());
                RuleFor(c => c)
                    .Must(c => !existsValidator.Exists(c.ForeignId, c.Id, ParseMediaType(c.MediaType)));
            }

            private static BookMediaType? ParseMediaType(string mediaType)
            {
                if (string.IsNullOrWhiteSpace(mediaType) || mediaType.Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                return mediaType.Equals("ebook", StringComparison.OrdinalIgnoreCase)
                    ? BookMediaType.Ebook
                    : BookMediaType.Audiobook;
            }
        }

        private sealed class StubImportListExclusionService : IImportListExclusionService
        {
            private readonly List<ImportListExclusion> _existing;

            public StubImportListExclusionService(params ImportListExclusion[] existing)
            {
                _existing = new List<ImportListExclusion>(existing ?? Array.Empty<ImportListExclusion>());
            }

            public ImportListExclusion Add(ImportListExclusion importListExclusion) => throw new NotImplementedException();
            public List<ImportListExclusion> All() => _existing;
            public void Delete(int id) => throw new NotImplementedException();
            public void Delete(List<int> ids) => throw new NotImplementedException();
            public void Delete(string foreignId) => throw new NotImplementedException();
            public ImportListExclusion Get(int id) => throw new NotImplementedException();
            public ImportListExclusion FindByForeignId(string foreignId) => throw new NotImplementedException();
            public List<ImportListExclusion> FindByForeignId(List<string> foreignIds) => throw new NotImplementedException();
            public ImportListExclusion Update(ImportListExclusion importListExclusion) => throw new NotImplementedException();
        }
    }
}
