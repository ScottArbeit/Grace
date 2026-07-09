
# ArtifactGrantValidationKey

Public P-256 validation key used by Grace Cache to verify signed artifact grants.

## Properties

Name | Type
------------ | -------------
`_class` | string
`keyId` | string
`algorithm` | string
`createdAt` | Date
`notBefore` | Date
`expiresAt` | Date
`publicKeyX` | string
`publicKeyY` | string

## Example

```typescript
import type { ArtifactGrantValidationKey } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "_class": ArtifactGrantValidationKey,
  "keyId": agk-example,
  "algorithm": ES256,
  "createdAt": null,
  "notBefore": null,
  "expiresAt": null,
  "publicKeyX": null,
  "publicKeyY": null,
} satisfies ArtifactGrantValidationKey

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as ArtifactGrantValidationKey
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


