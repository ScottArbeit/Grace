
# DirectoryVersion


## Properties

Name | Type
------------ | -------------
`_class` | string
`directoryVersionId` | string
`ownerId` | string
`organizationId` | string
`repositoryId` | string
`relativePath` | string
`sha256Hash` | string
`blake3Hash` | string
`directories` | Array&lt;string&gt;
`files` | [Array&lt;FileVersion&gt;](FileVersion.md)
`size` | number
`recursiveSize` | number
`createdAt` | Date
`hashesValidated` | boolean

## Example

```typescript
import type { DirectoryVersion } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "_class": null,
  "directoryVersionId": 33a4e36b-828f-4fae-9343-50b6560dc842,
  "ownerId": 9dd5f81f-dc43-4839-9173-85d09394f30f,
  "organizationId": e35d64a9-b990-44f5-bf02-32ad7d15630c,
  "repositoryId": ab6f35ef-6e01-440b-8f9b-c343a5272095,
  "relativePath": null,
  "sha256Hash": 805331A98813206270E35564769E8BB59EEA02AEB7B27C7D6C63E625E1857243,
  "blake3Hash": 9A35D91B2F631BE9025DE753139B88F7B1E71385C412BC3986FF2F38F230841D,
  "directories": null,
  "files": null,
  "size": null,
  "recursiveSize": null,
  "createdAt": null,
  "hashesValidated": null,
} satisfies DirectoryVersion

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as DirectoryVersion
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


