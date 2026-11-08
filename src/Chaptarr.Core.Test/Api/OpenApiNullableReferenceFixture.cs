using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System;
using Chaptarr.Api.V1.Author;
using Chaptarr.Api.V1.Books;
using Chaptarr.Api.V1.ImportLists;
using Chaptarr.Api.V1.PendingImport;
using Chaptarr.Api.V1.ProviderIds;
using Chaptarr.Api.V1;
using Chaptarr.Core.Test.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.ImportLists;
using NzbDrone.Host;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class OpenApiNullableReferenceFixture
    {
        internal static ServiceProvider CreateServices()
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
            {
                ["dataProtectionFolder"] = Path.GetTempPath()
            }).Build();
            var services = new ServiceCollection();
            services.AddSingleton<IWebHostEnvironment>(new ContractEnvironment());
            services.AddSingleton(InterfaceProxy.Proxy<IConfigFileProvider>((method, _) =>
                throw new InvalidOperationException("Contract generation must not read application settings: " + method.Name)));
            new Startup(configuration).ConfigureServices(services);
            return services.BuildServiceProvider();
        }

        private sealed class ContractEnvironment : IWebHostEnvironment
        {
            public string ApplicationName { get; set; } = typeof(Startup).Assembly.GetName().Name;
            public string EnvironmentName { get; set; } = "Development";
            public string ContentRootPath { get; set; } = TestContext.CurrentContext.TestDirectory;
            public string WebRootPath { get; set; } = TestContext.CurrentContext.TestDirectory;
            public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
            public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        }

        [TestCase(typeof(NzbDrone.Core.Books.Services.LibrarySearchAuthor), "exactMatch")]
        [TestCase(typeof(NzbDrone.Core.Books.Services.LibrarySearchBook), "exactMatch")]
        [TestCase(typeof(NzbDrone.Core.Books.Services.LibrarySearchBook), "matchedEditionTitle")]
        public void header_search_fields_match_the_generated_snapshot(Type type, string name)
        {
            using var services = CreateServices();
            var repository = new SchemaRepository();
            services.GetRequiredService<ISchemaGenerator>().GenerateSchema(type, repository);
            var schema = repository.Schemas[type.Name];
            Assert.That(schema.Properties.ContainsKey(name), Is.True);
            var snapshot = JObject.Parse(File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "openapi.json")));
            Assert.That(snapshot["components"]["schemas"][type.Name]["properties"][name]["type"].Value<string>(), Is.EqualTo(schema.Properties[name].Type));
        }

        [Test]
        public void settings_backup_preview_schema_matches_published_contract()
        {
            using var services = CreateServices();
            var document = services.GetRequiredService<Swashbuckle.AspNetCore.Swagger.ISwaggerProvider>().GetSwagger("v1");
            using var text = new StringWriter();
            document.SerializeAsV3(new Microsoft.OpenApi.Writers.OpenApiJsonWriter(text));
            var output = Path.Combine(TestContext.CurrentContext.WorkDirectory, "restore-openapi.generated.json");
            File.WriteAllText(output, text.ToString());
            TestContext.AddTestAttachment(output);
            var current = JObject.Parse(text.ToString());
            var snapshot = JObject.Parse(File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "openapi.json")));
            foreach (var name in new[] { "SettingsBackupRestoreRequest", "SettingsBackupRestoreResult" })
                Assert.That(JToken.DeepEquals(snapshot["components"]["schemas"][name], current["components"]["schemas"][name]), Is.True,
                    "Regenerate the API snapshot; the settings-restore contract changed: " + name);
            Assert.That(current["components"]["schemas"]["SettingsBackupRestoreRequest"]["properties"]["preview"]["type"].Value<string>(), Is.EqualTo("boolean"));
            Assert.That(current["components"]["schemas"]["SettingsBackupRestoreResult"]["properties"]["wouldRemove"]["type"].Value<string>(), Is.EqualTo("array"));
            Assert.That(current["components"]["schemas"]["SettingsBackupRestoreResult"]["properties"]["skippedRemotePathMappings"], Is.Null);
        }

        [TestCase(typeof(BookController), nameof(BookController.AddBook), "book", 409, typeof(ProviderAmbiguityResource))]
        [TestCase(typeof(BookController), nameof(BookController.AddBook), "book", 422, typeof(ApiErrorResource))]
        [TestCase(typeof(AuthorController), nameof(AuthorController.AddAuthor), "author", 409, typeof(ProviderAmbiguityResource))]
        [TestCase(typeof(AuthorController), nameof(AuthorController.AddAuthor), "author", 422, typeof(ApiErrorResource))]
        public void add_response_contract_should_match_the_generated_snapshot(Type controller, string action, string route, int status, Type resource)
        {
            var declared = controller.GetMethod(action).GetCustomAttributes<ProducesResponseTypeAttribute>()
                .Single(attribute => attribute.StatusCode == status);
            Assert.That(declared.Type, Is.EqualTo(resource));
            var snapshot = JObject.Parse(File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "openapi.json")));
            var response = snapshot["paths"][$"/api/v1/{route}"]["post"]["responses"][status.ToString(System.Globalization.CultureInfo.InvariantCulture)];
            Assert.That(response, Is.Not.Null, "Regenerate the API snapshot with scripts/docs.sh; do not hand-edit it.");
            var schema = response["content"]["application/json"]["schema"];
            Assert.That(schema["$ref"]?.Value<string>(), Is.EqualTo($"#/components/schemas/{resource.Name}"));
        }

        [Test]
        public void pending_paging_and_optional_delete_should_match_the_generated_snapshot()
        {
            var action = typeof(PendingAuthorImportController).GetMethod(nameof(PendingAuthorImportController.GetPaged));
            Assert.That(action.GetCustomAttribute<HttpGetAttribute>().Template, Is.EqualTo("paged"));
            var deleteParameter = typeof(PendingAuthorImportController).GetMethod(nameof(PendingAuthorImportController.Cancel)).GetParameters().Single(parameter => parameter.Name == "delete");
            Assert.That(deleteParameter.GetCustomAttribute<FromQueryAttribute>(), Is.Not.Null);
            Assert.That(deleteParameter.ParameterType, Is.EqualTo(typeof(bool)));
            Assert.That(deleteParameter.DefaultValue, Is.False);
            var snapshot = JObject.Parse(File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "openapi.json")));
            AssertPendingReadContract(snapshot);
        }

        private static void AssertPendingReadContract(JObject snapshot)
        {
            var paged = snapshot["paths"]["/api/v1/pendingauthorimport/paged"]?["get"];
            Assert.That(paged, Is.Not.Null, "Regenerate the API snapshot with scripts/docs.sh.");
            Assert.That(paged["parameters"].Select(parameter => parameter["name"].Value<string>()),
                Is.EquivalentTo(new[] { "page", "pageSize", "sortKey", "sortDirection" }));
            Assert.That(paged["responses"]["200"]["content"]["application/json"]["schema"]["$ref"].Value<string>(),
                Is.EqualTo("#/components/schemas/PendingAuthorImportResourcePagingResource"));
            var parameters = snapshot["paths"]["/api/v1/pendingauthorimport/{id}"]["delete"]["parameters"];
            var deletion = parameters.SingleOrDefault(parameter => parameter["name"].Value<string>() == "delete");
            Assert.That(deletion, Is.Not.Null);
            Assert.That(deletion["in"].Value<string>(), Is.EqualTo("query"));
            Assert.That(deletion["required"]?.Value<bool>() ?? false, Is.False);
            Assert.That(deletion["schema"]["type"].Value<string>(), Is.EqualTo("boolean"));
            Assert.That(deletion["schema"]["default"].Value<bool>(), Is.False);
        }

        [TestCase("paged")]
        [TestCase("delete")]
        public void missing_pending_api_snapshot_contract_should_be_detected(string missing)
        {
            var snapshot = JObject.Parse(File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "openapi.json")));
            if (missing == "paged") ((JObject)snapshot["paths"]).Remove("/api/v1/pendingauthorimport/paged");
            else snapshot["paths"]["/api/v1/pendingauthorimport/{id}"]["delete"]["parameters"]
                .Single(parameter => parameter["name"].Value<string>() == "delete").Remove();
            Assert.Throws<AssertionException>(() => AssertPendingReadContract(snapshot));
        }

        [Test]
        public void optional_monitoring_options_should_allow_null_without_changing_the_shared_type()
        {
            using var services = CreateServices();
            var repository = new SchemaRepository();
            services.GetRequiredService<ISchemaGenerator>().GenerateSchema(typeof(ImportListResource), repository);
            var property = repository.Schemas[nameof(ImportListResource)].Properties["monitoringOptions"];
            Assert.That(property.Nullable, Is.True);
            Assert.That(property.AllOf, Has.Count.EqualTo(1));
            Assert.That(property.AllOf[0].Reference.Id, Is.EqualTo(nameof(ImportListMonitoringOptions)));
            Assert.That(repository.Schemas[nameof(ImportListMonitoringOptions)].Nullable, Is.False);
        }

        [TestCase("audiobookMonitorNewItems", nameof(NewItemMonitorTypes))]
        [TestCase("ebookMonitorNewItems", nameof(NewItemMonitorTypes))]
        [TestCase("audiobookMonitorExistingMode", nameof(MonitorTypes))]
        [TestCase("ebookMonitorExistingMode", nameof(MonitorTypes))]
        public void optional_monitoring_enum_should_allow_null_without_changing_the_shared_enum(string name, string enumName)
        {
            using var services = CreateServices();
            var repository = new SchemaRepository();
            services.GetRequiredService<ISchemaGenerator>().GenerateSchema(typeof(BookImportAuthorMonitoring), repository);
            var property = repository.Schemas[nameof(BookImportAuthorMonitoring)].Properties[name];
            Assert.That(property.Nullable, Is.True);
            Assert.That(property.AllOf, Has.Count.EqualTo(1));
            Assert.That(property.AllOf[0].Reference.Id, Is.EqualTo(enumName));
            Assert.That(repository.Schemas[enumName].Nullable, Is.False);
            Assert.That(repository.Schemas[enumName].Enum, Is.Not.Empty);
        }

        [TestCase("monitorNewItems", nameof(NewItemMonitorTypes))]
        [TestCase("shouldMonitor", nameof(ImportListMonitorType))]
        public void nonnullable_monitoring_enum_should_keep_its_original_contract(string name, string enumName)
        {
            using var services = CreateServices();
            var repository = new SchemaRepository();
            services.GetRequiredService<ISchemaGenerator>().GenerateSchema(typeof(ImportListResource), repository);
            var property = repository.Schemas[nameof(ImportListResource)].Properties[name];
            Assert.That(property.Nullable, Is.False);
            var reference = property.Reference ?? property.AllOf.Single().Reference;
            Assert.That(reference.Id, Is.EqualTo(enumName));
            Assert.That(repository.Schemas[enumName].Nullable, Is.False);
        }
    }
}
