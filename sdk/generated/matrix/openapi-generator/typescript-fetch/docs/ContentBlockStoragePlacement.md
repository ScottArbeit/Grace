
# ContentBlockStoragePlacement

Object-storage placement metadata for a confirmed ContentBlock payload.

## Properties

Name | Type
------------ | -------------
`objectKey` | string
`eTag` | string

## Example

```typescript
import type { ContentBlockStoragePlacement } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "objectKey": null,
  "eTag": null,
} satisfies ContentBlockStoragePlacement

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as ContentBlockStoragePlacement
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


