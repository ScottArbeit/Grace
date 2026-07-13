
# SignedArtifactGrant

ES256 artifact grant envelope returned only for non-Direct materialization.

## Properties

Name | Type
------------ | -------------
`_class` | string
`header` | [ArtifactGrantHeader](ArtifactGrantHeader.md)
`payload` | [ArtifactGrantPayload](ArtifactGrantPayload.md)
`signature` | string

## Example

```typescript
import type { SignedArtifactGrant } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "_class": SignedArtifactGrant,
  "header": null,
  "payload": null,
  "signature": null,
} satisfies SignedArtifactGrant

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as SignedArtifactGrant
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


