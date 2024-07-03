$startTime = Get-Date
Write-Host "Deleting Kubernetes deployment..."
Start-Process -FilePath 'C:\Program Files\Docker\Docker\resources\bin\kubectl.exe' -ArgumentList 'delete -f .\kubernetes-deployment.yaml'
#docker build -t "scottarbeit/grace-server:latest" .
$startBulidTime = Get-Date
Write-Host "Building the solution..."
dotnet build .\Grace.sln -c Debug
Write-Host
Write-Host "Creating Docker image..."
dotnet publish .\Grace.Server\Grace.Server.fsproj --no-build -c Debug -p:PublishProfile=DefaultContainer
Write-Host
Write-Host "Publishing Docker image to Docker Hub..."
$startPublishTime = Get-Date
#docker push scottarbeit/grace-server:latest
Write-Host
$finishBuildTime = Get-Date
Write-Host "Docker push time: $([math]::Round(($finishBuildTime - $startPublishTime).TotalSeconds, 2)) seconds"
Write-Host
Write-Host "Build and publish time: $([math]::Round(($finishBuildTime - $startBulidTime).TotalSeconds, 2)) seconds"
Write-Host
Write-Host "Restarting Kubernetes deployment..."
k apply -f .\kubernetes-deployment.yaml
$finishTime = Get-Date
Write-Host
Write-Host "Total Time: $([math]::Round(($finishTime - $startTime).TotalSeconds, 2)) seconds"
