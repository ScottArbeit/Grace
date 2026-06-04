
# ClaimedReuseRange

Reuse range accepted by the upload-session workflow.

## Properties

Name | Type
------------ | -------------
`storagePoolId` | string
`contentBlockAddress` | string
`ordinalStart` | number
`ordinalCount` | number
`physicalOffset` | number
`physicalLength` | number
`metadataVersion` | number
`claimedAt` | Date

## Example

```typescript
import type { ClaimedReuseRange } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "storagePoolId": null,
  "contentBlockAddress": null,
  "ordinalStart": null,
  "ordinalCount": null,
  "physicalOffset": null,
  "physicalLength": null,
  "metadataVersion": null,
  "claimedAt": null,
} satisfies ClaimedReuseRange

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as ClaimedReuseRange
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


