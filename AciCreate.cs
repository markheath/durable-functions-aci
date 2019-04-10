using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Azure.Services.AppAuthentication;
using Newtonsoft.Json;
using Microsoft.Azure.Management.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent.Core;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent.Authentication;
using System.Collections.Generic;
using Microsoft.Azure.Management.ContainerInstance.Fluent.Models;
using Microsoft.Azure.Management.ContainerInstance.Fluent.ContainerGroup.Definition;
using System.Linq;

namespace DurableFunctionsAci
{

    public static class AciCreate
    {
        private static IAzure azure = GetAzure();

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
                return new BadRequestObjectResult("Please pass a name on the query string");
            }

            def.EnvironmentVariables = new Dictionary<string, string>
            {
                { "Name", "Value" },
            };

            //await RunTaskBasedContainer(log, def);
            var orchestrationId = await client.StartNewAsync(nameof(AciCreateOrchestrator), def);
            return new OkObjectResult($"Started {def.ContainerGroupName} with {orchestrationId}");
        }

        [FunctionName(nameof(AciCreateOrchestrator))]
        public static async Task AciCreateOrchestrator(
            [OrchestrationTrigger] DurableOrchestrationContextBase ctx,
            ILogger log)
        {
            var definition = ctx.GetInput<ContainerGroupDefinition>();
            await ctx.CallActivityAsync(nameof(AciCreateActivity), definition);
        }

        [FunctionName(nameof(AciCreateActivity))]
        public static async Task AciCreateActivity(
            [ActivityTrigger] ContainerGroupDefinition definition,
            ILogger logger
        )
        {
            await RunTaskBasedContainer(logger, definition);
        }

        private static IAzure GetAzure()
        {

            var tenantId = Environment.GetEnvironmentVariable("TenantId");
            var clientId = Environment.GetEnvironmentVariable("ClientId");
            var clientSecret = Environment.GetEnvironmentVariable("ClientSecret");
            AzureCredentials credentials;

            if (!string.IsNullOrEmpty(tenantId) && 
                !string.IsNullOrEmpty(clientId) &&
                !string.IsNullOrEmpty(clientSecret))
            {
                var sp = new ServicePrincipalLoginInformation
                {
                    ClientId = clientId,
                    ClientSecret = clientSecret
                };
                credentials = new AzureCredentials(sp, tenantId, AzureEnvironment.AzureGlobalCloud);
            }
            else
            {
                credentials = SdkContext
                    .AzureCredentialsFactory
                    .FromMSI(new MSILoginInformation(MSIResourceType.AppService), AzureEnvironment.AzureGlobalCloud);
            }
            var authenticatedAzure = Azure
                .Configure()
                .WithLogLevel(HttpLoggingDelegatingHandler.Level.Basic)
                .Authenticate(credentials);
            var subscriptionId = Environment.GetEnvironmentVariable("SubscriptionId");
            if (!String.IsNullOrEmpty(subscriptionId))
                return authenticatedAzure.WithSubscription(subscriptionId);
            return authenticatedAzure.WithDefaultSubscription();
        }

        // volume mounting: https://github.com/Azure/azure-libraries-for-net/blob/master/Samples/ContainerInstance/ManageWithAzureFileShareMount.cs
        // https://github.com/Azure/azure-libraries-for-net/blob/master/Samples/ContainerInstance/ManageWithManualAzureFileShareMountCreation.cs

        private static async Task RunTaskBasedContainer(
                    ILogger log,
                    ContainerGroupDefinition cg)
        {
            log.LogInformation($"Creating container group '{cg.ContainerGroupName}' with start command '{cg.CommandLine}'");

            // Get the resource group's region
            
            IResourceGroup resGroup = await azure.ResourceGroups.GetByNameAsync(cg.ResourceGroupName);
            Region azureRegion = resGroup.Region;
            log.LogInformation($"Region is {azureRegion}");
            //Region.EuropeWest
            // Create the container group
            var baseDefinition = azure.ContainerGroups.Define(cg.ContainerGroupName)
                .WithRegion(azureRegion)
                .WithExistingResourceGroup(cg.ResourceGroupName)
                .WithLinux()
                .WithPublicImageRegistryOnly();

            IWithFirstContainerInstance definition;
            var vol = cg.AzureFileShareVolumes?.FirstOrDefault();
            if (vol != null)
            {
                definition = baseDefinition.DefineVolume(vol.VolumeName)
                    .WithExistingReadWriteAzureFileShare(vol.ShareName)
                    .WithStorageAccountName(vol.StorageAccountName)
                    .WithStorageAccountKey(vol.StorageAccountKey)
                    .Attach();
            }
            else
            {
                definition = baseDefinition.WithoutVolume();
            }
            
            var withInstance = definition.DefineContainerInstance(cg.ContainerGroupName + "-1")
                    .WithImage(cg.ContainerImage)
                    .WithExternalTcpPort(80)
                    .WithCpuCoreCount(1.0)
                    .WithMemorySizeInGB(1);

            if (vol != null)
                withInstance = withInstance.WithVolumeMountSetting(vol.VolumeName, vol.MountPath);

            if (!String.IsNullOrEmpty(cg.CommandLine))
            {
                var args = SplitArguments(cg.CommandLine);
                log.LogInformation("Command line: " + String.Join('|',args));
                withInstance = withInstance.WithStartingCommandLine(args[0], args.Skip(1).ToArray());
            }

            var withCreate = withInstance
                    .WithEnvironmentVariables(cg.EnvironmentVariables)
                    .Attach()
                .WithDnsPrefix(cg.ContainerGroupName)
                .WithRestartPolicy(ContainerGroupRestartPolicy.Never);
            
            var containerGroup = await withCreate.CreateAsync();
            // Print the container's logs
            //Console.WriteLine($"Logs for container '{containerGroupName}-1':");
            //Console.WriteLine(await containerGroup.GetLogContentAsync(containerGroupName + "-1"));
        }

        public static string[] SplitArguments(string commandLine)
        {
            var parmChars = commandLine.ToCharArray();
            var inSingleQuote = false;
            var inDoubleQuote = false;
            for (var index = 0; index < parmChars.Length; index++)
            {
                if (parmChars[index] == '"' && !inSingleQuote)
                {
                    inDoubleQuote = !inDoubleQuote;
                    parmChars[index] = '\n';
                }
                if (parmChars[index] == '\'' && !inDoubleQuote)
                {
                    inSingleQuote = !inSingleQuote;
                    parmChars[index] = '\n';
                }
                if (!inSingleQuote && !inDoubleQuote && parmChars[index] == ' ')
                    parmChars[index] = '\n';
            }
            return (new string(parmChars)).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }
    }
    
}
