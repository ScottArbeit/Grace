
# ContentBlockReuseRangeHint

Server-issued hint used to claim a reusable ContentBlock range.

## Properties

Name | Type
------------ | -------------
`storagePoolId` | string
`manifestAddress` | string
`contentBlockAddress` | string
`ordinalStart` | number
`ordinalCount` | number
`metadataVersion` | number
`protectedChunkAddresses` | Array&lt;string&gt;

## Example

```typescript
import type { ContentBlockReuseRangeHint } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "storagePoolId": null,
  "manifestAddress": null,
  "contentBlockAddress": null,
  "ordinalStart": null,
  "ordinalCount": null,
  "metadataVersion": null,
  "protectedChunkAddresses": null,
} satisfies ContentBlockReuseRangeHint

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as ContentBlockReuseRangeHint
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


