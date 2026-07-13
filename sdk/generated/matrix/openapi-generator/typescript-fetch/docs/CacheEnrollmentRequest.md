
# CacheEnrollmentRequest

Administrator-authenticated enrollment for exactly one Owner or Organization and explicit repositories within it.

## Properties

Name | Type
------------ | -------------
`_class` | string
`displayName` | string
`boundaryKind` | [CacheBoundaryKind](CacheBoundaryKind.md)
`ownerId` | string
`organizationId` | string
`repositoryScopes` | [Array&lt;CacheRepositoryScope&gt;](CacheRepositoryScope.md)
`publicKey` | [CacheIdentityPublicKey](CacheIdentityPublicKey.md)
`endpoint` | string
`health` | [CacheHealthStatus](CacheHealthStatus.md)
`softwareVersion` | string
`protocolVersion` | string
`prefetchSupported` | boolean

## Example

```typescript
import type { CacheEnrollmentRequest } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "_class": CacheEnrollmentRequest,
  "displayName": null,
  "boundaryKind": null,
  "ownerId": null,
  "organizationId": null,
  "repositoryScopes": null,
  "publicKey": null,
  "endpoint": null,
  "health": null,
  "softwareVersion": null,
  "protocolVersion": null,
  "prefetchSupported": null,
} satisfies CacheEnrollmentRequest

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as CacheEnrollmentRequest
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


