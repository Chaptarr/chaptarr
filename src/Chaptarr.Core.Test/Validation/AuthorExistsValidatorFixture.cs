using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Chaptarr.Api.V1.Author;
using FluentValidation;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Validation.Paths;

namespace Chaptarr.Core.Test.Validation
{
    [TestFixture]
    public class AuthorExistsValidatorFixture
    {
        private class AuthorServiceProxy : DispatchProxy
        {
            public readonly List<(string Provider, string ProviderId)> Calls = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(IAuthorService.FindByProviderId))
                {
                    var provider = (string)args[0];
                    var providerId = (string)args[1];
                    Calls.Add((provider, providerId));

                    return provider == "gr" && providerId == "gr:173491"
                        ? new Author { Id = 77, GoodreadsAuthorId = "gr:173491" }
                        : null;
                }

                throw new NotImplementedException(targetMethod.Name);
            }
        }

        [Test]
        public void should_not_guess_provider_for_bare_numeric_author_id()
        {
            var proxy = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            var handler = (AuthorServiceProxy)(object)proxy;
            var validator = BuildValidator(proxy);

            var result = validator.Validate(new AuthorResource { ForeignAuthorId = "173491" });

            Assert.That(result.IsValid, Is.True);
            Assert.That(handler.Calls, Is.Empty);
        }

        [Test]
        public void should_still_reject_existing_prefixed_author_id()
        {
            var proxy = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            var handler = (AuthorServiceProxy)(object)proxy;
            var validator = BuildValidator(proxy);

            var result = validator.Validate(new AuthorResource { ForeignAuthorId = "gr:173491" });

            Assert.That(result.IsValid, Is.False);
            Assert.That(handler.Calls.Single(), Is.EqualTo(("gr", "gr:173491")));
        }

        private static InlineValidator<AuthorResource> BuildValidator(IAuthorService authorService)
        {
            var validator = new InlineValidator<AuthorResource>();
            validator.RuleFor(x => x.ForeignAuthorId).SetValidator(new AuthorExistsValidator(authorService));
            return validator;
        }
    }
}
