
# SynchronizedNamespaceSlotDto


## Properties

Name | Type
------------ | -------------
`parent` | [SynchronizedParentDto](SynchronizedParentDto.md)
`name` | string
`normalizedPath` | string
`slotVersion` | string
`state` | string
`occupantItemId` | string

## Example

```typescript
import type { SynchronizedNamespaceSlotDto } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "parent": null,
  "name": null,
  "normalizedPath": null,
  "slotVersion": null,
  "state": null,
  "occupantItemId": null,
} satisfies SynchronizedNamespaceSlotDto

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as SynchronizedNamespaceSlotDto
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


