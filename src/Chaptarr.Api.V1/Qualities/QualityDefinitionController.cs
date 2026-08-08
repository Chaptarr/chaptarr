using System.Collections.Generic;
using Chaptarr.Http;
using Chaptarr.Http.REST;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Datastore.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Qualities;
using NzbDrone.Http.REST.Attributes;
using NzbDrone.SignalR;

namespace Chaptarr.Api.V1.Qualities
{
    [V1ApiController]
    public class QualityDefinitionController : RestControllerWithSignalR<QualityDefinitionResource, QualityDefinition>, IHandle<CommandExecutedEvent>
    {
        private readonly IQualityDefinitionService _qualityDefinitionService;

        public QualityDefinitionController(IQualityDefinitionService qualityDefinitionService, IBroadcastSignalRMessage signalRBroadcaster)
            : base(signalRBroadcaster)
        {
            _qualityDefinitionService = qualityDefinitionService;
        }

        [RestPutById]
        public ActionResult<QualityDefinitionResource> Update([FromBody] QualityDefinitionResource resource)
        {
            var model = resource.ToModel();
            _qualityDefinitionService.Update(model);
            return Accepted(model.Id);
        }

        protected override QualityDefinitionResource GetResourceById(int id)
        {
            return _qualityDefinitionService.GetById(id).ToResource();
        }

        [HttpGet]
        public List<QualityDefinitionResource> GetAll()
        {
            var allDefinitions = _qualityDefinitionService.All();

            // Return all qualities - other private trackers use similar systems
            return allDefinitions.ToResource();
        }

        [HttpPut("update")]
        public object UpdateMany([FromBody] List<QualityDefinitionResource> resource)
        {
            //Read from request
            var qualityDefinitions = resource.ToModel();

            _qualityDefinitionService.UpdateMany(qualityDefinitions);

            var allDefinitions = _qualityDefinitionService.All();

            // Return all qualities - other private trackers use similar systems
            return Accepted(allDefinitions.ToResource());
        }

        [NonAction]
        public void Handle(CommandExecutedEvent message)
        {
            if (message.Command.Name == "ResetQualityDefinitions")
            {
                BroadcastResourceChange(ModelAction.Sync);
            }
        }
    }
}
