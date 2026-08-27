
# SynchronizedNamespaceDto


## Properties

Name | Type
------------ | -------------
`parent` | [SynchronizedParentDto](SynchronizedParentDto.md)
`name` | string
`normalizedPath` | string
`namespaceVersion` | string
`slotVersion` | string

## Example

```typescript
import type { SynchronizedNamespaceDto } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "parent": null,
  "name": null,
  "normalizedPath": null,
  "namespaceVersion": null,
  "slotVersion": null,
} satisfies SynchronizedNamespaceDto

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as SynchronizedNamespaceDto
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


