
# SynchronizedItemDto


## Properties

Name | Type
------------ | -------------
`itemId` | string
`itemKind` | [SynchronizedItemKind](SynchronizedItemKind.md)
`state` | string
`lastMutationCursor` | string
`rootConfigurationVersion` | string
`namespace` | [SynchronizedNamespaceDto](SynchronizedNamespaceDto.md)
`content` | [SynchronizedContentVersionDto](SynchronizedContentVersionDto.md)
`tombstone` | [SynchronizedTombstoneDto](SynchronizedTombstoneDto.md)

## Example

```typescript
import type { SynchronizedItemDto } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "itemId": null,
  "itemKind": null,
  "state": null,
  "lastMutationCursor": null,
  "rootConfigurationVersion": null,
  "namespace": null,
  "content": null,
  "tombstone": null,
} satisfies SynchronizedItemDto

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as SynchronizedItemDto
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


