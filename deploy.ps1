# ensure we're on the correct subscription
az account show -o table
az account set -s "Microsoft Azure Sponsorship"

# create the resource group
$resourceGroup = "DurableFunctionsAci"
$location = "westeurope"
az group create -n $resourceGroup -l $location

$aciResourceGroup = "DurableFunctionsAciContainers"
az group create -n $aciResourceGroup -l $location

# to be idempotent, see if we already picked a random suffix
$existingName = az storage account list -g $resourceGroup --query "[].name" -o tsv
$prefix = "durablefuncsaci"
if ($existingName) { 
    $rand = $existingName.SubString($prefix.Length)
 } 
 else 
 { $rand = Get-Random -Minimum 10000 -Maximum 99999 }

# create a storage account
$storageAccountName = "$prefix$rand"
az storage account create `
  -n $storageAccountName `
  -l $location `
  -g $resourceGroup `
  --sku Standard_LRS

# create a function app running on consumption plan
$functionAppName = "$prefix$rand"
az functionapp create `
    -n $functionAppName `
    --storage-account $storageAccountName `
    --consumption-plan-location $location `
    --runtime dotnet `
    -g $resourceGroup

# create a managed identity (idempotent - returns the existing identity if there already is one)
az functionapp identity assign -n $functionAppName -g $resourceGroup

$principalId = az functionapp identity show -n $functionAppName -g $resourceGroup --query principalId -o tsv
$tenantId = az functionapp identity show -n $functionAppName -g $resourceGroup --query tenantId -o tsv

# let's see if we can grant the Function App's managed identity contributor rights to this resource group
# so it can create Azure CLI instances
# https://docs.microsoft.com/en-us/azure/role-based-access-control/role-assignments-cli
# not sure if I need    --resource-group $resourceGroup `

$subscriptionId = az account show --query "id" -o tsv
az role assignment create --role "Contributor" `
    --assignee-object-id $principalId `
    --scope "/subscriptions/$subscriptionId/resourceGroups/$aciResourceGroup"

# TODO - publish the function app
$publishFolder = "publish"
dotnet publish -c Release -o $publishFolder
$publishFolder = "FunctionsDemo/bin/Release/netcoreapp2.1/publish"

# create the zip
$publishZip = "publish.zip"
if(Test-path $publishZip) {Remove-item $publishZip}
Add-Type -assembly "system.io.compression.filesystem"
[io.compression.zipfile]::CreateFromDirectory($publishFolder, $publishZip)

# deploy the zipped package
az functionapp deployment source config-zip `
 -g $resourceGroup -n $functionAppName --src $publishZip

# create the event grid subscription
$hostName = az functionapp show -g $resourceGroup -n $functionAppName --query "defaultHostName" -o tsv
$code = "TODO - get function secure code" # func azure functionapp list-functions $functionAppName --show-keys

az eventgrid event-subscription create -g $resourceGroup --name "AciEvents" `
    --endpoint-type "webhook" --included-event-types "All" `
    --resource-id "" `
    --endpoint https://$hostName/api/AciMonitor?code=$code