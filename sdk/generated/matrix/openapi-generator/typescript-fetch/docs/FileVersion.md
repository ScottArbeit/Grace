
# FileVersion


## Properties

Name | Type
------------ | -------------
`_class` | string
`relativePath` | string
`sha256Hash` | string
`blake3Hash` | string
`isBinary` | boolean
`size` | number
`createdAt` | Date
`blobUri` | string
`contentReference` | [FileContentReference](FileContentReference.md)

## Example

```typescript
import type { FileVersion } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "_class": null,
  "relativePath": null,
  "sha256Hash": 805331a98813206270e35564769e8bb59eea02aeb7b27c7d6c63e625e1857243,
  "blake3Hash": 9a35d91b2f631be9025de753139b88f7b1e71385c412bc3986ff2f38f230841d,
  "isBinary": null,
  "size": null,
  "createdAt": null,
  "blobUri": null,
  "contentReference": null,
} satisfies FileVersion

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as FileVersion
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


