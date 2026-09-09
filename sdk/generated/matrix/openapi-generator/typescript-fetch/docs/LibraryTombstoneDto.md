
# LibraryTombstoneDto


## Properties

Name | Type
------------ | -------------
`deletedAt` | Date
`deletedBy` | string
`deleteCursor` | string
`lastNamespace` | [LibraryNamespaceDto](LibraryNamespaceDto.md)
`lastContentVersionId` | string

## Example

```typescript
import type { LibraryTombstoneDto } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "deletedAt": null,
  "deletedBy": null,
  "deleteCursor": null,
  "lastNamespace": null,
  "lastContentVersionId": null,
} satisfies LibraryTombstoneDto

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as LibraryTombstoneDto
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


