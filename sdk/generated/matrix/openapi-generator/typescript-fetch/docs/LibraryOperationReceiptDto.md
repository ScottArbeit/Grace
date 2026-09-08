
# LibraryOperationReceiptDto


## Properties

Name | Type
------------ | -------------
`operationId` | string
`requestHash` | string
`outcome` | [LibraryOutcomeKind](LibraryOutcomeKind.md)
`change` | [LibraryChangeDto](LibraryChangeDto.md)
`reasonCode` | string
`currentLibraryCatalog` | [LibraryCatalogDto](LibraryCatalogDto.md)
`rebaseline` | [LibraryRebaselineDto](LibraryRebaselineDto.md)

## Example

```typescript
import type { LibraryOperationReceiptDto } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "operationId": null,
  "requestHash": null,
  "outcome": null,
  "change": null,
  "reasonCode": null,
  "currentLibraryCatalog": null,
  "rebaseline": null,
} satisfies LibraryOperationReceiptDto

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as LibraryOperationReceiptDto
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


