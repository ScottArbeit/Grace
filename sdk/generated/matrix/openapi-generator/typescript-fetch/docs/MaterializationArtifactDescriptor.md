
# MaterializationArtifactDescriptor

Stable artifact identity required to materialize a resolved target root.

## Properties

Name | Type
------------ | -------------
`_class` | string
`artifactKind` | [MaterializationArtifactKind](MaterializationArtifactKind.md)
`canonicalArtifactIdentity` | string
`representedRootDirectoryVersionId` | string
`targetRootDirectoryVersionId` | string
`sizeInBytes` | number
`relativePath` | string
`sha256Hash` | string
`blake3Hash` | string
`manifestAddress` | string
`contentBlockAddress` | string
`storagePoolId` | string
`source` | [MaterializationArtifactSource](MaterializationArtifactSource.md)

## Example

```typescript
import type { MaterializationArtifactDescriptor } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "_class": MaterializationArtifactDescriptor,
  "artifactKind": null,
  "canonicalArtifactIdentity": null,
  "representedRootDirectoryVersionId": 33a4e36b-828f-4fae-9343-50b6560dc842,
  "targetRootDirectoryVersionId": 33a4e36b-828f-4fae-9343-50b6560dc842,
  "sizeInBytes": null,
  "relativePath": null,
  "sha256Hash": 805331a98813206270e35564769e8bb59eea02aeb7b27c7d6c63e625e1857243,
  "blake3Hash": 9a35d91b2f631be9025de753139b88f7b1e71385c412bc3986ff2f38f230841d,
  "manifestAddress": null,
  "contentBlockAddress": null,
  "storagePoolId": null,
  "source": null,
} satisfies MaterializationArtifactDescriptor

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as MaterializationArtifactDescriptor
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


