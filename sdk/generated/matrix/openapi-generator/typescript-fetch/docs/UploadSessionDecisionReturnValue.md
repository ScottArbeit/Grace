
# UploadSessionDecisionReturnValue

Successful upload-session command response.

## Properties

Name | Type
------------ | -------------
`eventTime` | Date
`correlationId` | string
`properties` | { [key: string]: any; }
`returnValue` | [UploadSessionDecision](UploadSessionDecision.md)

## Example

```typescript
import type { UploadSessionDecisionReturnValue } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "eventTime": null,
  "correlationId": null,
  "properties": null,
  "returnValue": null,
} satisfies UploadSessionDecisionReturnValue

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as UploadSessionDecisionReturnValue
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


