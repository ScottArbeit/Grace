
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
  "sha256Hash": 805331A98813206270E35564769E8BB59EEA02AEB7B27C7D6C63E625E1857243,
  "blake3Hash": 9A35D91B2F631BE9025DE753139B88F7B1E71385C412BC3986FF2F38F230841D,
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


