
# MaterializationPlan

Resolved immutable target root and artifacts required to materialize it.

## Properties

Name | Type
------------ | -------------
`_class` | string
`targetRootDirectoryVersionId` | string
`executionMode` | [MaterializationExecutionMode](MaterializationExecutionMode.md)
`cacheSelection` | [MaterializationCacheSelection](MaterializationCacheSelection.md)
`requiredArtifacts` | [Array&lt;MaterializationArtifactDescriptor&gt;](MaterializationArtifactDescriptor.md)
`artifactGrant` | [SignedArtifactGrant](SignedArtifactGrant.md)

## Example

```typescript
import type { MaterializationPlan } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "_class": MaterializationPlan,
  "targetRootDirectoryVersionId": 33a4e36b-828f-4fae-9343-50b6560dc842,
  "executionMode": null,
  "cacheSelection": null,
  "requiredArtifacts": null,
  "artifactGrant": null,
} satisfies MaterializationPlan

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as MaterializationPlan
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


