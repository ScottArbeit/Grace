
# LibraryBootstrapPageDto


## Properties

Name | Type
------------ | -------------
`bootstrapId` | string
`boundaryCursor` | string
`cursorEpoch` | string
`libraryCatalog` | [LibraryCatalogDto](LibraryCatalogDto.md)
`items` | [Array&lt;LibraryItemDto&gt;](LibraryItemDto.md)
`nextPageToken` | string

## Example

```typescript
import type { LibraryBootstrapPageDto } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "bootstrapId": null,
  "boundaryCursor": null,
  "cursorEpoch": null,
  "libraryCatalog": null,
  "items": null,
  "nextPageToken": null,
} satisfies LibraryBootstrapPageDto

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as LibraryBootstrapPageDto
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


