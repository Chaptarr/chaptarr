using System;
using System.Collections.Generic;
using System.Reflection;
using Chaptarr.Api.V1.ImportLists;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NUnit.Framework;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Validation;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class ImportListControllerMinRefreshIntervalFixture
    {
        private class ThrowingProxy<T> : DispatchProxy where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"Test proxy does not implement {typeof(T).Name}.{targetMethod?.Name}");
            }
        }

        private sealed class StubImportListFactory : IImportListFactory
        {
            public ImportListDefinition Existing { get; set; }
            public TimeSpan ProviderMinRefreshInterval { get; set; } = TimeSpan.FromHours(12);

            public List<ImportListDefinition> All() => throw new NotImplementedException();
            public List<IImportList> GetAvailableProviders() => throw new NotImplementedException();
            public bool Exists(int id) => throw new NotImplementedException();
            public ImportListDefinition Find(int id) => Existing != null && Existing.Id == id ? Existing : null;
            public ImportListDefinition Get(int id) => Existing != null && Existing.Id == id ? Existing : throw new NotImplementedException();
            public IEnumerable<ImportListDefinition> Get(IEnumerable<int> ids) => throw new NotImplementedException();
            public ImportListDefinition Create(ImportListDefinition definition) => throw new NotImplementedException();
            public void Update(ImportListDefinition definition) => throw new NotImplementedException();
            public IEnumerable<ImportListDefinition> Update(IEnumerable<ImportListDefinition> definitions) => throw new NotImplementedException();
            public void Delete(int id) => throw new NotImplementedException();
            public void Delete(IEnumerable<int> ids) => throw new NotImplementedException();
            public IEnumerable<ImportListDefinition> GetDefaultDefinitions() => throw new NotImplementedException();
            public IEnumerable<ImportListDefinition> GetPresetDefinitions(ImportListDefinition providerDefinition) => throw new NotImplementedException();
            public void SetProviderCharacteristics(ImportListDefinition definition) => definition.MinRefreshInterval = ProviderMinRefreshInterval;
            public void SetProviderCharacteristics(IImportList provider, ImportListDefinition definition) => throw new NotImplementedException();
            public IImportList GetInstance(ImportListDefinition definition) => throw new NotImplementedException();
            public FluentValidation.Results.ValidationResult Test(ImportListDefinition definition) => throw new NotImplementedException();
            public object RequestAction(ImportListDefinition definition, string action, IDictionary<string, string> query) => throw new NotImplementedException();
            public List<ImportListDefinition> AllForTag(int tagId) => throw new NotImplementedException();
            public List<IImportList> AutomaticAddEnabled(bool filterBlockedImportLists = true) => throw new NotImplementedException();
        }

        private static ImportListController BuildController(StubImportListFactory factory)
        {
            var controller = new ImportListController(
                factory,
                new QualityProfileExistsValidator(DispatchProxy.Create<IQualityProfileService, ThrowingProxy<IQualityProfileService>>()),
                new MetadataProfileExistsValidator(DispatchProxy.Create<IMetadataProfileService, ThrowingProxy<IMetadataProfileService>>()));

            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            };
            controller.Request.Method = "PUT";

            return controller;
        }

        // ValidateResource is the same protected method OnActionExecuting calls on every real PUT
        // (Chaptarr.Http/REST/RestController.cs). Invoking it directly here, with skipSharedValidate:true,
        // isolates the PutValidator rule under test from the unrelated SharedValidator rules (name
        // uniqueness, config contract, etc.) that would otherwise require a much heavier fixture.
        private static void Validate(ImportListController controller, ImportListResource resource)
        {
            var method = typeof(ImportListController).GetMethod("ValidateResource", BindingFlags.NonPublic | BindingFlags.Instance);
            try
            {
                method.Invoke(controller, new object[] { resource, false, true });
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }
        }

        [Test]
        public void should_reject_put_that_changes_min_refresh_interval()
        {
            var factory = new StubImportListFactory
            {
                Existing = new ImportListDefinition { Id = 7, MinRefreshInterval = TimeSpan.FromHours(12) },
                ProviderMinRefreshInterval = TimeSpan.FromHours(12)
            };
            var controller = BuildController(factory);

            var resource = new ImportListResource { Id = 7, MinRefreshInterval = TimeSpan.FromMinutes(5) };

            var ex = Assert.Throws<ValidationException>(() => Validate(controller, resource));

            Assert.That(ex.Message, Does.Contain("minRefreshInterval is fixed by the list type"));
            Assert.That(ex.Message, Does.Contain("12:00:00"));
        }

        [Test]
        public void should_allow_put_that_leaves_min_refresh_interval_unchanged()
        {
            var factory = new StubImportListFactory
            {
                Existing = new ImportListDefinition { Id = 7, MinRefreshInterval = TimeSpan.FromHours(12) },
                ProviderMinRefreshInterval = TimeSpan.FromHours(12)
            };
            var controller = BuildController(factory);

            var resource = new ImportListResource { Id = 7, MinRefreshInterval = TimeSpan.FromHours(12) };

            Assert.DoesNotThrow(() => Validate(controller, resource));
        }

        [Test]
        public void should_allow_put_that_omits_min_refresh_interval()
        {
            // A non-nullable TimeSpan field that's absent from the JSON body deserializes to
            // TimeSpan.Zero, indistinguishable from a client explicitly sending "00:00:00". Since no
            // provider ever uses zero, this can't mask a real attempt to change the value, and it stops
            // clients that only send the fields they mean to change from getting a spurious 400.
            var factory = new StubImportListFactory
            {
                Existing = new ImportListDefinition { Id = 7, MinRefreshInterval = TimeSpan.FromHours(12) },
                ProviderMinRefreshInterval = TimeSpan.FromHours(12)
            };
            var controller = BuildController(factory);

            var resource = new ImportListResource { Id = 7, MinRefreshInterval = TimeSpan.Zero };

            Assert.DoesNotThrow(() => Validate(controller, resource));
        }

        [Test]
        public void should_defer_to_the_normal_not_found_handling_for_an_unknown_id()
        {
            var factory = new StubImportListFactory { Existing = null };
            var controller = BuildController(factory);

            var resource = new ImportListResource { Id = 999, MinRefreshInterval = TimeSpan.FromMinutes(5) };

            Assert.DoesNotThrow(() => Validate(controller, resource));
        }
    }
}
