k delete -f .\kubernetes-deployment.yaml
docker build -t "scottarbeit/grace-server:latest" .
k apply -f .\kubernetes-deployment.yaml
