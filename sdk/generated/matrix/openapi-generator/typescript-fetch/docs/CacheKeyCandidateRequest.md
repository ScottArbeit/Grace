
# CacheKeyCandidateRequest

Active-key-proven submission of the one candidate Cache identity key. The candidate key promotes itself through a later refresh proof.

## Properties

Name | Type
------------ | -------------
`_class` | string
`cacheId` | string
`candidatePublicKey` | [CacheIdentityPublicKey](CacheIdentityPublicKey.md)
`rotationIntervalMinutes` | number
`isStartup` | boolean
`proof` | [SignedCacheRequestProof](SignedCacheRequestProof.md)

## Example

```typescript
import type { CacheKeyCandidateRequest } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "_class": CacheKeyCandidateRequest,
  "cacheId": null,
  "candidatePublicKey": null,
  "rotationIntervalMinutes": null,
  "isStartup": null,
  "proof": null,
} satisfies CacheKeyCandidateRequest

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as CacheKeyCandidateRequest
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


