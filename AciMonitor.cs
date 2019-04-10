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
            log.LogInformation($"{eventGridEvent.EventType}-{eventGridEvent.Subject}-{eventGridEvent.Topic}");
            
            log.LogInformation(eventGridEvent.Data.ToString());
        }
    }
}
