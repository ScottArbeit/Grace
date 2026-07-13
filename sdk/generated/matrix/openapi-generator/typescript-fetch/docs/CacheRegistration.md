
# CacheRegistration

Durable Cache registration with immutable CacheId, explicit repository assignments, and no private key material.

## Properties

Name | Type
------------ | -------------
`_class` | string
`cacheId` | string
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
`enrolledBy` | string
`enrolledAt` | Date
`lastRefreshedAt` | Date
`refreshAfter` | Date
`expiresAt` | Date
`rotationDueAt` | Date
`revokedAt` | Date

## Example

```typescript
import type { CacheRegistration } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "_class": CacheRegistration,
  "cacheId": null,
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
  "enrolledBy": null,
  "enrolledAt": null,
  "lastRefreshedAt": null,
  "refreshAfter": null,
  "expiresAt": null,
  "rotationDueAt": null,
  "revokedAt": null,
} satisfies CacheRegistration

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as CacheRegistration
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


