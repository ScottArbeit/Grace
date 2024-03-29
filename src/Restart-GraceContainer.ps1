$startTime = Get-Date
Start-Process -FilePath 'C:\Program Files\Docker\Docker\resources\bin\kubectl.exe' -ArgumentList 'delete -f .\kubernetes-deployment.yaml'
#docker build -t "scottarbeit/grace-server:latest" .
$startBulidTime = Get-Date
dotnet build .\Grace.Server\Grace.Server.fsproj -c Debug
Write-Host
dotnet publish .\Grace.Server\Grace.Server.fsproj --no-build -c Debug -p:PublishProfile=DefaultContainer
Write-Host
$finishBuildTime = Get-Date
Write-Host "Build and publish time: $([math]::Round(($finishBuildTime - $startBulidTime).TotalSeconds, 2)) seconds"
Write-Host
k apply -f .\kubernetes-deployment.yaml
$finishTime = Get-Date
Write-Host
Write-Host "Total Time: $([math]::Round(($finishTime - $startTime).TotalSeconds, 2)) seconds"
