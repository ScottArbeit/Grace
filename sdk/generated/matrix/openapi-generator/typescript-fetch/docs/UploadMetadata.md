
# UploadMetadata

Upload metadata returned by whole-file compatibility upload planning.

## Properties

Name | Type
------------ | -------------
`relativePath` | string
`blobUriWithSasToken` | string
`sha256Hash` | string
`contentReference` | [FileContentReference](FileContentReference.md)

## Example

```typescript
import type { UploadMetadata } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "relativePath": null,
  "blobUriWithSasToken": null,
  "sha256Hash": 805331A98813206270E35564769E8BB59EEA02AEB7B27C7D6C63E625E1857243,
  "contentReference": null,
} satisfies UploadMetadata

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as UploadMetadata
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


