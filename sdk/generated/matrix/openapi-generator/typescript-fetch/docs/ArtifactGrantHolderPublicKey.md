
# ArtifactGrantHolderPublicKey

Canonical public half of the client\'s ephemeral P-256 holder key.

## Properties

Name | Type
------------ | -------------
`_class` | string
`algorithm` | string
`curve` | string
`publicKeyX` | string
`publicKeyY` | string

## Example

```typescript
import type { ArtifactGrantHolderPublicKey } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "_class": ArtifactGrantHolderPublicKey,
  "algorithm": null,
  "curve": null,
  "publicKeyX": null,
  "publicKeyY": null,
} satisfies ArtifactGrantHolderPublicKey

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as ArtifactGrantHolderPublicKey
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


