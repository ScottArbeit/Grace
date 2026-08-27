
# CacheArtifactGrantValidationKey


## Properties

Name | Type
------------ | -------------
`issuer` | string
`audience` | string
`algorithm` | string
`keyId` | string
`publicJwk` | [P256PublicJwk](P256PublicJwk.md)

## Example

```typescript
import type { CacheArtifactGrantValidationKey } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "issuer": null,
  "audience": null,
  "algorithm": null,
  "keyId": null,
  "publicJwk": null,
} satisfies CacheArtifactGrantValidationKey

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as CacheArtifactGrantValidationKey
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


