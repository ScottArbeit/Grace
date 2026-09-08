
# LibraryChangeDto


## Properties

Name | Type
------------ | -------------
`operationId` | string
`changeKind` | [LibraryChangeKind](LibraryChangeKind.md)
`acceptedAt` | Date
`acceptedBy` | string
`libraryCatalogVersion` | string
`item` | [LibraryItemDto](LibraryItemDto.md)
`conflict` | [LibraryConflictProvenanceDto](LibraryConflictProvenanceDto.md)

## Example

```typescript
import type { LibraryChangeDto } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "operationId": null,
  "changeKind": null,
  "acceptedAt": null,
  "acceptedBy": null,
  "libraryCatalogVersion": null,
  "item": null,
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


