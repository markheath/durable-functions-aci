using System.Collections.Generic;
using Microsoft.Azure.Management.ContainerInstance.Fluent.Models;

namespace DurableFunctionsAci
{
    public class ContainerInstanceStatus
    {
        public string Name { get; set; }
        public string Image { get; set; }
        public IList<string> Command { get; set; }
        public ContainerState CurrentState { get; set; }
        public int? RestartCount { get; set; }
    }
}