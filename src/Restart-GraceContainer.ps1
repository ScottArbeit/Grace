Start-Process -FilePath 'C:\Program Files\Docker\Docker\resources\bin\kubectl.exe' -ArgumentList 'delete -f .\kubernetes-deployment.yaml'
#docker build -t "scottarbeit/grace-server:latest" .
dotnet publish .\Grace.Server\Grace.Server.fsproj -c Debug -p:PublishProfile=DefaultContainer
k apply -f .\kubernetes-deployment.yaml
