
# SynchronizedOperationReceiptDto


## Properties

Name | Type
------------ | -------------
`operationId` | string
`requestHash` | string
`outcome` | [SynchronizedOutcomeKind](SynchronizedOutcomeKind.md)
`rootConfigurationVersion` | string
`recordedAt` | Date
`principalId` | string
`mutation` | [SynchronizedMutationDto](SynchronizedMutationDto.md)
`cursor` | string
`item` | [SynchronizedItemDto](SynchronizedItemDto.md)
`conflict` | [SynchronizedConflictProvenanceDto](SynchronizedConflictProvenanceDto.md)
`reasonCode` | string
`currentRootConfiguration` | [SynchronizedRootConfigurationDto](SynchronizedRootConfigurationDto.md)
`rebaseline` | [SynchronizedRebaselineDto](SynchronizedRebaselineDto.md)

## Example

```typescript
import type { SynchronizedOperationReceiptDto } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "operationId": null,
  "requestHash": null,
  "outcome": null,
  "rootConfigurationVersion": null,
  "recordedAt": null,
  "principalId": null,
  "mutation": null,
  "cursor": null,
  "item": null,
  "conflict": null,
  "reasonCode": null,
  "currentRootConfiguration": null,
  "rebaseline": null,
} satisfies SynchronizedOperationReceiptDto

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as SynchronizedOperationReceiptDto
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


