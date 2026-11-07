using System;
using System.Linq;
using System.Reflection;
using Chaptarr.Api.V1.Author;
using Chaptarr.Api.V1.Books;
using Chaptarr.Api.V1.Bookshelf;
using Chaptarr.Api.V1.ManualImport;
using Microsoft.AspNetCore.Mvc;
using NUnit.Framework;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class BookshelfJsonContentTypeParityFixture
    {
        [TestCase(typeof(AuthorEditorController), nameof(AuthorEditorController.SaveAll))]
        [TestCase(typeof(AuthorEditorController), nameof(AuthorEditorController.DeleteAuthor))]
        [TestCase(typeof(BookshelfController), nameof(BookshelfController.UpdateAll))]
        [TestCase(typeof(BookEditorController), nameof(BookEditorController.SaveAll))]
        [TestCase(typeof(BookEditorController), nameof(BookEditorController.DeleteBook))]
        [TestCase(typeof(ManualImportController), nameof(ManualImportController.UpdateItems))]
        public void bookshelf_state_changing_actions_should_require_json(Type controllerType, string actionName)
        {
            var action = controllerType.GetMethod(actionName, BindingFlags.Instance | BindingFlags.Public);
            Assert.That(action, Is.Not.Null, $"{controllerType.Name}.{actionName} should exist");

            var contentTypes = action.GetCustomAttributes<ConsumesAttribute>(inherit: true)
                .SelectMany(attribute => attribute.ContentTypes);

            Assert.That(contentTypes, Does.Contain("application/json"), $"{controllerType.Name}.{actionName}");
        }
    }
}
