
# SynchronizedConflictProvenanceDto


## Properties

Name | Type
------------ | -------------
`sourceOperationId` | string
`sourceItemId` | string
`canonicalItemId` | string
`conflictItemId` | string
`conflictPath` | string
`acceptedAt` | Date
`sourceContentVersionId` | string
`baseContentVersionId` | string

## Example

```typescript
import type { SynchronizedConflictProvenanceDto } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "sourceOperationId": null,
  "sourceItemId": null,
  "canonicalItemId": null,
  "conflictItemId": null,
  "conflictPath": null,
  "acceptedAt": null,
  "sourceContentVersionId": null,
  "baseContentVersionId": null,
} satisfies SynchronizedConflictProvenanceDto

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as SynchronizedConflictProvenanceDto
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


