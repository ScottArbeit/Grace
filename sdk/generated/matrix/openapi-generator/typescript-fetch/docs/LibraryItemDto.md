
# LibraryItemDto


## Properties

Name | Type
------------ | -------------
`itemId` | string
`itemKind` | [LibraryItemKind](LibraryItemKind.md)
`state` | string
`lastChangeCursor` | string
`libraryCatalogVersion` | string
`namespace` | [LibraryNamespaceDto](LibraryNamespaceDto.md)
`content` | [LibraryContentVersionDto](LibraryContentVersionDto.md)
`tombstone` | [LibraryTombstoneDto](LibraryTombstoneDto.md)

## Example

```typescript
import type { LibraryItemDto } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "itemId": null,
  "itemKind": null,
  "state": null,
  "lastChangeCursor": null,
  "libraryCatalogVersion": null,
  "namespace": null,
  "content": null,
  "tombstone": null,
} satisfies LibraryItemDto

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as LibraryItemDto
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


