using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Threading;

namespace DurableFunctionsAci
{
    public static class AciCreate
    {
        [FunctionName("AciCreate")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req,
            [OrchestrationClient] DurableOrchestrationClientBase client,
            ILogger log)
        {
            var body = await req.ReadAsStringAsync();
            var def = JsonConvert.DeserializeObject<ContainerGroupDefinition>(body);
            //var def = new ContainerGroupDefinition();
            //def.ResourceGroupName = Environment.GetEnvironmentVariable("AciResourceGroup");
            //def.ContainerGroupName = req.Query["name"];
            log.LogInformation($"AciCreate: CG={def.ContainerGroupName}, RG={def.ResourceGroupName}");
            if (string.IsNullOrEmpty(def.ContainerGroupName))
            {
                return new BadRequestObjectResult("Must provide a container group name");
            }

            def.EnvironmentVariables = new Dictionary<string, string>
            {
                { "Name", "Value" },
            };

            var orchestrationId = await client.StartNewAsync(nameof(AciCreateOrchestrator), def);
            var payload = client.CreateHttpManagementPayload(orchestrationId);
            log.LogInformation($"Started {def.ContainerGroupName} with {orchestrationId}");
            return new OkObjectResult(payload);
        }

        [FunctionName(nameof(AciCreateOrchestrator))]
        public static async Task AciCreateOrchestrator(
            [OrchestrationTrigger] DurableOrchestrationContextBase ctx,
            ILogger log)
        {
            var definition = ctx.GetInput<ContainerGroupDefinition>();
            await ctx.CallActivityAsync(nameof(AciCreateActivity), definition);
            if (ctx.IsReplaying)
                log.LogInformation("Created container");
            await ctx.CallSubOrchestratorAsync(nameof(AciWaitForExitOrchestrator), definition);
            if (ctx.IsReplaying)
                log.LogInformation("Container has exited");
            await ctx.CallActivityAsync(nameof(AciDeleteContainerGroupActivity), definition);
            if (ctx.IsReplaying)
                log.LogInformation("Container has been deleted");
        }

        [FunctionName(nameof(AciWaitForExitOrchestrator))]
        public static async Task AciWaitForExitOrchestrator(
            [OrchestrationTrigger] DurableOrchestrationContextBase ctx,
            ILogger log)
        {
            var definition = ctx.GetInput<ContainerGroupDefinition>();

            var containerGroupStatus = await ctx.CallActivityAsync<ContainerGroupStatus>(nameof(AciGetContainerGroupStatusActivity), definition);
            log.LogInformation($"{containerGroupStatus.Name} status: {containerGroupStatus.State}, " +
                $"{containerGroupStatus.Containers[0]?.CurrentState?.State}, " +
                $"{containerGroupStatus.Containers[0]?.CurrentState?.DetailStatus}");
            // container instance states we've seen: "Terminated", detailState = Completed
            if (containerGroupStatus.Containers[0]?.CurrentState?.State == "Terminated")
            {
                log.LogInformation($"Container has terminated with exit code {containerGroupStatus.Containers[0]?.CurrentState?.ExitCode}");
                return;
            }

            // go round again
            using(var cts = new CancellationTokenSource())
            {
                await ctx.CreateTimer(ctx.CurrentUtcDateTime.AddSeconds(30), cts.Token);
            }
            
            ctx.ContinueAsNew(definition);
        }

        [FunctionName(nameof(AciCreateActivity))]
        public static async Task AciCreateActivity(
            [ActivityTrigger] ContainerGroupDefinition definition,
            ILogger logger
        )
        {
            await AciHelpers.RunTaskBasedContainer(logger, definition);
        }

        
        [FunctionName(nameof(AciGetContainerGroupStatusActivity))]
        public static async Task<ContainerGroupStatus> AciGetContainerGroupStatusActivity(
            [ActivityTrigger] ContainerGroupDefinition definition,
            ILogger logger
        )
        {
            return await AciHelpers.GetContainerGroupStatus(logger, definition);
        }

        [FunctionName(nameof(AciDeleteContainerGroupActivity))]
        public static async Task AciDeleteContainerGroupActivity(
            [ActivityTrigger] ContainerGroupDefinition definition,
            ILogger logger
        )
        {
            await AciHelpers.DeleteContainerGroup(logger, definition);
        }
    }
}
