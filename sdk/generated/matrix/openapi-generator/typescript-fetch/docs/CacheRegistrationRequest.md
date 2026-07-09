
# CacheRegistrationRequest

Request body used by an approved Grace Cache service to register its endpoint and requested boundary.

## Properties

Name | Type
------------ | -------------
`_class` | string
`endpoint` | string
`requestedScopes` | Array&lt;string&gt;
`requestedCapabilities` | Array&lt;string&gt;
`requestedExecutionModes` | [Array&lt;MaterializationExecutionMode&gt;](MaterializationExecutionMode.md)

## Example

```typescript
import type { CacheRegistrationRequest } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "_class": CacheRegistrationRequest,
  "endpoint": https://cache.example.net/grace,
  "requestedScopes": [repository:owner/org/repo],
  "requestedCapabilities": [read-through, prefetch],
  "requestedExecutionModes": [cachePreferred, cacheRequired],
} satisfies CacheRegistrationRequest

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as CacheRegistrationRequest
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


