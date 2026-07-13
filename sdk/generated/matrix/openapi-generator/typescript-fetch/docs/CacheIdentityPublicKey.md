
# CacheIdentityPublicKey

Canonical P-256 public key for a Cache identity. Private key material is never accepted or returned.

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
import type { CacheIdentityPublicKey } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "_class": CacheIdentityPublicKey,
  "algorithm": null,
  "curve": null,
  "publicKeyX": null,
  "publicKeyY": null,
} satisfies CacheIdentityPublicKey

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as CacheIdentityPublicKey
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


