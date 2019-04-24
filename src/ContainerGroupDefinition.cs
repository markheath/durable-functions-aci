using System.Collections.Generic;

namespace DurableFunctionsAci
{
    public class ContainerGroupDefinition
    {
        public string ResourceGroupName { get; set; }
        public string ContainerGroupName { get; set; }
        public string ContainerImage { get; set;}
        public string CommandLine { get; set; }
        public List<AzureFileShareVolumeDefinition> AzureFileShareVolumes { get; set; }
        public Dictionary<string, string> EnvironmentVariables { get; set; }
    }
}
