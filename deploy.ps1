# ensure we're on the correct subscription
az account show -o table
az account set -s "Microsoft Azure Sponsorship"

# create the resource group
$resourceGroup = "DurableFunctionsAci"
$location = "westeurope"
az group create -n $resourceGroup -l $location

# to see if anything was already in there
az resource list -g $resourceGroup -o table

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

# create an app insights instance
$appInsightsName = "$prefix$rand"
az resource create `
  -g $resourceGroup -n $appInsightsName `
  --resource-type "Microsoft.Insights/components" `
  --properties '{\"Application_Type\":\"web\"}'

# create a function app running on consumption plan
$functionAppName = "$prefix$rand"
az functionapp create `
    -n $functionAppName `
    --storage-account $storageAccountName `
    --consumption-plan-location $location `
    --app-insights $appInsightsName `
    --runtime dotnet `
    -g $resourceGroup

# create a managed identity (idempotent - returns the existing identity if there already is one)
az functionapp identity assign -n $functionAppName -g $resourceGroup

$principalId = az functionapp identity show -n $functionAppName -g $resourceGroup --query principalId -o tsv
$tenantId = az functionapp identity show -n $functionAppName -g $resourceGroup --query tenantId -o tsv

# grant the Function App's managed identity contributor rights to this resource group
# so it can create Azure CLI instances
# https://docs.microsoft.com/en-us/azure/role-based-access-control/role-assignments-cli

$subscriptionId = az account show --query "id" -o tsv
# to see if any assignments already exist:
az role assignment list  --assignee $principalId `
    --scope "/subscriptions/$subscriptionId/resourceGroups/$aciResourceGroup"

# grant the contributor role
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
# we need to get hold of some keys first
$hostName = az functionapp show -g $resourceGroup -n $functionAppName --query "defaultHostName" -o tsv

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

function getFunctionKey([string]$appName, [string]$functionName, [string]$encodedCreds)
{
    $jwt = Invoke-RestMethod -Uri "https://$appName.scm.azurewebsites.net/api/functions/admin/token" -Headers @{Authorization=("Basic {0}" -f $encodedCreds)} -Method GET

    $keys = Invoke-RestMethod -Method GET -Headers @{Authorization=("Bearer {0}" -f $jwt)} `
            -Uri "https://$appName.azurewebsites.net/admin/functions/$functionName/keys" 

    $code = $keys.keys[0].value
    return $code
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
$functionUrl = "https://$hostName/runtime/webhooks/EventGrid?functionName=$functionName" + "&code=$extensionKey" # https://docs.microsoft.com/en-us/azure/azure-functions/functions-bindings-event-grid

# this is so horrible! https://github.com/Azure/azure-cli/issues/7147
$func2 = $functionUrl.Replace("&", "^^^&")
# we're subscribing to events that happen in our ACI resource group
# The Microsoft.EventGrid resource provider is not registered in subscription 671b9a61-c023-4cf4-8736-80875bd06db3
# check we're registered for creating EventGrid subscriptions
az provider show -n "Microsoft.EventGrid" --query "registrationState"
# register if not
az provider register -n "Microsoft.EventGrid" 
# create the event grid
$subscriptionId = az account show --query id -o tsv
$resourceId = "/subscriptions/$subscriptionId/resourcegroups/$aciResourceGroup"
az eventgrid event-subscription create --name "AciEvents" `
    --source-resource-id $resourceId `
    --endpoint-type "WebHook" --included-event-types "All" `
    --endpoint $func2

# The attempt to validate the provided endpoint https://durablefuncsaci26076.azurewebsites.net/runtime/webhooks/eventgrid failed. For more details, visit https://aka.ms/esvalidation.
# https://docs.microsoft.com/en-us/azure/event-grid/security-authentication
# https://github.com/Azure/azure-sdk-for-net/issues/4732
# https://github.com/Azure/Azure-Functions/issues/1007
# an alternative way that uses an ARM template
#az group deployment create -g $aciResourceGroup --template-file "EventGridSubscription.json" `
#     --parameters SubscriptionName=AciEvents1 WebhookUrl=$functionUrl

# setting up the ACI app settings
az functionapp config appsettings set -n $functionAppName -g $resourceGroup `
    --settings "AciResourceGroup=$aciResourceGroup" "SubscriptionId=$subscriptionId"


# calling the function
# ensure TLS 1.2 enabled
#[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$functionCode = getFunctionKey $functionAppName "AciCreate" $kuduCreds 
$containerGroupName = "markacitest1"
Invoke-WebRequest -Uri "https://$hostName/api/AciCreate?code=$functionCode&name=$containerGroupName"

# check it worked
az resource list -g $aciResourceGroup -o table
az container show -g $aciResourceGroup -n $containerGroupName
$containerDomain = az container show -g $aciResourceGroup -n $containerGroupName --query "ipAddress.fqdn" -o tsv
Start-Process "http://$containerDomain"

# clean up the container
az container delete -g $aciResourceGroup -n $containerGroupName -y

# to delete the event grid subscription
az eventgrid event-subscription delete --name "AciEvents" --source-resource-id $resourceId

az group delete -n $resourceGroup -y --no-wait

az group delete -n $aciResourceGroup -y --no-wait