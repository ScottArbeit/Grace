
# CacheRepositoryAssignmentRequest


## Properties

Name | Type
------------ | -------------
`_class` | string
`cacheId` | string
`repositoryScopes` | [Array&lt;CacheRepositoryScope&gt;](CacheRepositoryScope.md)

## Example

```typescript
import type { CacheRepositoryAssignmentRequest } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "_class": CacheRepositoryAssignmentRequest,
  "cacheId": null,
  "repositoryScopes": null,
} satisfies CacheRepositoryAssignmentRequest

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as CacheRepositoryAssignmentRequest
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


