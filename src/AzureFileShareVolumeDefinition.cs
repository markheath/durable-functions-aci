namespace DurableFunctionsAci
{
    public class AzureFileShareVolumeDefinition
    {
        public string StorageAccountName { get; set; }
        public string StorageAccountKey { get; set; }
        public string ShareName { get; set; }
        public string VolumeName { get; set; }
        public string MountPath { get; set; }
    }
}
