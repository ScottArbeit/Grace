
# CacheKeyRotationRequest


## Properties

Name | Type
------------ | -------------
`_class` | string
`cacheId` | string
`newPublicKey` | [CacheIdentityPublicKey](CacheIdentityPublicKey.md)
`proof` | [SignedCacheRequestProof](SignedCacheRequestProof.md)

## Example

```typescript
import type { CacheKeyRotationRequest } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "_class": CacheKeyRotationRequest,
  "cacheId": null,
  "newPublicKey": null,
  "proof": null,
} satisfies CacheKeyRotationRequest

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as CacheKeyRotationRequest
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


