
# CacheRepositoryScope

Explicit durable repository assignment with its organization needed for authoritative lookup.

## Properties

Name | Type
------------ | -------------
`_class` | string
`organizationId` | string
`repositoryId` | string

## Example

```typescript
import type { CacheRepositoryScope } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "_class": CacheRepositoryScope,
  "organizationId": null,
  "repositoryId": null,
} satisfies CacheRepositoryScope

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as CacheRepositoryScope
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


