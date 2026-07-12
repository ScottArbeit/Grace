
# MaterializationArtifactSource

Fetchable or deferred source for one planned artifact.

## Properties

Name | Type
------------ | -------------
`_class` | string
`sourceKind` | [MaterializationArtifactSourceKind](MaterializationArtifactSourceKind.md)
`directUri` | string
`cacheKey` | string
`cacheEndpoint` | string
`cacheServicePrincipalId` | string
`directFallbackUri` | string

## Example

```typescript
import type { MaterializationArtifactSource } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "_class": MaterializationArtifactSource,
  "sourceKind": null,
  "directUri": null,
  "cacheKey": null,
  "cacheEndpoint": null,
  "cacheServicePrincipalId": null,
  "directFallbackUri": null,
} satisfies MaterializationArtifactSource

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as MaterializationArtifactSource
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


