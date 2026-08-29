
# LibraryCatalogChangeResultDto


## Properties

Name | Type
------------ | -------------
`operationId` | string
`outcome` | [LibraryOutcomeKind](LibraryOutcomeKind.md)
`libraryCatalog` | [LibraryCatalogDto](LibraryCatalogDto.md)
`reasonCode` | string
`recordedAt` | Date

## Example

```typescript
import type { LibraryCatalogChangeResultDto } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "operationId": null,
  "outcome": null,
  "libraryCatalog": null,
  "reasonCode": null,
  "recordedAt": null,
} satisfies LibraryCatalogChangeResultDto

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as LibraryCatalogChangeResultDto
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


