// P2-04 / D5: Azure Cache for Redis. The protocol is the seam — this is a
// connection-string change only, no provider abstraction. maxmemory-policy
// mirrors docker-compose.yml exactly: velocity counters, dedupe keys and
// challenge tokens all carry TTLs, so only volatile keys are ever evicted.
param location string
param environmentName string
@description('Precomputed in main.bicep.')
param redisName string

var skuConfig = environmentName == 'prod'
  ? { name: 'Standard', family: 'C', capacity: 1 }
  : { name: 'Basic', family: 'C', capacity: 0 }

resource redis 'Microsoft.Cache/redis@2023-08-01' = {
  name: redisName
  location: location
  properties: {
    sku: skuConfig
    enableNonSslPort: false
    minimumTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    redisConfiguration: {
      'maxmemory-policy': 'volatile-lru'
    }
  }
}

output redisHostName string = redis.properties.hostName
@secure()
output redisPrimaryKey string = redis.listKeys().primaryKey
