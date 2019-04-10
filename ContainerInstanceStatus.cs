using System.Collections.Generic;
using Microsoft.Azure.Management.ContainerInstance.Fluent.Models;

namespace DurableFunctionsAci
{
    public class ContainerInstanceStatus
    {
        public string Name { get; internal set; }
        public string Image { get; internal set; }
        public IList<string> Command { get; internal set; }
        public ContainerState CurrentState { get; internal set; }
        public int? RestartCount { get; internal set; }
    }
}