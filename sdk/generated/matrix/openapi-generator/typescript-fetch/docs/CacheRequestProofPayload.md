
# CacheRequestProofPayload

Canonical Cache proof binding the cache id, operation, canonical request digest, and Unix-millisecond timestamp.

## Properties

Name | Type
------------ | -------------
`_class` | string
`cacheId` | string
`operation` | string
`requestDigest` | string
`issuedAt` | Date

## Example

```typescript
import type { CacheRequestProofPayload } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "_class": CacheRequestProofPayload,
  "cacheId": null,
  "operation": null,
  "requestDigest": null,
  "issuedAt": null,
} satisfies CacheRequestProofPayload

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as CacheRequestProofPayload
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


