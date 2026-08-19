@description('Azure Container Instances group name for the disposable Redis process.')
param name string

@description('Azure region for the Redis container group.')
param location string

@description('Tags applied to the Redis container group.')
param tags object

@description('Public Redis image used only by the disposable infrastructure lab.')
param image string = 'redis:7.4-alpine'

resource redisContainerGroup 'Microsoft.ContainerInstance/containerGroups@2023-05-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    containers: [
      {
        name: 'redis'
        properties: {
          command: [
            'redis-server'
            '--save'
            ''
            '--appendonly'
            'no'
            '--protected-mode'
            'yes'
          ]
          image: image
          resources: {
            requests: {
              cpu: json('0.5')
              memoryInGB: json('0.5')
            }
          }
        }
      }
    ]
    osType: 'Linux'
    restartPolicy: 'Always'
  }
}

output containerGroupName string = redisContainerGroup.name
output image string = image
