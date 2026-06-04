
# BlockUploadIntent

Registered intent to upload a logical ContentBlock range.

## Properties

Name | Type
------------ | -------------
`contentBlockAddress` | string
`logicalOffset` | number
`logicalLength` | number
`expectedPayloadLength` | number
`registeredAt` | Date

## Example

```typescript
import type { BlockUploadIntent } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "contentBlockAddress": null,
  "logicalOffset": null,
  "logicalLength": null,
  "expectedPayloadLength": null,
  "registeredAt": null,
} satisfies BlockUploadIntent

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as BlockUploadIntent
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


