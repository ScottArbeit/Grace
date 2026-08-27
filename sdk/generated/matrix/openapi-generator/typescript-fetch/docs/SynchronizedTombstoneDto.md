
# SynchronizedTombstoneDto


## Properties

Name | Type
------------ | -------------
`itemId` | string
`itemKind` | [SynchronizedItemKind](SynchronizedItemKind.md)
`deletedAt` | Date
`deletedBy` | string
`deleteCursor` | string
`lastNamespaceVersion` | string
`lastContentVersionId` | string

## Example

```typescript
import type { SynchronizedTombstoneDto } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "itemId": null,
  "itemKind": null,
  "deletedAt": null,
  "deletedBy": null,
  "deleteCursor": null,
  "lastNamespaceVersion": null,
  "lastContentVersionId": null,
} satisfies SynchronizedTombstoneDto

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as SynchronizedTombstoneDto
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


