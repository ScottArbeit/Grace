
# SynchronizedRootMutationResultDto


## Properties

Name | Type
------------ | -------------
`operationId` | string
`outcome` | [SynchronizedOutcomeKind](SynchronizedOutcomeKind.md)
`rootConfiguration` | [SynchronizedRootConfigurationDto](SynchronizedRootConfigurationDto.md)
`reasonCode` | string
`recordedAt` | Date

## Example

```typescript
import type { SynchronizedRootMutationResultDto } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "operationId": null,
  "outcome": null,
  "rootConfiguration": null,
  "reasonCode": null,
  "recordedAt": null,
} satisfies SynchronizedRootMutationResultDto

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as SynchronizedRootMutationResultDto
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


