// Default URL for triggering event grid function in the local environment.
// http://localhost:7071/runtime/webhooks/EventGrid?functionName={functionname}

using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Azure.EventGrid.Models;
using Microsoft.Azure.WebJobs.Extensions.EventGrid;
using Microsoft.Extensions.Logging;

namespace DurableFunctionsAci
{
    public static class AciMonitor
    {
        [FunctionName("AciMonitor")]
        public static void Run([EventGridTrigger]EventGridEvent eventGridEvent, ILogger log)
        {
            log.LogInformation($"EVENT: {eventGridEvent.EventType}-{eventGridEvent.Subject}-{eventGridEvent.Topic}");
            log.LogInformation(eventGridEvent.Data.ToString());
            
            // some properties on data:
            //  "resourceProvider": "Microsoft.ContainerInstance"
            // "status": "Succeeded"
            // "resourceUri": "/subscriptions/mysubid/resourceGroups/DurableFunctionsAciContainers/providers/Microsoft.ContainerInstance/containerGroups/markacitest1",
            dynamic data = eventGridEvent.Data;
            if (data.operationName == "Microsoft.ContainerInstance/containerGroups/delete")
            {
                log.LogInformation($"Deleted container group {data.resourceUri} with status {data.status}");
            }
            else if (data.operationName == "Microsoft.ContainerInstance/containerGroups/write")
            {
                log.LogInformation($"Created or updated container group {data.resourceUri} with status {data.status}");
            }
        }
    }
}
