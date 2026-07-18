
# CacheRegistrationResult

Result returned by Cache registration and refresh routes.

## Properties

Name | Type
------------ | -------------
`_class` | string
`status` | [CacheRegistrationRefreshStatus](CacheRegistrationRefreshStatus.md)
`registration` | [CacheRegistration](CacheRegistration.md)
`message` | string
`retryAfterSeconds` | number

## Example

```typescript
import type { CacheRegistrationResult } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "_class": CacheRegistrationResult,
  "status": null,
  "registration": null,
  "message": Cache registration is current.,
  "retryAfterSeconds": null,
} satisfies CacheRegistrationResult

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as CacheRegistrationResult
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


