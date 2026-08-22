@description('Azure Container Instances group name for the disposable Redis process.')
param name string

@description('Azure region for the Redis container group.')
param location string

@description('Tags applied to the Redis container group.')
param tags object

@description('Globally unique DNS label whose FQDN is present in the generated server certificate.')
param dnsNameLabel string

@secure()
@description('Base64-encoded PEM certificate for the per-run lab CA.')
param caCertificate string

@secure()
@description('Base64-encoded PEM certificate for the Redis TLS server.')
param serverCertificate string

@secure()
@description('Base64-encoded PEM private key for the Redis TLS server.')
param serverPrivateKey string

@secure()
@description('Base64-encoded Redis ACL file containing the generated per-run credential.')
param aclFile string

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
            '--port'
            '0'
            '--tls-port'
            '6380'
            '--tls-cert-file'
            '/redis-secrets/server.crt'
            '--tls-key-file'
            '/redis-secrets/server.key'
            '--tls-ca-cert-file'
            '/redis-secrets/ca.crt'
            '--tls-auth-clients'
            'no'
            '--aclfile'
            '/redis-secrets/users.acl'
            '--save'
            ''
            '--appendonly'
            'no'
            '--protected-mode'
            'yes'
          ]
          image: image
          ports: [
            {
              port: 6380
              protocol: 'TCP'
            }
          ]
          resources: {
            requests: {
              cpu: json('0.5')
              memoryInGB: json('0.5')
            }
          }
          volumeMounts: [
            {
              mountPath: '/redis-secrets'
              name: 'redis-secrets'
              readOnly: true
            }
          ]
        }
      }
    ]
    ipAddress: {
      dnsNameLabel: dnsNameLabel
      ports: [
        {
          port: 6380
          protocol: 'TCP'
        }
      ]
      type: 'Public'
    }
    osType: 'Linux'
    restartPolicy: 'Always'
    volumes: [
      {
        name: 'redis-secrets'
        secret: {
          'ca.crt': caCertificate
          'server.crt': serverCertificate
          'server.key': serverPrivateKey
          'users.acl': aclFile
        }
      }
    ]
  }
}

output containerGroupName string = redisContainerGroup.name
output fqdn string = redisContainerGroup.properties.ipAddress.fqdn
output image string = image
output tlsPort int = 6380
