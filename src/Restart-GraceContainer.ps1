Start-Process -FilePath 'C:\Program Files\Docker\Docker\resources\bin\kubectl.exe' -ArgumentList 'delete -f .\kubernetes-deployment.yaml'
docker build -t "scottarbeit/grace-server:latest" .
k apply -f .\kubernetes-deployment.yaml
