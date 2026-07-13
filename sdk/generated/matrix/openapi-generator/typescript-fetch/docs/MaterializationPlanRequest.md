
# MaterializationPlanRequest

Request contract for server-side Materialization Plan creation.

## Properties

Name | Type
------------ | -------------
`_class` | string
`targetSelector` | [MaterializationTargetSelector](MaterializationTargetSelector.md)
`executionMode` | [MaterializationExecutionMode](MaterializationExecutionMode.md)
`cacheSelection` | [MaterializationCacheSelection](MaterializationCacheSelection.md)
`requestedArtifactKinds` | [Array&lt;MaterializationArtifactKind&gt;](MaterializationArtifactKind.md)
`holderPublicKey` | [ArtifactGrantHolderPublicKey](ArtifactGrantHolderPublicKey.md)

## Example

```typescript
import type { MaterializationPlanRequest } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "_class": MaterializationPlanRequest,
  "targetSelector": null,
  "executionMode": null,
  "cacheSelection": null,
  "requestedArtifactKinds": null,
  "holderPublicKey": null,
} satisfies MaterializationPlanRequest

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as MaterializationPlanRequest
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


