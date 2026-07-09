
# ArtifactGrantHeader

Canonical header for one signed artifact grant.

## Properties

Name | Type
------------ | -------------
`_class` | string
`algorithm` | string
`keyId` | string

## Example

```typescript
import type { ArtifactGrantHeader } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "_class": ArtifactGrantHeader,
  "algorithm": null,
  "keyId": null,
} satisfies ArtifactGrantHeader

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as ArtifactGrantHeader
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


