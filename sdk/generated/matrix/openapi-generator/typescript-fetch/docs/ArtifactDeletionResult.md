
# ArtifactDeletionResult


## Properties

Name | Type
------------ | -------------
`artifactId` | string
`workItemId` | string
`deletionGeneration` | string
`deletedAt` | Date
`physicalDeletionAt` | Date
`deleteReason` | string

## Example

```typescript
import type { ArtifactDeletionResult } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "artifactId": null,
  "workItemId": null,
  "deletionGeneration": null,
  "deletedAt": null,
  "physicalDeletionAt": null,
  "deleteReason": null,
} satisfies ArtifactDeletionResult

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as ArtifactDeletionResult
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


