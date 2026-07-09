
# CacheRegistration

Server-owned active registration record for an approved Grace Cache service.

## Properties

Name | Type
------------ | -------------
`_class` | string
`servicePrincipalId` | string
`endpoint` | string
`approvedScopes` | Array&lt;string&gt;
`approvedCapabilities` | Array&lt;string&gt;
`approvedExecutionModes` | [Array&lt;MaterializationExecutionMode&gt;](MaterializationExecutionMode.md)
`registeredAt` | Date
`lastRefreshedAt` | Date
`refreshAfter` | Date
`expiresAt` | Date
`readThroughEnabled` | boolean
`prefetchEnabled` | boolean

## Example

```typescript
import type { CacheRegistration } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "_class": CacheRegistration,
  "servicePrincipalId": cache-service-client,
  "endpoint": https://cache.example.net/grace,
  "approvedScopes": null,
  "approvedCapabilities": null,
  "approvedExecutionModes": null,
  "registeredAt": null,
  "lastRefreshedAt": null,
  "refreshAfter": null,
  "expiresAt": null,
  "readThroughEnabled": null,
  "prefetchEnabled": null,
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


