
# LibraryChangeDto


## Properties

Name | Type
------------ | -------------
`cursor` | string
`operationId` | string
`changeKind` | [LibraryChangeKind](LibraryChangeKind.md)
`itemId` | string
`itemKind` | [LibraryItemKind](LibraryItemKind.md)
`acceptedAt` | Date
`acceptedBy` | string
`libraryCatalogVersion` | string
`namespace` | [LibraryNamespaceDto](LibraryNamespaceDto.md)
`content` | [LibraryContentVersionDto](LibraryContentVersionDto.md)
`tombstone` | [LibraryTombstoneDto](LibraryTombstoneDto.md)
`conflict` | [LibraryConflictProvenanceDto](LibraryConflictProvenanceDto.md)

## Example

```typescript
import type { LibraryChangeDto } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "cursor": null,
  "operationId": null,
  "changeKind": null,
  "itemId": null,
  "itemKind": null,
  "acceptedAt": null,
  "acceptedBy": null,
  "libraryCatalogVersion": null,
  "namespace": null,
  "content": null,
  "tombstone": null,
  "conflict": null,
} satisfies LibraryChangeDto

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as LibraryChangeDto
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


