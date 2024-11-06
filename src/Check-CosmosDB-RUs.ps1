# Set variables for the Azure Cosmos DB account and resource group
$resourceGroupName = "gracevcs-development"
$accountName = "gracevcs-development"
$databaseName = "gracevcs-development-db"
$containerName = "grace-development"

# Function to check the allocated RUs for the specified Cosmos DB account
function Get-CosmosDBRUs {
    try {
        # Retrieve the current RU settings for the specified container
        $ruSettings = az cosmosdb sql container throughput show `
            --resource-group $resourceGroupName `
            --account-name $accountName `
            --database-name $databaseName `
            --name $containerName `
            --query "resource.throughput" `
            --output json

        # Output the current RUs
        Write-Host "Current Allocated RUs: $ruSettings"
    } catch {
        Write-Host "Error fetching RU settings: $_"
    }
}

# Loop to check RUs every minute
while ($true) {
    Get-CosmosDBRUs
    Start-Sleep -Seconds 60
}
