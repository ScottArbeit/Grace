$startTime = Get-Date

# Using Start-Process here to run the deletion of the current deployment asynchronously.
Write-Host "Deleting Kubernetes deployment..."
Start-Process -FilePath 'C:\Program Files\Docker\Docker\resources\bin\kubectl.exe' -ArgumentList 'delete -f .\kubernetes-deployment.yaml' -WindowStyle Minimized
#k delete -f .\kubernetes-deployment.yaml
docker image rm --force scottarbeit/grace-server:latest scottarbeit/grace-server:0.1
Write-Host

$startBulidTime = Get-Date
Write-Host "Building the solution..."
Start-Process -FilePath 'C:\Program Files\dotnet\dotnet.exe' -ArgumentList 'build .\Grace.sln -c Debug' -WindowStyle Minimized
Write-Host

Write-Host "Publishing Grace.Server container to local registry..."
#dotnet publish .\Grace.Server\Grace.Server.fsproj --no-build -c Debug -t:PublishContainer
docker build -t scottarbeit/grace-server:latest -t scottarbeit/grace-server:0.1 --progress=plain -f .\Grace.Server\Dockerfile .
Write-Host

#Write-Host "Pushing Docker image to Docker Hub..."
#$startPublishTime = Get-Date
#docker push scottarbeit/grace-server:latest
#Write-Host
#Write-Host "Docker push time: $([math]::Round(($finishBuildTime - $startPublishTime).TotalSeconds, 2)) seconds"
#Write-Host

$finishBuildTime = Get-Date
Write-Host "Build and publish time: $([math]::Round(($finishBuildTime - $startBulidTime).TotalSeconds, 2)) seconds"
Write-Host

Write-Host "Restarting Kubernetes deployment..."
k apply -f .\kubernetes-deployment.yaml
$finishTime = Get-Date
Write-Host

Write-Host "Total Time: $([math]::Round(($finishTime - $startTime).TotalSeconds, 2)) seconds"
