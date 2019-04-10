using System.Collections.Generic;
using Microsoft.Azure.Management.ContainerInstance.Fluent.Models;

namespace DurableFunctionsAci
{
    public class ContainerGroupStatus
    {
        public ContainerInstanceStatus[] Containers { get; set;}

        public string State { get; set; }
        public string Id { get; internal set; }
        public string Name { get; internal set; }
        public string ResourceGroupName { get; internal set; }
    }
}