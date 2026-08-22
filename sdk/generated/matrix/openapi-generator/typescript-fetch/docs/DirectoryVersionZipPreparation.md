
# DirectoryVersionZipPreparation


## Properties

Name | Type
------------ | -------------
`descriptor` | [CacheArtifactDescriptor](CacheArtifactDescriptor.md)
`permit` | string
`permitExpiresAt` | Date
`redemptionBytes` | string

## Example

```typescript
import type { DirectoryVersionZipPreparation } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "descriptor": null,
  "permit": null,
  "permitExpiresAt": null,
  "redemptionBytes": null,
} satisfies DirectoryVersionZipPreparation

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as DirectoryVersionZipPreparation
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


