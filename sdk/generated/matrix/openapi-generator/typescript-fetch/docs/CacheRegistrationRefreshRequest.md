
# CacheRegistrationRefreshRequest

Cache-authenticated refresh that may update operational facts only.

## Properties

Name | Type
------------ | -------------
`_class` | string
`cacheId` | string
`endpoint` | string
`health` | [CacheHealthStatus](CacheHealthStatus.md)
`softwareVersion` | string
`protocolVersion` | string
`prefetchSupported` | boolean
`observedAt` | Date
`proof` | [SignedCacheRequestProof](SignedCacheRequestProof.md)

## Example

```typescript
import type { CacheRegistrationRefreshRequest } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "_class": CacheRegistrationRefreshRequest,
  "cacheId": null,
  "endpoint": null,
  "health": null,
  "softwareVersion": null,
  "protocolVersion": null,
  "prefetchSupported": null,
  "observedAt": null,
  "proof": null,
} satisfies CacheRegistrationRefreshRequest

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as CacheRegistrationRefreshRequest
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


