
# SynchronizedRepositoryStatusDto


## Properties

Name | Type
------------ | -------------
`state` | string
`repositoryId` | string
`rootConfigurationVersion` | string
`isCaughtUp` | boolean
`rebaselineRequired` | boolean
`isBlocked` | boolean
`pendingOperationCount` | number
`oldestPendingAgeMilliseconds` | number
`projectionLagCount` | number
`lastCompletedAt` | Date

## Example

```typescript
import type { SynchronizedRepositoryStatusDto } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "state": null,
  "repositoryId": ab6f35ef-6e01-440b-8f9b-c343a5272095,
  "rootConfigurationVersion": null,
  "isCaughtUp": null,
  "rebaselineRequired": null,
  "isBlocked": null,
  "pendingOperationCount": null,
  "oldestPendingAgeMilliseconds": null,
  "projectionLagCount": null,
  "lastCompletedAt": null,
} satisfies SynchronizedRepositoryStatusDto

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as SynchronizedRepositoryStatusDto
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


