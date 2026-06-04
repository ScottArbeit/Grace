
# DirectoryVersionApiDto

Public directory version DTO returned by directory query endpoints.

## Properties

Name | Type
------------ | -------------
`directoryVersion` | [DirectoryVersion](DirectoryVersion.md)
`recursiveSize` | number
`deletedAt` | Date
`deleteReason` | string
`hashesValidated` | boolean

## Example

```typescript
import type { DirectoryVersionApiDto } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "directoryVersion": null,
  "recursiveSize": 4096,
  "deletedAt": null,
  "deleteReason": ,
  "hashesValidated": true,
} satisfies DirectoryVersionApiDto

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as DirectoryVersionApiDto
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


