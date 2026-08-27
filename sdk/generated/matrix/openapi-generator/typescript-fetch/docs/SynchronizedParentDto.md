
# SynchronizedParentDto

Repository-owned root or directory parent identity.

## Properties

Name | Type
------------ | -------------
`kind` | string
`rootPath` | string
`itemId` | string

## Example

```typescript
import type { SynchronizedParentDto } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "kind": null,
  "rootPath": null,
  "itemId": null,
} satisfies SynchronizedParentDto

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as SynchronizedParentDto
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


