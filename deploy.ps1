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

# publish the function app
$publishFolder = "publish"
dotnet publish -c Release -o $publishFolder

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

#https://markheath.net/post/managing-azure-function-keys
function getKuduCreds($appName, $resourceGroup)
{
    $user = az webapp deployment list-publishing-profiles -n $appName -g $resourceGroup `
            --query "[?publishMethod=='MSDeploy'].userName" -o tsv

    $pass = az webapp deployment list-publishing-profiles -n $appName -g $resourceGroup `
            --query "[?publishMethod=='MSDeploy'].userPWD" -o tsv

    $pair = "$($user):$($pass)"
    $encodedCreds = [System.Convert]::ToBase64String([System.Text.Encoding]::ASCII.GetBytes($pair))
    return $encodedCreds
}

function getMasterFunctionKey([string]$appName, [string]$encodedCreds)
{
    $jwt = Invoke-RestMethod -Uri "https://$appName.scm.azurewebsites.net/api/functions/admin/token" -Headers @{Authorization=("Basic {0}" -f $encodedCreds)} -Method GET

    $keys = Invoke-RestMethod -Method GET -Headers @{Authorization=("Bearer {0}" -f $jwt)} `
            -Uri "https://$appName.azurewebsites.net/admin/host/systemkeys/_master" 

    # n.b. Key Management API documentation currently doesn't explain how to get master key correctly
    # https://github.com/Azure/azure-functions-host/wiki/Key-management-API
    # https://$appName.azurewebsites.net/admin/host/keys/_master = does NOT return master key
    # https://$appName.azurewebsites.net/admin/host/systemkeys/_master = does return master key

    return $keys.value
}


$kuduCreds = getKuduCreds $functionAppName $resourceGroup
# $jwt = Invoke-RestMethod -Uri "https://$functionAppName.scm.azurewebsites.net/api/functions/admin/token" -Headers @{Authorization=("Basic {0}" -f $kuduCreds)} -Method GET

$masterKey = getMasterFunctionKey $functionAppName $kuduCreds

# ugh - my powershell needs to be updated to support TLS 1.1
# https://stackoverflow.com/a/36266735/7532
# $AllProtocols = [System.Net.SecurityProtocolType]'Ssl3,Tls,Tls11,Tls12'
# [System.Net.ServicePointManager]::SecurityProtocol = $AllProtocols

$extensionKey = (Invoke-RestMethod -Method GET -Uri "https://$functionAppName.azurewebsites.net/admin/host/systemkeys/eventgrid_extension?code=$masterKey").value
$functionName = "AciMonitor"
$functionUrl = "https://$hostName/runtime/webhooks/eventgrid?functionName=$functionName" + "&code=$code" # https://docs.microsoft.com/en-us/azure/azure-functions/functions-bindings-event-grid

az eventgrid event-subscription create -g $resourceGroup --name "AciEvents" `
    --endpoint-type "webhook" --included-event-types "All" `
    --resource-id "" `
    --endpoint $functionUrl