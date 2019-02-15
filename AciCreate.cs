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

namespace DurableFunctionsAci
{
    public static class AciCreate
    {
        private static IAzure azure = GetAzure();

        [FunctionName("AciCreate")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            var resourceGroupName = Environment.GetEnvironmentVariable("AciResourceGroup");
            var containerGroupName = req.Query["name"];
            log.LogInformation($"AciCreate: CG={containerGroupName}, RG={resourceGroupName}");
            if (string.IsNullOrEmpty(containerGroupName))
            {
                return new BadRequestObjectResult("Please pass a name on the query string");
            }
            await RunTaskBasedContainer(log, resourceGroupName, containerGroupName, "markheath/miniblogcore:v1-linux", "");
            return new OkObjectResult($"Started {containerGroupName}");
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

        private static async Task RunTaskBasedContainer(
                    ILogger log,
                    string resourceGroupName, 
                    string containerGroupName, 
                    string containerImage,
                    string startCommandLine)
        {
            // Example of how to set up 
            var envVars = new Dictionary<string, string>
            {
                { "Name", "Value" },
            };

            log.LogInformation($"Creating container group '{containerGroupName}' with start command '{startCommandLine}'");

            // Get the resource group's region
            
            IResourceGroup resGroup = await azure.ResourceGroups.GetByNameAsync(resourceGroupName);
            Region azureRegion = resGroup.Region;
            log.LogInformation($"Region is {azureRegion}");
            //Region.EuropeWest
            // Create the container group
            var containerGroup = await azure.ContainerGroups.Define(containerGroupName)
                .WithRegion(azureRegion)
                .WithExistingResourceGroup(resourceGroupName)
                .WithLinux()
                .WithPublicImageRegistryOnly()
                //.WithNewAzureFileShareVolume("","")
                .WithoutVolume()
                .DefineContainerInstance(containerGroupName + "-1")
                    .WithImage(containerImage)
                    .WithExternalTcpPort(80)
                    .WithCpuCoreCount(1.0)
                    .WithMemorySizeInGB(1)
                    //.WithStartingCommandLine(startCommandLine)
                    .WithEnvironmentVariables(envVars)
                    .Attach()
                .WithDnsPrefix(containerGroupName)
                .WithRestartPolicy(ContainerGroupRestartPolicy.Never)
                .CreateAsync();
            
            // Print the container's logs
            //Console.WriteLine($"Logs for container '{containerGroupName}-1':");
            //Console.WriteLine(await containerGroup.GetLogContentAsync(containerGroupName + "-1"));
        }
    }
}
