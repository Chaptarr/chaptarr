using System;
using Chaptarr.Http;
using Chaptarr.Http.Middleware;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Configuration;

namespace Chaptarr.Api.V1.Notifications
{
    [V1ApiController]
    public class NotificationController : ProviderControllerBase<NotificationResource, NotificationBulkResource, INotification, NotificationDefinition>
    {
        public static readonly NotificationResourceMapper ResourceMapper = new();
        public static readonly NotificationBulkResourceMapper BulkResourceMapper = new();
        private readonly IConfigService _configService;

        public NotificationController(NotificationFactory notificationFactory, IConfigService configService)
            : base(notificationFactory, "notification", ResourceMapper, BulkResourceMapper)
        {
            _configService = configService;
        }

        private bool PreserveBookshelfSettings => _configService.BookshelfNGDatabaseMigrated &&
            _configService.BookshelfNGMigrationProfile?.IsCompatible == true;

        protected override NotificationResource ToResource(NotificationDefinition definition)
        {
            var resource = base.ToResource(definition);
            if (PreserveBookshelfSettings && HttpContext?.GetReadarrFacadeContext()?.IsBookshelfCompatibility != true)
            {
                // The native UI must expose the migrated master switch too, so an
                // operator can re-enable it without editing the database or legacy API.
                ProviderResourceCompatibilityProjection.ProjectBookshelfLegacyFields(resource, definition.Settings);
            }

            return resource;
        }

        protected override void ApplyResourceSettings(NotificationResource resource, NotificationDefinition definition)
        {
            base.ApplyResourceSettings(resource, definition);
            if (PreserveBookshelfSettings)
            {
                ProviderResourceCompatibilityProjection.ApplyBookshelfLegacySettings(resource, definition.Settings);
            }
        }

        [NonAction]
        public override ActionResult<NotificationResource> UpdateProvider([FromBody] NotificationBulkResource providerResource)
        {
            throw new NotImplementedException();
        }

        // Remove the override to allow base class implementation for bulk delete
        // The base class properly handles bulk deletion of notifications
    }
}
