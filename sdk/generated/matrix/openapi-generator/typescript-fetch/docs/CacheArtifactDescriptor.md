
# CacheArtifactDescriptor


## Properties

Name | Type
------------ | -------------
`repositoryId` | string
`directoryVersionId` | string
`kind` | string
`sha256` | string
`size` | number

## Example

```typescript
import type { CacheArtifactDescriptor } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "repositoryId": null,
  "directoryVersionId": null,
  "kind": null,
  "sha256": null,
  "size": null,
} satisfies CacheArtifactDescriptor

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as CacheArtifactDescriptor
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


