
# ConfirmedBlockUpload

Confirmed object-storage placement for an uploaded ContentBlock.

## Properties

Name | Type
------------ | -------------
`contentBlockAddress` | string
`payloadLength` | number
`storagePlacement` | [ContentBlockStoragePlacement](ContentBlockStoragePlacement.md)
`ranges` | [Array&lt;ContentBlockMetadataRange&gt;](ContentBlockMetadataRange.md)
`confirmedAt` | Date

## Example

```typescript
import type { ConfirmedBlockUpload } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "contentBlockAddress": null,
  "payloadLength": null,
  "storagePlacement": null,
  "ranges": null,
  "confirmedAt": null,
} satisfies ConfirmedBlockUpload

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as ConfirmedBlockUpload
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


