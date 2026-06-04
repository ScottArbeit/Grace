
# ContentBlockMetadataRange

Physical range metadata for ContentBlock reconstruction, reuse, and compaction.

## Properties

Name | Type
------------ | -------------
`ordinalStart` | number
`ordinalCount` | number
`activeManifestCount` | number
`physicalOffset` | number
`physicalLength` | number

## Example

```typescript
import type { ContentBlockMetadataRange } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "ordinalStart": null,
  "ordinalCount": null,
  "activeManifestCount": null,
  "physicalOffset": null,
  "physicalLength": null,
} satisfies ContentBlockMetadataRange

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as ContentBlockMetadataRange
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


