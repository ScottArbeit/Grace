
# LibraryContentVersionDto


## Properties

Name | Type
------------ | -------------
`contentVersionId` | string
`blake3Hash` | string
`sha256Hash` | string
`size` | number
`createdAt` | Date

## Example

```typescript
import type { LibraryContentVersionDto } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "contentVersionId": null,
  "blake3Hash": 9a35d91b2f631be9025de753139b88f7b1e71385c412bc3986ff2f38f230841d,
  "sha256Hash": 805331a98813206270e35564769e8bb59eea02aeb7b27c7d6c63e625e1857243,
  "size": null,
  "createdAt": null,
} satisfies LibraryContentVersionDto

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as LibraryContentVersionDto
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


